using Openhold.Core.Configuration;
using Openhold.Core.Content;
using Openhold.Core.Contracts;
using Openhold.Server.Browsing;
using Openhold.Server.Configuration;
using Openhold.Server.Lifecycles;
using Openhold.Server.Data;
using Openhold.Server.Files;
using Openhold.Server.CheckIn;
using Openhold.Server.Identity;
using Openhold.Server.Jobs;
using Openhold.Server.Locking;
using Openhold.Server.Numbering;
using Openhold.Server.Replication;

namespace Openhold.Server.Api;

public static class VaultApi
{
    /// <summary>Largest chunk the server accepts; matches <see cref="FastCdc.DefaultMaxSize"/>.</summary>
    private const int MaxChunkBytes = FastCdc.DefaultMaxSize;

    private const int MaxFetchChunks = 1024;

    public static void MapVaultApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        // Everything that changes the vault goes through this group, so a backup server can refuse it in one place.
        var writes = api.MapGroup("").AddEndpointFilter<RequireWritableServer>();

        // Chunks: clients ask which hashes the site lacks, upload only those, then check in.
        api.MapPost("/chunks/missing", async (IReadOnlyList<string> hashes, IChunkStore store, CancellationToken ct) =>
        {
            var parsed = hashes.Select(h => ContentHash.TryParse(h, out var hash)
                ? hash
                : throw OpenholdException.BadRequest("invalid_hash", $"'{h}' is not a content hash.")).ToList();
            var missing = await store.FindMissingAsync(parsed, ct);
            return missing.Select(h => h.ToString());
        });

        // Upload: a stream of frames. Every frame is verified against its hash before it is stored.
        writes.MapPost("/chunks/upload", async (HttpRequest request, IChunkStore store, CancellationToken ct) =>
        {
            var stored = 0;
            try
            {
                await foreach (var (hash, data) in ChunkFrames.ReadAsync(request.Body, MaxChunkBytes, ct))
                {
                    await store.PutAsync(hash, data, ct);
                    stored++;
                }
            }
            catch (ChunkIntegrityException ex)
            {
                throw OpenholdException.BadRequest("chunk_hash_mismatch", ex.Message);
            }
            catch (FormatException ex)
            {
                throw OpenholdException.BadRequest("invalid_frames", ex.Message);
            }
            return Results.Ok(new { stored });
        });

        // Download: ask for a list of hashes, receive their frames in the same order.
        api.MapPost("/chunks/fetch", async (IReadOnlyList<string> hashes, HttpContext http, IChunkStore store, FolderGuard guard,
            VaultInstance vault, ContentReplicator replicator, CancellationToken ct) =>
        {
            if (hashes.Count > MaxFetchChunks)
                throw OpenholdException.BadRequest("too_many_chunks", $"Fetch at most {MaxFetchChunks} chunks per request.");
            var parsed = hashes.Select(h => ContentHash.TryParse(h, out var hash)
                ? hash
                : throw OpenholdException.BadRequest("invalid_hash", $"'{h}' is not a content hash.")).ToList();
            await guard.RequireChunksAsync(parsed, ct);

            // Content checked in at another site that hasn't arrived yet: fetch it from there first, so a Get works now.
            var missing = await store.FindMissingAsync(parsed, ct);
            if (missing.Count > 0)
            {
                await replicator.FetchAsync(vault, missing, null, ct);
                missing = await store.FindMissingAsync(parsed, ct);
            }

            // Fail before streaming starts, while a proper error response can still be sent.
            if (missing.Count > 0)
                throw OpenholdException.NotFound("Some chunks are not at this site.", new { missing = missing.Select(h => h.ToString()) });

            http.Response.ContentType = "application/octet-stream";
            foreach (var hash in parsed)
                await ChunkFrames.WriteAsync(http.Response.Body, hash, await store.GetAsync(hash, ct), ct);
        });

        // The vaults this server hosts (e.g. Production, Sandbox), for choosing one when setting up a PC.
        // Readable before signing in: a new PC is set up by choosing one.
        api.MapGet("/vaults", (VaultRegistry registry) => registry.Vaults.Select(v => v.Name).ToList()).AllowAnonymous();

