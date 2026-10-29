using Dapper;
using Microsoft.Extensions.Options;
using Openhold.Core.Contracts;
using Openhold.Server.Data;

namespace Openhold.Server.Replication;

/// <summary>
/// Marks files whose latest content hasn't arrived at this site yet as <see cref="VaultFileInfo.Syncing"/>, so people
/// can see that someone at another site has checked in a change that's still on its way. One query per listing; a
/// vault with a single site never has anything syncing.
/// </summary>
public sealed class SyncingStatus(OpenholdDb db, IOptions<OpenholdOptions> options)
{
    public async Task<IReadOnlyList<VaultFileInfo>> MarkAsync(IReadOnlyList<VaultFileInfo> files, CancellationToken cancellationToken)
    {
        var hashes = files.Where(f => f.ContentHash is not null).Select(f => f.ContentHash!).Distinct().ToList();
        if (hashes.Count == 0)
            return files;
        await using var connection = await db.Local.OpenConnectionAsync(cancellationToken);
        var syncing = (await connection.QueryAsync<string>("""
            SELECT encode(c.hash, 'hex') FROM unnest(@hashes) AS c(hash)
            WHERE EXISTS (SELECT 1 FROM sites WHERE id <> @site)
              AND NOT EXISTS (SELECT 1 FROM content_presence p WHERE p.content_hash = c.hash AND p.site_id = @site)
            """, new { hashes = hashes.Select(Convert.FromHexString).ToArray(), site = options.Value.SiteId })).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return syncing.Count == 0 ? files : files.Select(f => f.ContentHash is { } hash && syncing.Contains(hash) ? f with { Syncing = true } : f).ToList();
    }

    public async Task<VaultFileInfo> MarkAsync(VaultFileInfo file, CancellationToken cancellationToken) =>
        (await MarkAsync([file], cancellationToken))[0];
}
