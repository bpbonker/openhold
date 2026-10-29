using Dapper;
using Openhold.Core.Contracts;
using Openhold.Server.Configuration;
using Openhold.Server.Data;
using Openhold.Server.Identity;

namespace Openhold.Server.Audit;

/// <summary>
/// Read-only views for auditors and administrators: the append-only audit log, and the access review (everyone who
/// can sign in, how, and what their groups let them do), for periodic checks that access is still right.
/// </summary>
public sealed class AuditService(OpenholdDb db, ConfigurationService configuration, AccountService accounts)
{
    public const int MaxEntries = 5000;

    public async Task<IReadOnlyList<AuditEntry>> ListAsync(DateTime? from, DateTime? to, string? username, string? action, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.Local.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<AuditEntry>("""
            SELECT a.id AS Id, a.at AS At, COALESCE(u.username, a.data->>'username') AS Username, a.site_id AS Site, a.action AS Action,
                   f.name AS FileName, fo.path AS FolderPath, a.data::text AS Data
            FROM audit_events a
            LEFT JOIN users u ON u.id = a.user_id
            LEFT JOIN files f ON f.id = a.file_id
            LEFT JOIN folders fo ON fo.id = f.folder_id
            WHERE (@from::timestamptz IS NULL OR a.at >= @from)
              AND (@to::timestamptz IS NULL OR a.at < @to)
              AND (@username::text IS NULL OR lower(COALESCE(u.username, a.data->>'username')) = lower(@username))
              AND (@action::text IS NULL OR a.action = @action)
            ORDER BY a.id DESC
            LIMIT @limit
            """, new { from, to, username, action, limit = Math.Clamp(limit, 1, MaxEntries) })).ToList();
    }

    public async Task<IReadOnlyList<AccessReviewEntry>> AccessReviewAsync(CancellationToken cancellationToken = default)
    {
        var current = (await configuration.GetAsync(cancellationToken)).Configuration;
        var people = await accounts.ListAsync(cancellationToken);
        // Group members who have never signed in still have access on paper, so they belong in the review too.
        var known = people.Select(p => p.Username).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var neverSignedIn = current.Groups.SelectMany(g => g.Members).Where(m => !known.Contains(m)).Distinct(StringComparer.OrdinalIgnoreCase);

        return people.Select(p => Entry(p.Username, p.DisplayName, p.Kind, p.Disabled, p.TwoFactorEnabled, p.LastSignInAt))
            .Concat(neverSignedIn.Select(m => Entry(m, m, "not yet signed in", false, false, null)))
            .OrderBy(e => e.Username, StringComparer.OrdinalIgnoreCase)
            .ToList();

        AccessReviewEntry Entry(string username, string name, string kind, bool disabled, bool twoFactor, DateTime? lastSignIn) =>
            new(username, name, kind, disabled, twoFactor, lastSignIn,
                current.Groups.Where(g => current.IsMember(username, g.Id)).Select(g => g.Name).ToList(),
                current.PrivilegesOf(username));
    }
}
