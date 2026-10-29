using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Openhold.Core.Configuration;
using Openhold.Core.Contracts;
using Openhold.Server.Configuration;
using Openhold.Server.Data;
using Openhold.Server.Identity;

namespace Openhold.Server.Files;

/// <summary>
/// Deleting to the trash bin and restoring from it. Deletes are all-or-nothing, never break references
/// (a file another file still uses can't be deleted), and keep the full history for a later restore.
/// </summary>
public sealed class FileManagementService(OpenholdDb db, IOptions<OpenholdOptions> options)
{
    public async Task<IReadOnlyList<Guid>> DeleteAsync(Actor actor, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken = default)
    {
        var ids = fileIds.Distinct().ToArray();
        await using var connection = await db.Primary.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var files = (await connection.QueryAsync<(Guid Id, string Name)>(
            "SELECT id, name FROM files WHERE id = ANY(@ids) AND deleted_at IS NULL ORDER BY id FOR UPDATE", new { ids }, transaction)).ToList();
        if (files.Count != ids.Length)
            throw OpenholdException.NotFound("Some of these files are no longer in the vault.");

        // Released (or otherwise locked) files are records others rely on: only an administrator can bin them.
        var configuration = (await ConfigurationService.LoadAsync(connection, transaction, options.Value)).Configuration;
        if (!configuration.HasPrivilege(actor.Username, Privileges.DeleteReleasedFiles))
        {
            var locked = (await connection.QueryAsync<(string Name, string? Category, string? State)>("""
                SELECT f.name, f.category_id, v.state
                FROM files f JOIN file_versions v ON v.file_id = f.id AND v.version = f.latest_version
                WHERE f.id = ANY(@ids)
                """, new { ids }, transaction))
                .Select(f => (f.Name, State: configuration.StateOf(f.Category, f.State)))
                .Where(f => f.State is { Locked: true })
                .ToList();
            if (locked.Count > 0)
            {
                throw OpenholdException.Conflict("state_locked",
                    "Nothing was deleted: " + string.Join("; ", locked.Select(l => $"{l.Name} is {l.State!.Name}")) +
                    ". Deleting files in that state needs the Delete Released Files privilege; make them obsolete instead if they're no longer to be used.");
            }
        }

        var heldByOthers = (await connection.QueryAsync<(string Name, string Username, string Machine)>("""
            SELECT f.name, u.username, l.machine
            FROM locks l JOIN files f ON f.id = l.file_id JOIN users u ON u.id = l.user_id
            WHERE l.file_id = ANY(@ids) AND NOT (l.user_id = @userId AND l.machine = @machine)
            """, new { ids, userId = actor.UserId, machine = actor.Machine }, transaction)).ToList();
        if (heldByOthers.Count > 0)
        {
            throw OpenholdException.Conflict("checked_out",
                "Nothing was deleted, because some files are checked out: " +
                string.Join("; ", heldByOthers.Select(h => $"{h.Name} is checked out to {h.Username} on {h.Machine}")) + ".");
        }

        // A file still used by another file's latest version (outside this delete) would leave a broken reference.
        var usedBy = (await connection.QueryAsync<(string Child, string Parent)>("""
            SELECT c.name, p.name
            FROM file_references r
            JOIN files p ON p.id = r.parent_file_id AND r.parent_version = p.latest_version AND p.deleted_at IS NULL
            JOIN files c ON c.id = r.child_file_id
            WHERE r.child_file_id = ANY(@ids) AND NOT (r.parent_file_id = ANY(@ids))
            """, new { ids }, transaction)).ToList();
        if (usedBy.Count > 0)
        {
            throw OpenholdException.Conflict("still_used",
                "Nothing was deleted, because other files still use some of these: " +
                string.Join("; ", usedBy.GroupBy(u => u.Child).Select(g => $"{g.Key} is used by {string.Join(", ", g.Select(x => x.Parent).Distinct())}")) +
                ". Remove them from those files first, or delete those files too.");
        }

        await connection.ExecuteAsync("DELETE FROM locks WHERE file_id = ANY(@ids)", new { ids }, transaction);
        await connection.ExecuteAsync(
            "UPDATE files SET deleted_at = now(), deleted_by = @userId WHERE id = ANY(@ids)", new { ids, userId = actor.UserId }, transaction);
        await Audit(connection, transaction, actor, "delete", ids);
        await transaction.CommitAsync(cancellationToken);
        return ids;
    }

    public async Task<IReadOnlyList<TrashItem>> ListTrashAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await db.Local.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<TrashItem>("""
            SELECT f.id AS FileId, f.name AS Name, fo.path AS FolderPath, f.latest_version AS LatestVersion,
                   f.deleted_at AS DeletedAt, COALESCE(u.username, '') AS DeletedBy
            FROM files f JOIN folders fo ON fo.id = f.folder_id LEFT JOIN users u ON u.id = f.deleted_by
            WHERE f.deleted_at IS NOT NULL
            ORDER BY f.deleted_at DESC
            LIMIT 1000
            """)).ToList();
    }

    /// <summary>Brings files back from the trash with their full history. Fails as a whole if a name is now taken.</summary>
    public async Task<IReadOnlyList<Guid>> RestoreFromTrashAsync(Actor actor, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken = default)
    {
        var ids = fileIds.Distinct().ToArray();
        await using var connection = await db.Primary.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var restored = (await connection.QueryAsync<Guid>(
                "UPDATE files SET deleted_at = NULL, deleted_by = NULL WHERE id = ANY(@ids) AND deleted_at IS NOT NULL RETURNING id",
                new { ids }, transaction)).ToList();
            await Audit(connection, transaction, actor, "restore_from_trash", restored.ToArray());
            await transaction.CommitAsync(cancellationToken);
            return restored;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw OpenholdException.Conflict("name_conflict",
                "Nothing was restored, because a file with the same name has been added since. Rename or delete that file first.");
        }
    }

    private static Task Audit(NpgsqlConnection connection, NpgsqlTransaction transaction, Actor actor, string action, Guid[] ids) =>
        connection.ExecuteAsync("""
            INSERT INTO audit_events (user_id, site_id, action, file_id, data)
            SELECT @userId, @siteId, @action, id, jsonb_build_object('machine', @machine::text) FROM unnest(@ids) AS id
            """, new { userId = actor.UserId, siteId = actor.SiteId, action, ids, machine = actor.Machine }, transaction);
}
