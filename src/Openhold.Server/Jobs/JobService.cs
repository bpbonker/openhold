using System.Net;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Openhold.Core.Configuration;
using Openhold.Core.Content;
using Openhold.Core.Contracts;
using Openhold.Server.Configuration;
using Openhold.Server.Data;
using Openhold.Server.Identity;

namespace Openhold.Server.Jobs;

/// <summary>
/// The job queue: work done after the fact by job workers, e.g. publishing a stamped PDF when a drawing is
/// released. A job never holds up the change that queued it. Workers claim one job at a time; a job whose
/// worker disappears is handed out again, and one that keeps failing stops after a few attempts.
/// </summary>
public sealed class JobService(OpenholdDb db, IChunkStore chunkStore, IOptions<OpenholdOptions> options)
{
    public const string Pdf = "pdf";

    /// <summary>The group whose members (besides administrators) may run jobs.</summary>
    public const string WorkersGroup = "job-workers";

    public const int MaxAttempts = 3;

    /// <summary>A job running longer than this is assumed abandoned (its worker crashed or lost the network) and is handed out again.</summary>
    public static readonly TimeSpan Abandoned = TimeSpan.FromMinutes(30);

    /// <summary>Drawings and documents whose release publishes a PDF.</summary>
    public static bool Publishable(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() is ".dwg" or ".idw" or ".pdf";

    /// <summary>Queues a job, once: queuing the same work again while it's waiting does nothing.</summary>
    internal static Task QueueAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string kind, Guid fileId, int version, Guid queuedBy) =>
        connection.ExecuteAsync("""
            INSERT INTO jobs (kind, file_id, version, queued_by) VALUES (@kind, @fileId, @version, @queuedBy)
            ON CONFLICT (kind, file_id, version) WHERE status IN ('queued', 'running') DO NOTHING
            """, new { kind, fileId, version, queuedBy }, transaction);

    private sealed record ClaimedRow(long Id, string Kind, Guid FileId, int Version, int Attempts, string FileName, string FolderPath,
        string PropertiesJson, string? CategoryId, string? State, DateTime ChangedAt);

    /// <summary>Hands the oldest waiting job of the given kinds to the caller, or null if there's nothing to do.</summary>
    public async Task<JobInfo?> ClaimAsync(Actor actor, IReadOnlyList<string> kinds, string baseUrl, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.Primary.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var configuration = await RequireWorkerAsync(connection, transaction, actor);

        // A job whose worker vanished on its last attempt won't be handed out again: record it as failed.
        await connection.ExecuteAsync("""
            UPDATE jobs SET status = 'failed', finished_at = now(),
                last_error = COALESCE(last_error || ' ', '') || 'The job worker stopped responding.'
            WHERE status = 'running' AND started_at < now() - @abandoned AND attempts >= @maxAttempts
            """, new { abandoned = Abandoned, maxAttempts = MaxAttempts }, transaction);

        // SKIP LOCKED lets several workers claim at once without queuing behind each other.
        var row = await connection.QuerySingleOrDefaultAsync<ClaimedRow>("""
            WITH next AS (
                SELECT id FROM jobs
                WHERE kind = ANY(@kinds) AND attempts < @maxAttempts
                  AND (status = 'queued' OR (status = 'running' AND started_at < now() - @abandoned))
                ORDER BY created_at
                LIMIT 1
                FOR UPDATE SKIP LOCKED
            )
            UPDATE jobs j SET status = 'running', attempts = j.attempts + 1, started_at = now(), worker = @worker
            FROM next, files f, folders fo, file_versions v
            WHERE j.id = next.id AND f.id = j.file_id AND fo.id = f.folder_id AND v.file_id = j.file_id AND v.version = j.version
            RETURNING j.id AS Id, j.kind AS Kind, j.file_id AS FileId, j.version AS Version, j.attempts AS Attempts,
                      f.name AS FileName, fo.path AS FolderPath, (v.vault_properties || v.properties)::text AS PropertiesJson,
                      f.category_id AS CategoryId, v.state AS State, v.created_at AS ChangedAt
            """, new { kinds = kinds.ToArray(), maxAttempts = MaxAttempts, abandoned = Abandoned, worker = $"{actor.Username} @ {actor.Machine}" }, transaction);
        await transaction.CommitAsync(cancellationToken);
        if (row is null)
            return null;

        var properties = JsonSerializer.Deserialize<Dictionary<string, string>>(row.PropertiesJson) ?? [];
        return new JobInfo(row.Id, row.Kind, row.FileId, row.Version, row.FileName, row.FolderPath,
            properties.GetValueOrDefault("Revision"), configuration.StateOf(row.CategoryId, row.State)?.Name, row.ChangedAt,
            $"{baseUrl.TrimEnd('/')}/verify/{row.FileId}/{row.Version}", row.Attempts);
    }

