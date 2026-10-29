using Openhold.Core.Configuration;
using Openhold.Core.Contracts;
using Openhold.Server.Browsing;
using Openhold.Server.Configuration;
using Openhold.Server.Data;
using Openhold.Server.Erp;
using Openhold.Server.Identity;
using Openhold.Server.Items;

namespace Openhold.Server.Api;

/// <summary>The item master (catalogue): universal part numbers, manufacturer parts, cross-references and linked files.</summary>
public static class ItemApi
{
    public static void MapItemApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/items");
        var writes = api.MapGroup("").AddEndpointFilter<RequireWritableServer>();

        writes.MapPost("", async (SaveItemRequest body, HttpContext http, ActorResolver actors, ItemService items, FolderGuard guard, CancellationToken ct) =>
        {
            await RequireReadableFilesAsync(body, guard, ct);
            return await HideFilesAsync(await items.CreateAsync(await actors.ResolveAsync(http), body, ct), guard, ct);
        });
        writes.MapPut("/{id:guid}", async (Guid id, SaveItemRequest body, HttpContext http, ActorResolver actors, ItemService items, FolderGuard guard, CancellationToken ct) =>
        {
            await RequireReadableFilesAsync(body, guard, ct);
            // Links to files the person can't see were never shown to them, so keep them rather than drop them.
            var linked = (await items.GetAsync(id, ct)).Files;
            var visible = await guard.ReadableAsync(linked.Select(f => f.FileId).ToList(), ct);
            body = body with { Files = [.. body.Files ?? [], .. linked.Where(f => !visible.Contains(f.FileId))] };
            return await HideFilesAsync(await items.UpdateAsync(await actors.ResolveAsync(http), id, body, ct), guard, ct);
        });
        writes.MapPost("/{id:guid}/state", async (Guid id, ItemStateRequest body, HttpContext http, ActorResolver actors, ItemService items, FolderGuard guard, CancellationToken ct) =>
            await HideFilesAsync(await items.ChangeStateAsync(await actors.ResolveAsync(http), id, body, ct), guard, ct));

        api.MapGet("/{id:guid}", async (Guid id, ItemService items, FolderGuard guard, CancellationToken ct) =>
            await HideFilesAsync(await items.GetAsync(id, ct), guard, ct));
        api.MapGet("/by-number/{number}", async (string number, ItemService items, FolderGuard guard, CancellationToken ct) =>
            await HideFilesAsync(await items.GetByNumberAsync(number, ct), guard, ct));
        api.MapPost("/search", (ItemSearchRequest body, ItemService items, CancellationToken ct) => items.SearchAsync(body, ct));
        api.MapPost("/duplicates", (DuplicateCheckRequest body, ItemService items, CancellationToken ct) => items.FindDuplicatesAsync(body, ct));

        // ERP connections (configured on the server): status, test, send everything, retry, and one item's status.
        api.MapGet("/{id:guid}/erp", (Guid id, VaultInstance vault, ErpSyncService erp, CancellationToken ct) => erp.ForItemAsync(vault, id, ct));
        var erpApi = app.MapGroup("/api/erp");
        erpApi.MapGet("/connections", async (HttpContext http, ConfigurationService configuration, VaultInstance vault, ErpSyncService erp, CancellationToken ct) =>
        {
            await RequireAsync(http, configuration, ct, Privileges.ManageConfiguration, Privileges.PartLibrarian);
            return await erp.ListAsync(vault, ct);
        });
        // Setting connections up (administrators): the kinds there are, each connection's settings (never its secrets).
        erpApi.MapGet("/kinds", async (HttpContext http, ConfigurationService configuration, ErpConnections connections, CancellationToken ct) =>
        {
            await RequireAsync(http, configuration, ct, Privileges.ManageConfiguration);
            return connections.Kinds;
        });
        erpApi.MapGet("/setup", async (HttpContext http, ConfigurationService configuration, VaultInstance vault, ErpConnections connections, CancellationToken ct) =>
        {
            await RequireAsync(http, configuration, ct, Privileges.ManageConfiguration);
            return await connections.DetailsAsync(vault, ct);
        });
        var erpWrites = erpApi.MapGroup("").AddEndpointFilter<RequireWritableServer>();
        erpWrites.MapPut("/setup/{id}", async (string id, SaveErpConnectionRequest body, HttpContext http, ConfigurationService configuration, ActorResolver actors,
            VaultInstance vault, ErpConnections connections, CancellationToken ct) =>
        {
            await RequireAsync(http, configuration, ct, Privileges.ManageConfiguration);
            return await connections.SaveAsync(vault, await actors.ResolveAsync(http), id, body, ct);
        });
        erpWrites.MapDelete("/setup/{id}", async (string id, HttpContext http, ConfigurationService configuration, ActorResolver actors,
            VaultInstance vault, ErpConnections connections, CancellationToken ct) =>
        {
            await RequireAsync(http, configuration, ct, Privileges.ManageConfiguration);
            await connections.DeleteAsync(vault, await actors.ResolveAsync(http), id, ct);
            return Results.NoContent();
        });
        erpWrites.MapPost("/connections/{id}/test", async (string id, HttpContext http, ConfigurationService configuration, VaultInstance vault, ErpSyncService erp, CancellationToken ct) =>
        {
            await RequireAsync(http, configuration, ct, Privileges.ManageConfiguration);
            await erp.TestAsync(vault, id, ct);
            return Results.NoContent();
        });
        erpWrites.MapPost("/connections/{id}/send-all", async (string id, HttpContext http, ConfigurationService configuration, VaultInstance vault, ErpSyncService erp, CancellationToken ct) =>
        {
            await RequireAsync(http, configuration, ct, Privileges.ManageConfiguration);
            return Results.Ok(new { queued = await erp.QueueAllAsync(vault, id, ct) });
        });
        erpWrites.MapPost("/send-now", async (HttpContext http, ConfigurationService configuration, VaultInstance vault, ErpSyncService erp, CancellationToken ct) =>
        {
            await RequireAsync(http, configuration, ct, Privileges.ManageConfiguration, Privileges.PartLibrarian);
            await erp.RetryFailedAsync(vault, ct);
            return Results.Ok(new { sent = await erp.SendDueAsync(vault, ct) });
        });

        app.MapGet("/api/files/{id:guid}/items", async (Guid id, ItemService items, FolderGuard guard, CancellationToken ct) =>
        {
            await guard.RequireFilesAsync([id], FolderAccess.Read, ct);
            return await items.ForFileAsync(id, ct);
        });
    }

    private static async Task RequireAsync(HttpContext http, ConfigurationService configuration, CancellationToken ct, params string[] privileges)
    {
        var current = (await configuration.GetAsync(ct)).Configuration;
        var username = ActorResolver.NormalizeUsername(http.User.Identity?.Name) ?? "";
        if (!privileges.Any(p => current.HasPrivilege(username, p)))
            throw new OpenholdException(StatusCodes.Status403Forbidden, "not_allowed", $"This needs the {string.Join(" or ", privileges)} privilege.");
    }

    /// <summary>A file can only be linked by someone who may read it.</summary>
    private static Task RequireReadableFilesAsync(SaveItemRequest body, FolderGuard guard, CancellationToken ct) =>
        guard.RequireFilesAsync((body.Files ?? []).Select(f => f.FileId), FolderAccess.Read, ct);

    /// <summary>Linked files in folders hidden from the person are left out.</summary>
    private static async Task<ItemInfo> HideFilesAsync(ItemInfo item, FolderGuard guard, CancellationToken ct) =>
        item with { Files = await guard.FilterAsync(item.Files, f => f.FolderPath ?? "$", ct) };
}