        // Browsing (local replica). Folder permissions hide what a person may not read (FolderGuard).
        api.MapGet("/folders", async (string? path, VaultBrowser browser, FolderGuard guard, SyncingStatus syncing, CancellationToken ct) =>
        {
            var listing = await guard.FilterAsync(await browser.ListAsync(path, ct), ct);
            return listing with { Files = await syncing.MarkAsync(listing.Files, ct) };
        });
        writes.MapPost("/folders", async (CreateFolderRequest body, VaultBrowser browser, FolderGuard guard, CancellationToken ct) =>
        {
            await guard.RequireFolderAsync(body.Path, FolderAccess.Change, ct);
            return await browser.EnsureFolderAsync(body.Path, ct);
        });
        writes.MapDelete("/folders", async (string path, HttpContext http, ActorResolver actors, VaultBrowser browser, FolderGuard guard, CancellationToken ct) =>
        {
            await guard.RequireFolderAsync(path, FolderAccess.Change, ct);
            return await browser.DeleteEmptyFolderAsync(await actors.ResolveAsync(http), path, ct);
        });
        api.MapPost("/search", async (SearchRequest body, VaultSearch search, FolderGuard guard, SyncingStatus syncing, CancellationToken ct) =>
        {
            var results = await search.SearchAsync(body, ct);
            var hits = await guard.FilterAsync(results.Hits, h => h.FolderPath, ct);
            var marked = (await syncing.MarkAsync(hits.Select(h => h.File).ToList(), ct)).ToDictionary(f => f.Id);
            return results with { Hits = hits.Select(h => h with { File = marked[h.File.Id] }).ToList() };
        });
        api.MapPost("/files/resolve", async (ResolvePathsRequest body, VaultBrowser browser, FolderGuard guard, SyncingStatus syncing, CancellationToken ct) =>
        {
            var resolved = await browser.ResolvePathsAsync(body.Paths, ct);
            var readable = await guard.ReadableAsync(resolved.Where(r => r.File is not null).Select(r => r.File!.Id).ToList(), ct);
            var marked = (await syncing.MarkAsync(resolved.Where(r => r.File is not null && readable.Contains(r.File.Id)).Select(r => r.File!).DistinctBy(f => f.Id).ToList(), ct))
                .ToDictionary(f => f.Id);
            return resolved.Select(r => r with { File = r.File is { } file && marked.TryGetValue(file.Id, out var shown) ? shown : null }).ToList();
        });
        api.MapGet("/files/{id:guid}", async (Guid id, VaultBrowser browser, FolderGuard guard, SyncingStatus syncing, CancellationToken ct) =>
        {
            await guard.RequireFilesAsync([id], FolderAccess.Read, ct);
            return await syncing.MarkAsync(await browser.GetFileAsync(id, ct), ct);
        });

        // Sites and how up to date each one's copy of the files is.
        api.MapGet("/sites", async (OpenholdDb db, Microsoft.Extensions.Options.IOptions<OpenholdOptions> options, CancellationToken ct) =>
        {
            await using var connection = await db.Local.OpenConnectionAsync(ct);
            return await ContentReplicator.StatusAsync(connection, options.Value.SiteId);
        });
        // Fetch everything this site lacks now, rather than at the next round (administrators; works on backup servers too).
        api.MapPost("/sites/catch-up", async (HttpContext http, ConfigurationService configuration, VaultInstance vault, ContentReplicator replicator, CancellationToken ct) =>
        {
            var current = (await configuration.GetAsync(ct)).Configuration;
            if (!current.HasPrivilege(ActorResolver.NormalizeUsername(http.User.Identity?.Name) ?? "", Privileges.ManageConfiguration))
                throw new OpenholdException(StatusCodes.Status403Forbidden, "not_allowed", "Only administrators can start replication by hand.");
            await replicator.RegisterAsync(vault, ct);
            return Results.Ok(new { arrived = await replicator.CatchUpAsync(vault, ct) });
        });
        api.MapGet("/files/{id:guid}/history", async (Guid id, VaultBrowser browser, FolderGuard guard, CancellationToken ct) =>
        {
            await guard.RequireFilesAsync([id], FolderAccess.Read, ct);
            return await browser.GetHistoryAsync(id, ct);
        });
        api.MapGet("/files/{id:guid}/uses", async (Guid id, int? version, VaultBrowser browser, FolderGuard guard, CancellationToken ct) =>
        {
            await guard.RequireFilesAsync([id], FolderAccess.Read, ct);
            return await guard.FilterAsync(await browser.GetUsesAsync(id, version, ct), r => r.Path, ct);
        });
        api.MapGet("/files/{id:guid}/uses/all", async (Guid id, VaultBrowser browser, FolderGuard guard, CancellationToken ct) =>
        {
            await guard.RequireFilesAsync([id], FolderAccess.Read, ct);
            return await guard.FilterAsync(await browser.GetAllUsesAsync(id, ct), r => r.Path, ct);
        });
        api.MapGet("/files/{id:guid}/where-used", async (Guid id, VaultBrowser browser, FolderGuard guard, CancellationToken ct) =>
        {
            await guard.RequireFilesAsync([id], FolderAccess.Read, ct);
            return await guard.FilterAsync(await browser.GetWhereUsedAsync(id, ct), r => r.Path, ct);
        });
        api.MapGet("/files/{id:guid}/thumbnail", async (Guid id, int? version, VaultBrowser browser, FolderGuard guard, CancellationToken ct) =>
        {
            await guard.RequireFilesAsync([id], FolderAccess.Read, ct);
            return await browser.GetThumbnailAsync(id, version, ct) is { } png ? Results.Bytes(png, "image/png") : Results.NoContent();
        });
        api.MapGet("/files/{id:guid}/manifest", async (Guid id, int? version, VaultBrowser browser, FolderGuard guard, CancellationToken ct) =>
        {
            await guard.RequireFilesAsync([id], FolderAccess.Read, ct);
            var manifest = await browser.GetManifestAsync(id, version, ct);
            await guard.GrantAsync(manifest, ct);
            return Results.Bytes(manifest, "application/octet-stream");
        });

