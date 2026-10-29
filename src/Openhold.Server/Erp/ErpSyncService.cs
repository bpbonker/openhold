using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Openhold.Core.Contracts;
using Openhold.Server.Configuration;
using Openhold.Server.Data;
using Openhold.Server.Items;

namespace Openhold.Server.Erp;

/// <summary>
/// Keeps each ERP connection's copy of released catalogue items up to date. Saving or releasing an item marks its
/// link to every connection 'pending' in the same transaction (so a change is never forgotten); this service, on the
/// authority, sends pending items and retries failures with growing gaps, up to six hours apart.
/// </summary>
public sealed class ErpSyncService(VaultRegistry vaults, IOptions<OpenholdOptions> options, ErpConnections erpConnections, ILogger<ErpSyncService> logger)
    : BackgroundService
{
    private const int BatchSize = 50;

    private readonly SemaphoreSlim _running = new(1, 1);

    /// <summary>Marks the item as needing to be sent to every connection. Call inside the transaction that changed it.</summary>
    public static Task QueueAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, IReadOnlyList<ErpConnectionOptions> connections, Guid itemId) =>
        connections.Count == 0 ? Task.CompletedTask : connection.ExecuteAsync("""
            INSERT INTO item_erp_links (item_id, connection_id) SELECT @itemId, unnest(@connections)
            ON CONFLICT (item_id, connection_id) DO UPDATE SET status = 'pending', attempts = 0, next_attempt_at = now(), updated_at = now()
            """, new { itemId, connections = connections.Select(c => c.Id).ToArray() }, transaction);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            foreach (var vault in vaults.Vaults)
            {
                try
                {
                    await SendDueAsync(vault, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Sending items to ERPs failed in vault {Vault}; will retry", vault.Name);
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Sends every item that is due, in batches. Returns how many were sent.</summary>
    public async Task<int> SendDueAsync(VaultInstance vault, CancellationToken cancellationToken)
    {
        Dictionary<string, ErpConnectionOptions> connections;
        await using (var lookup = await vault.Db.Primary.OpenConnectionAsync(cancellationToken))
            connections = (await erpConnections.ForVaultAsync(vault, lookup, withSecrets: true)).ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        if (connections.Count == 0)
            return 0;
        await _running.WaitAsync(cancellationToken);
        try
        {
            var sent = 0;
            while (true)
            {
                await using var connection = await vault.Db.Primary.OpenConnectionAsync(cancellationToken);
                var due = (await connection.QueryAsync<(Guid ItemId, string ConnectionId, string? ExternalId, int Attempts)>("""
                    SELECT item_id, connection_id, external_id, attempts FROM item_erp_links
                    WHERE status IN ('pending', 'failed') AND next_attempt_at <= now() AND connection_id = ANY(@connections)
                    ORDER BY next_attempt_at LIMIT @limit
                    """, new { connections = connections.Keys.ToArray(), limit = BatchSize })).ToList();
                if (due.Count == 0)
                    return sent;

                var rules = (await ConfigurationService.LoadAsync(connection, null, options.Value)).Configuration;
                foreach (var link in due)
                {
                    var settings = connections[link.ConnectionId];
                    var item = await ItemService.LoadAsync(connection, rules, link.ItemId);
                    try
                    {
                        var connector = erpConnections.Connector(settings.Kind)
                            ?? throw new ErpException($"There is no '{settings.Kind}' ERP connector; use csv or simpro.");
                        var externalId = await connector.PushAsync(settings, item!, link.ExternalId, cancellationToken);
                        await connection.ExecuteAsync("""
                            UPDATE item_erp_links SET external_id = @externalId, sent_version = @version, attempts = 0, last_error = NULL, updated_at = now(),
                                status = CASE WHEN (SELECT row_version FROM items WHERE id = @itemId) > @version THEN 'pending' ELSE 'done' END
                            WHERE item_id = @itemId AND connection_id = @connectionId
                            """, new { externalId, version = item!.RowVersion, link.ItemId, link.ConnectionId });
                        sent++;
                    }
                    // An ERP that stalls past the HTTP timeout throws TaskCanceledException: that's a failed send, not a shutdown.
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        var wait = TimeSpan.FromMinutes(Math.Min(360, Math.Pow(2, link.Attempts)));
                        logger.LogWarning("Couldn't send {Number} to {Connection} (attempt {Attempt}): {Message}", item?.Number, settings.Name, link.Attempts + 1, ex.Message);
                        await connection.ExecuteAsync("""
                            UPDATE item_erp_links SET status = 'failed', attempts = attempts + 1, last_error = @error,
                                next_attempt_at = now() + @wait, updated_at = now()
                            WHERE item_id = @itemId AND connection_id = @connectionId
                            """, new { error = ex is ErpException or HttpRequestException ? ex.Message
                                : ex is OperationCanceledException ? "The ERP didn't answer in time." : $"{ex.GetType().Name}: {ex.Message}", wait, link.ItemId, link.ConnectionId });
                    }
                }
            }
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>How many items are waiting, failed and sent, per connection.</summary>
    public async Task<IReadOnlyList<ErpConnectionInfo>> ListAsync(VaultInstance vault, CancellationToken cancellationToken)
    {
        await using var connection = await vault.Db.Primary.OpenConnectionAsync(cancellationToken);
        var connections = await erpConnections.ForVaultAsync(vault, connection, enabledOnly: false);
        var counts = (await connection.QueryAsync<(string ConnectionId, string Status, int Count)>(
            "SELECT connection_id, status, count(*)::int FROM item_erp_links GROUP BY connection_id, status")).ToList();
        int Count(string id, string status) => counts.Where(c => c.ConnectionId == id && c.Status == status).Sum(c => c.Count);
        return connections.Select(c => new ErpConnectionInfo(c.Id, c.Name, c.Kind, Count(c.Id, "pending"), Count(c.Id, "failed"), Count(c.Id, "done"))).ToList();
    }

    public async Task TestAsync(VaultInstance vault, string connectionId, CancellationToken cancellationToken)
    {
        ErpConnectionOptions settings;
        await using (var lookup = await vault.Db.Primary.OpenConnectionAsync(cancellationToken))
            settings = (await erpConnections.ForVaultAsync(vault, lookup, enabledOnly: false, withSecrets: true)).FirstOrDefault(c => c.Id == connectionId)
                ?? throw OpenholdException.NotFound($"There is no ERP connection '{connectionId}'.");
        var connector = erpConnections.Connector(settings.Kind)
            ?? throw OpenholdException.BadRequest("unknown_connector", $"There is no '{settings.Kind}' ERP connector; use csv or simpro.");
        try
        {
            await connector.TestAsync(settings, cancellationToken);
        }
        catch (Exception ex) when (ex is ErpException or HttpRequestException or IOException or UnauthorizedAccessException)
        {
            throw OpenholdException.BadRequest("erp_test_failed", ex.Message);
        }
    }

    /// <summary>Queues every released and obsolete item for one connection, e.g. after adding it. Returns how many.</summary>
    public async Task<int> QueueAllAsync(VaultInstance vault, string connectionId, CancellationToken cancellationToken)
    {
        await using var connection = await vault.Db.Primary.OpenConnectionAsync(cancellationToken);
        if (!(await erpConnections.ForVaultAsync(vault, connection)).Any(c => c.Id == connectionId))
            throw OpenholdException.NotFound($"There is no switched-on ERP connection '{connectionId}'.");
        return await connection.ExecuteAsync("""
            INSERT INTO item_erp_links (item_id, connection_id)
            SELECT id, @connectionId FROM items WHERE state IN ('Released', 'Obsolete')
            ON CONFLICT (item_id, connection_id) DO UPDATE SET status = 'pending', attempts = 0, next_attempt_at = now(), updated_at = now()
            """, new { connectionId });
    }

    /// <summary>Retries failed items now instead of waiting for their next attempt.</summary>
    public async Task<int> RetryFailedAsync(VaultInstance vault, CancellationToken cancellationToken)
    {
        await using var connection = await vault.Db.Primary.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync("UPDATE item_erp_links SET next_attempt_at = now() WHERE status = 'failed'");
    }

    /// <summary>Where one item stands in each ERP.</summary>
    public async Task<IReadOnlyList<ItemErpStatus>> ForItemAsync(VaultInstance vault, Guid itemId, CancellationToken cancellationToken)
    {
        await using var connection = await vault.Db.Local.OpenConnectionAsync(cancellationToken);
        var names = (await erpConnections.ForVaultAsync(vault, connection, enabledOnly: false)).ToDictionary(c => c.Id, c => c.Name);
        return (await connection.QueryAsync<(string ConnectionId, string? ExternalId, string Status, int Attempts, string? LastError, DateTime UpdatedAt)>("""
            SELECT connection_id, external_id, status, attempts, last_error, updated_at FROM item_erp_links WHERE item_id = @itemId ORDER BY connection_id
            """, new { itemId }))
            .Select(l => new ItemErpStatus(l.ConnectionId, names.GetValueOrDefault(l.ConnectionId, l.ConnectionId), l.ExternalId, l.Status, l.Attempts, l.LastError, l.UpdatedAt))
            .ToList();
    }
}