    /// <summary>Stores the job's output (already uploaded to the chunk store) with the version, and marks the job done.</summary>
    public async Task CompleteAsync(Actor actor, long jobId, CompleteJobRequest request, CancellationToken cancellationToken = default)
    {
        ChunkManifest manifest;
        try
        {
            manifest = ManifestCodec.Decode(request.Manifest);
        }
        catch (FormatException ex)
        {
            throw OpenholdException.BadRequest("invalid_manifest", ex.Message);
        }
        var missing = await chunkStore.FindMissingAsync(manifest.DistinctHashes(), cancellationToken);
        if (missing.Count > 0)
            throw OpenholdException.Conflict("missing_chunks", $"{missing.Count} chunk(s) must be uploaded first.", new { missing = missing.Select(h => h.ToString()) });

        await using var connection = await db.Primary.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await RequireWorkerAsync(connection, transaction, actor);
        var job = await LockRunningAsync(connection, transaction, jobId, request.Attempt);

        await connection.ExecuteAsync("""
            INSERT INTO renditions (file_id, version, kind, manifest, length, content_hash)
            VALUES (@fileId, @version, @kind, @manifest, @length, @hash)
            ON CONFLICT (file_id, version, kind) DO UPDATE SET manifest = EXCLUDED.manifest, length = EXCLUDED.length,
                content_hash = EXCLUDED.content_hash, created_at = now()
            """, new { job.FileId, job.Version, job.Kind, manifest = request.Manifest, length = manifest.Length, hash = manifest.FileHash.Bytes.ToArray() }, transaction);
        await connection.ExecuteAsync(
            "INSERT INTO content_presence (content_hash, site_id) VALUES (@hash, @siteId) ON CONFLICT DO NOTHING",
            new { hash = manifest.FileHash.Bytes.ToArray(), siteId = actor.SiteId }, transaction);
        await connection.ExecuteAsync(
            "UPDATE jobs SET status = 'done', finished_at = now(), last_error = NULL WHERE id = @jobId", new { jobId }, transaction);
        await connection.ExecuteAsync("""
            INSERT INTO audit_events (user_id, site_id, action, file_id, data)
            VALUES (@userId, @siteId, 'published', @fileId, jsonb_build_object('kind', @kind::text, 'version', @version))
            """, new { userId = actor.UserId, siteId = actor.SiteId, job.FileId, job.Kind, job.Version }, transaction);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Records a failure. The job is tried again (up to <see cref="MaxAttempts"/>) unless retrying can't help.</summary>
    public async Task FailAsync(Actor actor, long jobId, FailJobRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.Primary.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await RequireWorkerAsync(connection, transaction, actor);
        await LockRunningAsync(connection, transaction, jobId, request.Attempt);
        await connection.ExecuteAsync("""
            UPDATE jobs SET last_error = @error, finished_at = now(),
                status = CASE WHEN @retry AND attempts < @maxAttempts THEN 'queued' ELSE 'failed' END
            WHERE id = @jobId
            """, new { jobId, error = Truncate(request.Error, 2000), retry = request.Retry, maxAttempts = MaxAttempts }, transaction);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Recent jobs, newest first: what's waiting, running, done or failed (and why).</summary>
    public async Task<IReadOnlyList<JobStatus>> ListAsync(string? status, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.Local.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<JobStatus>("""
            SELECT j.id AS Id, j.kind AS Kind, j.file_id AS FileId, f.name AS FileName, j.version AS Version, j.status AS Status,
                   j.attempts AS Attempts, j.last_error AS LastError, j.worker AS Worker, j.created_at AS CreatedAt, j.finished_at AS FinishedAt
            FROM jobs j JOIN files f ON f.id = j.file_id
            WHERE @status::text IS NULL OR j.status = @status
            ORDER BY j.created_at DESC
            LIMIT 500
            """, new { status })).ToList();
    }

    /// <summary>The newest version of a file that has a published PDF, or null if none has been published yet.</summary>
    public async Task<PublishedPdf?> GetPdfAsync(Guid fileId, int? version, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.Local.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<PublishedPdf>("""
            SELECT file_id AS FileId, version AS Version, length AS Length, manifest AS Manifest
            FROM renditions
            WHERE file_id = @fileId AND kind = 'pdf' AND (@version::int IS NULL OR version = @version)
            ORDER BY version DESC
            LIMIT 1
            """, new { fileId, version });
    }

    private sealed record RunningJob(Guid FileId, int Version, string Kind, string Status, int Attempts);

    /// <summary>
    /// The job, if it's still running under the claim the caller holds. A worker that took too long, and whose job
    /// was handed to another worker since, can't complete or fail it from under the new one.
    /// </summary>
    private static async Task<RunningJob> LockRunningAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long jobId, int attempt)
    {
        var job = await connection.QuerySingleOrDefaultAsync<RunningJob>(
            "SELECT file_id AS FileId, version AS Version, kind AS Kind, status AS Status, attempts AS Attempts FROM jobs WHERE id = @jobId FOR UPDATE",
            new { jobId }, transaction)
            ?? throw OpenholdException.NotFound($"There is no job {jobId}.");
        if (job.Status != "running" || job.Attempts != attempt)
            throw OpenholdException.Conflict("job_not_yours", $"Job {jobId} is no longer yours to finish (it is {job.Status}, attempt {job.Attempts}).");
        return job;
    }