        // What a person may do in a folder, and why: your own (for menus), or anyone's for administrators and auditors.
        api.MapGet("/permissions/explain", async (string path, string? username, HttpContext http, ConfigurationService configuration, CancellationToken ct) =>
        {
            var current = (await configuration.GetAsync(ct)).Configuration;
            var me = ActorResolver.NormalizeUsername(http.User.Identity?.Name)!;
            var who = ActorResolver.NormalizeUsername(username) ?? me;
            if (!string.Equals(who, me, StringComparison.OrdinalIgnoreCase)
                && !new[] { Privileges.ManageConfiguration, Privileges.ManageUsers, Privileges.ViewAudit }.Any(p => current.HasPrivilege(me, p)))
                throw new OpenholdException(StatusCodes.Status403Forbidden, "not_allowed", "Only administrators and auditors can see other people's access.");
            var normalized = VaultBrowser.NormalizePath(path);
            var (access, reason) = current.ExplainAccess(who, normalized);
            return new FolderAccessInfo(normalized, who, access, reason);
        });

        // Locks and check-in (authority writes).
        writes.MapPost("/locks/checkout", async (FileIdsRequest body, HttpContext http, ActorResolver actors, LockService locks, FolderGuard guard, CancellationToken ct) =>
        {
            await guard.RequireFilesAsync(body.FileIds, FolderAccess.Change, ct);
            var result = await locks.CheckOutAsync(await actors.ResolveAsync(http), body.FileIds, ct);
            return result.Succeeded ? Results.Ok(result) : Results.Conflict(result);
        });

        // Undoing your own check-out only needs read access, so losing change access later never strands a lock.
        writes.MapPost("/locks/undo", async (FileIdsRequest body, HttpContext http, ActorResolver actors, LockService locks, FolderGuard guard, CancellationToken ct) =>
        {
            await guard.RequireFilesAsync(body.FileIds, FolderAccess.Read, ct);
            return await locks.UndoCheckOutAsync(await actors.ResolveAsync(http), body.FileIds, ct);
        });

        api.MapPost("/locks/query", async (FileIdsRequest body, LockService locks, FolderGuard guard, CancellationToken ct) =>
            await locks.GetLocksAsync([.. await guard.ReadableAsync(body.FileIds, ct)], ct));

        writes.MapPost("/files/{id:guid}/restore", async (Guid id, RestoreRequest body, HttpContext http, ActorResolver actors, CheckInService checkIn, FolderGuard guard, CancellationToken ct) =>
        {
            await guard.RequireFilesAsync([id], FolderAccess.Change, ct);
            return await checkIn.RestoreAsync(await actors.ResolveAsync(http), id, body, ct);
        });

        // Fills in a preview a version was checked in without; never replaces one.
        writes.MapPut("/files/{id:guid}/thumbnail", async (Guid id, int version, HttpRequest request, CheckInService checkIn, FolderGuard guard, CancellationToken ct) =>
        {
            await guard.RequireFilesAsync([id], FolderAccess.Read, ct);
            using var body = new MemoryStream();
            await request.Body.CopyToAsync(body, ct);
            return Results.Ok(new { updated = await checkIn.AddMissingThumbnailAsync(id, version, body.ToArray(), ct) });
        });

        writes.MapPost("/files/delete", async (FileIdsRequest body, HttpContext http, ActorResolver actors, FileManagementService files, FolderGuard guard, CancellationToken ct) =>
        {
            await guard.RequireFilesAsync(body.FileIds, FolderAccess.Change, ct);
            return await files.DeleteAsync(await actors.ResolveAsync(http), body.FileIds, ct);
        });
        writes.MapPost("/trash/restore", async (FileIdsRequest body, HttpContext http, ActorResolver actors, FileManagementService files, FolderGuard guard, CancellationToken ct) =>
        {
            await guard.RequireFilesAsync(body.FileIds, FolderAccess.Change, ct);
            return await files.RestoreFromTrashAsync(await actors.ResolveAsync(http), body.FileIds, ct);
        });
        api.MapGet("/trash", async (FileManagementService files, FolderGuard guard, CancellationToken ct) =>
            // Only what the person could restore: the trash of folders they may change (administrators see it all).
            await guard.FilterAsync(await files.ListTrashAsync(ct), t => t.FolderPath, ct, FolderAccess.Change));

        writes.MapPost("/checkin", async (CheckInRequest body, HttpContext http, ActorResolver actors, CheckInService checkIn, FolderGuard guard, CancellationToken ct) =>
        {
            await guard.RequireFilesAsync(body.Files.Where(f => f.FileId is not null).Select(f => f.FileId!.Value), FolderAccess.Change, ct);
            await guard.RequireFolderIdsAsync(body.Files.Where(f => f.FolderId is not null).Select(f => f.FolderId!.Value), FolderAccess.Change, ct);
            // A move is a write to the destination as well as the source.
            await guard.RequireFolderIdsAsync(body.Files.Where(f => f.NewFolderId is not null).Select(f => f.NewFolderId!.Value), FolderAccess.Change, ct);
            return await checkIn.CheckInAsync(await actors.ResolveAsync(http), body, ct);
        });

        // Configuration (categories, lifecycles, revision schemes, groups): everyone reads it, administrators save it.
        api.MapGet("/config", (ConfigurationService configuration, CancellationToken ct) => configuration.GetAsync(ct));
        api.MapGet("/config/history", (ConfigurationService configuration, CancellationToken ct) => configuration.HistoryAsync(ct));
        api.MapGet("/config/versions/{version:int}", (int version, ConfigurationService configuration, CancellationToken ct) => configuration.GetVersionAsync(version, ct));
        api.MapGet("/users", (ConfigurationService configuration, CancellationToken ct) => configuration.UsersAsync(ct));
        writes.MapPut("/config", async (SaveConfigurationRequest body, HttpContext http, ActorResolver actors, ConfigurationService configuration, CancellationToken ct) =>
            await configuration.SaveAsync(await actors.ResolveAsync(http), body, ct));

        // Lifecycles: state changes, category changes, and change requests waiting for approval.
        writes.MapPost("/lifecycle/change-state", async (ChangeStateRequest body, HttpContext http, ActorResolver actors, LifecycleService lifecycle, FolderGuard guard, CancellationToken ct) =>
        {
            await guard.RequireFilesAsync(body.FileIds, FolderAccess.Change, ct);
            return await lifecycle.ChangeStateAsync(await actors.ResolveAsync(http), body, ct);
        });
        writes.MapPost("/lifecycle/change-category", async (ChangeCategoryRequest body, HttpContext http, ActorResolver actors, LifecycleService lifecycle, FolderGuard guard, CancellationToken ct) =>
        {
            await guard.RequireFilesAsync(body.FileIds, FolderAccess.Change, ct);
            return await lifecycle.ChangeCategoryAsync(await actors.ResolveAsync(http), body, ct);
        });
        api.MapGet("/change-requests", async (HttpContext http, ActorResolver actors, LifecycleService lifecycle, CancellationToken ct) =>
            await lifecycle.ListOpenAsync(await actors.ResolveAsync(http), ct));
        api.MapGet("/change-requests/{id:guid}", async (Guid id, HttpContext http, ActorResolver actors, LifecycleService lifecycle, CancellationToken ct) =>
            await lifecycle.GetAsync(await actors.ResolveAsync(http), id, ct));
        writes.MapPost("/change-requests/{id:guid}/approve", async (Guid id, ApprovalRequest body, HttpContext http, ActorResolver actors, LifecycleService lifecycle, CancellationToken ct) =>
            await lifecycle.ApproveAsync(await actors.ResolveAsync(http), id, body.Comment, ct));
        writes.MapPost("/change-requests/{id:guid}/reject", async (Guid id, ApprovalRequest body, HttpContext http, ActorResolver actors, LifecycleService lifecycle, CancellationToken ct) =>
            await lifecycle.RejectAsync(await actors.ResolveAsync(http), id, body.Comment, ct));
        writes.MapPost("/change-requests/{id:guid}/cancel", async (Guid id, HttpContext http, ActorResolver actors, LifecycleService lifecycle, CancellationToken ct) =>
            await lifecycle.CancelAsync(await actors.ResolveAsync(http), id, ct));