    private async Task<VaultConfiguration> RequireWorkerAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Actor actor)
    {
        var configuration = (await ConfigurationService.LoadAsync(connection, transaction, options.Value)).Configuration;
        if (!configuration.HasPrivilege(actor.Username, Privileges.RunJobs) && !configuration.IsMember(actor.Username, WorkersGroup))
            throw new OpenholdException(StatusCodes.Status403Forbidden, "not_a_job_worker", $"Only members of the '{WorkersGroup}' group (or administrators) can run jobs.");
        return configuration;
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];

    /// <summary>
    /// What the QR code on a printed PDF opens: whether that printout is still the current release. Anyone can
    /// open it (shop-floor phones aren't signed in), so it says only what the printout itself already shows:
    /// the file name, its revision and state. The link names the file and version by id, which can't be guessed.
    /// </summary>
    public async Task<string> VerifyPageAsync(Guid fileId, int version, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.Local.OpenConnectionAsync(cancellationToken);
        var configuration = (await ConfigurationService.LoadAsync(connection, null, options.Value)).Configuration;
        var versions = (await connection.QueryAsync<(int Version, string? State, string PropertiesJson, DateTime CreatedAt, string Name, string? Category, DateTime? Deleted, string Hash)>("""
            SELECT v.version, v.state, (v.vault_properties || v.properties)::text, v.created_at, f.name, f.category_id, f.deleted_at,
                   encode(v.content_hash, 'hex')
            FROM file_versions v JOIN files f ON f.id = v.file_id
            WHERE v.file_id = @fileId
            ORDER BY v.version DESC
            """, new { fileId })).ToList();
        var printed = versions.FirstOrDefault(v => v.Version == version);
        if (printed.Name is null)
            return Page("Not recognised", "#6B7280", "This code doesn't match any drawing in this vault.", null);

        string Revision(string json) => (JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []).GetValueOrDefault("Revision") ?? "(none)";
        var printedRevision = Revision(printed.PropertiesJson);
        var name = WebUtility.HtmlEncode(printed.Name);
        var latest = versions[0];
        var latestState = configuration.StateOf(latest.Category, latest.State);
        var latestRelease = versions.FirstOrDefault(v => configuration.StateOf(v.Category, v.State)?.Released == true);
        var details = $"{name}<br>Revision {WebUtility.HtmlEncode(printedRevision)}, released {printed.CreatedAt:d MMM yyyy}";

        if (printed.Deleted is not null)
            return Page("Withdrawn", "#B3261E", "This drawing has been removed from the vault. Don't use this printout.", details);
        if (latestState?.Obsolete == true)
            return Page("Obsolete", "#B3261E", "This drawing is obsolete. Don't use this printout.", details);
        // Releases record a new version with the same content, so "the same release" means the same content and the
        // same revision (a revision can change without the content, e.g. one kept only in the vault).
        if (latestRelease.Name is null || latestRelease.Hash != printed.Hash || Revision(latestRelease.PropertiesJson) != printedRevision)
        {
            var current = latestRelease.Name is null ? "" : $" The current release is revision {WebUtility.HtmlEncode(Revision(latestRelease.PropertiesJson))}.";
            return Page("Superseded", "#B3261E", $"This printout is out of date.{current} Get the current drawing before using it.", details);
        }
        var note = latestState is { Released: false } ? $" A new revision is being worked on ({WebUtility.HtmlEncode(latestState.Name)}), but this is still the current release." : "";
        return Page("Current", "#1E7B34", $"This is the current release.{note}", details);
    }

    private static string Page(string verdict, string colour, string message, string? details) => $$"""
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{{verdict}}: Openhold drawing check</title>
        <style>
          body { font-family: system-ui, sans-serif; margin: 0; padding: 24px; background: #fff; color: #111; }
          .verdict { font-size: 2.2rem; font-weight: 700; color: {{colour}}; margin: 0 0 12px; }
          p { font-size: 1.1rem; line-height: 1.5; margin: 0 0 16px; }
          .details { color: #444; border-top: 1px solid #ddd; padding-top: 12px; }
          .checked { color: #888; font-size: 0.85rem; }
        </style></head>
        <body>
          <div class="verdict">{{verdict}}</div>
          <p>{{message}}</p>
          {{(details is null ? "" : $"<p class=\"details\">{details}</p>")}}
          <p class="checked">Checked {{DateTime.UtcNow:d MMM yyyy HH:mm}} UTC against the vault.</p>
        </body></html>
        """;
}