        // Jobs: job workers claim work (e.g. publishing PDFs on release) and hand back the result.
        writes.MapPost("/jobs/claim", async (ClaimJobRequest body, HttpContext http, ActorResolver actors, JobService jobs, Microsoft.Extensions.Options.IOptions<OpenholdOptions> options, CancellationToken ct) =>
        {
            var baseUrl = options.Value.PublicUrl is { Length: > 0 } configured
                ? configured.TrimEnd('/') + http.Request.PathBase
                : $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}";
            return await jobs.ClaimAsync(await actors.ResolveAsync(http), body.Kinds, baseUrl, ct) is { } job ? Results.Ok(job) : Results.NoContent();
        });
        writes.MapPost("/jobs/{id:long}/complete", async (long id, CompleteJobRequest body, HttpContext http, ActorResolver actors, JobService jobs, CancellationToken ct) =>
        {
            await jobs.CompleteAsync(await actors.ResolveAsync(http), id, body, ct);
            return Results.NoContent();
        });
        writes.MapPost("/jobs/{id:long}/fail", async (long id, FailJobRequest body, HttpContext http, ActorResolver actors, JobService jobs, CancellationToken ct) =>
        {
            await jobs.FailAsync(await actors.ResolveAsync(http), id, body, ct);
            return Results.NoContent();
        });
        // A job names its file, so people only see jobs on files they may read.
        api.MapGet("/jobs", async (string? status, JobService jobs, FolderGuard guard, CancellationToken ct) =>
        {
            var list = await jobs.ListAsync(status, ct);
            var readable = await guard.ReadableAsync(list.Select(j => j.FileId).Distinct().ToList(), ct);
            return list.Where(j => readable.Contains(j.FileId)).ToList();
        });
        api.MapGet("/files/{id:guid}/pdf", async (Guid id, int? version, JobService jobs, FolderGuard guard, CancellationToken ct) =>
        {
            await guard.RequireFilesAsync([id], FolderAccess.Read, ct);
            if (await jobs.GetPdfAsync(id, version, ct) is not { } pdf)
                return Results.NotFound();
            await guard.GrantAsync(pdf.Manifest, ct);
            return Results.Ok(pdf);
        });

        // What a released PDF's QR code opens. Anyone may ask (phones on the shop floor aren't signed in).
        app.MapGet("/verify/{fileId:guid}/{version:int}", async (Guid fileId, int version, JobService jobs, CancellationToken ct) =>
            Results.Content(await jobs.VerifyPageAsync(fileId, version, ct), "text/html; charset=utf-8")).AllowAnonymous();

        // Numbering.
        api.MapGet("/numbering/schemes", (NumberingService numbering, CancellationToken ct) => numbering.ListSchemesAsync(ct));
        writes.MapPut("/numbering/schemes/{id}", async (string id, NumberingScheme body, HttpContext http, ActorResolver actors, NumberingService numbering, CancellationToken ct) =>
            await numbering.SaveSchemeAsync(await actors.ResolveAsync(http), body with { Id = id }, ct));
        writes.MapDelete("/numbering/schemes/{id}", async (string id, HttpContext http, ActorResolver actors, NumberingService numbering, CancellationToken ct) =>
        {
            await numbering.DeleteSchemeAsync(await actors.ResolveAsync(http), id, ct);
            return Results.NoContent();
        });
        api.MapPost("/numbering/schemes/{id}/preview", async (string id, ReserveNumbersRequest body, HttpContext http, ActorResolver actors, NumberingService numbering, CancellationToken ct) =>
            await numbering.PreviewAsync(await actors.ResolveAsync(http), id, body.Fields, ct));
        writes.MapPost("/numbering/schemes/{id}/reserve", async (string id, ReserveNumbersRequest body, HttpContext http, ActorResolver actors, NumberingService numbering, CancellationToken ct) =>
            await numbering.ReserveAsync(await actors.ResolveAsync(http), id, body.Count, body.Fields, ct));
        writes.MapPost("/numbering/release", async (ReleaseNumbersRequest body, HttpContext http, ActorResolver actors, NumberingService numbering, CancellationToken ct) =>
            await numbering.ReleaseAsync(await actors.ResolveAsync(http), body.Numbers, ct));
    }
}
