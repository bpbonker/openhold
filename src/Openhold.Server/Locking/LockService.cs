using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Openhold.Core.Contracts;
using Openhold.Server.Configuration;
using Openhold.Server.Data;
using Openhold.Server.Identity;

namespace Openhold.Server.Locking;

/// <summary>
/// Global check-out locks. Every operation is a single transaction on the authority, so a batch
/// (an assembly and all its children, or an AutoCAD Electrical project set) is taken all-or-nothing
/// in one WAN round trip.
/// </summary>
public sealed class LockService(OpenholdDb db, IOptions<OpenholdOptions> options)
{
    private const int MaxDeadlockRetries = 3;

    // Re-checking-out a file you already hold on the same machine is a no-op success.
    // A lock held by anyone else (or by you on another machine) is not returned, which fails the batch.
    // Rows are inserted in id order so overlapping batches queue rather than deadlock.
    private const string AcquireSql = """
        INSERT INTO locks (file_id, user_id, site_id, machine, base_version)
        SELECT f.id, @userId, @siteId, @machine, f.latest_version
        FROM files f
        WHERE f.id = ANY(@ids) AND f.deleted_at IS NULL
        ORDER BY f.id
        ON CONFLICT (file_id) DO UPDATE SET machine = locks.machine
            WHERE locks.user_id = EXCLUDED.user_id AND locks.machine = EXCLUDED.machine
        RETURNING file_id
        """;

    public async Task<CheckOutResult> CheckOutAsync(Actor actor, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken = default)
    {
        var ids = fileIds.Distinct().Order().ToArray();
        if (ids.Length == 0)
            return new CheckOutResult(true, [], [], []);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await TryCheckOutAsync(actor, ids, cancellationToken);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DeadlockDetected && attempt < MaxDeadlockRetries)
            {
                // Postgres aborted one side of a deadlock; ours is safe to retry from scratch.
            }
        }
    }

    private async Task<CheckOutResult> TryCheckOutAsync(Actor actor, Guid[] ids, CancellationToken cancellationToken)
    {
        await using var connection = await db.Primary.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // A share lock waits for any state change in progress on these files, so the state read below is the one
        // it left: a file can never be checked out in the instant it is being released.
        await connection.ExecuteAsync("SELECT id FROM files WHERE id = ANY(@ids) ORDER BY id FOR SHARE", new { ids }, transaction);
        await RejectLockedStatesAsync(connection, transaction, ids);

        var files = (await connection.QueryAsync<LockedFile>(
            "SELECT id AS FileId, latest_version AS LatestVersion FROM files WHERE id = ANY(@ids) AND deleted_at IS NULL",
            new { ids }, transaction)).ToList();
        var notFound = ids.Except(files.Select(f => f.FileId)).ToList();
        if (notFound.Count > 0)
            return new CheckOutResult(false, [], [], notFound);

        var acquired = (await connection.QueryAsync<Guid>(AcquireSql,
            new { ids, userId = actor.UserId, siteId = actor.SiteId, machine = actor.Machine }, transaction)).ToHashSet();

        if (acquired.Count != ids.Length)
        {
            await transaction.RollbackAsync(cancellationToken);
            var conflicts = await GetHoldersAsync(connection, ids.Where(id => !acquired.Contains(id)).ToArray());
            return new CheckOutResult(false, [], conflicts, []);
        }

        await connection.ExecuteAsync("""
            INSERT INTO audit_events (user_id, site_id, action, file_id, data)
            SELECT @userId, @siteId, 'checkout', id, jsonb_build_object('machine', @machine::text)
            FROM unnest(@ids) AS id
            """, new { ids, userId = actor.UserId, siteId = actor.SiteId, machine = actor.Machine }, transaction);

        await transaction.CommitAsync(cancellationToken);
        return new CheckOutResult(true, files.OrderBy(f => f.FileId).ToList(), [], []);
    }

    /// <summary>
    /// Files in a locked lifecycle state (e.g. Released) can't be checked out: someone must move them back to an
    /// editable state first, which is a deliberate, recorded step.
    /// </summary>
    private async Task RejectLockedStatesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid[] ids)
    {
        var states = (await connection.QueryAsync<(Guid Id, string Name, string? Category, string? State)>("""
            SELECT f.id, f.name, f.category_id, v.state
            FROM files f LEFT JOIN file_versions v ON v.file_id = f.id AND v.version = f.latest_version
            WHERE f.id = ANY(@ids) AND f.category_id IS NOT NULL
            """, new { ids }, transaction)).ToList();
        if (states.Count == 0)
            return;

        var configuration = (await ConfigurationService.LoadAsync(connection, transaction, options.Value)).Configuration;
        var locked = states
            .Select(s => (s.Id, s.Name, State: configuration.StateOf(s.Category, s.State), Lifecycle: configuration.LifecycleFor(s.Category)))
            .Where(s => s.State is { Locked: true })
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (locked.Count == 0)
            return;

        var first = locked[0];
        var reopen = first.Lifecycle!.TransitionsFrom(first.State!.Name).FirstOrDefault(t => first.Lifecycle.State(t.To) is { Locked: false });
        var how = reopen is null ? "It can't be edited in this state." : $"To change it, use \"{reopen.DisplayName}\" first.";
        var names = locked.Count == 1
            ? $"{first.Name} is {first.State.Name}"
            : $"{locked.Count} files are in a state that can't be edited, e.g. {first.Name} is {first.State.Name}";
        throw OpenholdException.Conflict("state_locked", $"{names}, so {(locked.Count == 1 ? "it" : "they")} can't be checked out. {how}",
            new { fileIds = locked.Select(l => l.Id), states = locked.Select(l => l.State!.Name) });
    }

    /// <summary>Releases the caller's own locks held on this machine, without checking in. Returns the files released.</summary>
    public async Task<IReadOnlyList<Guid>> UndoCheckOutAsync(Actor actor, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken = default)
    {
        var ids = fileIds.Distinct().ToArray();
        await using var connection = await db.Primary.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var released = (await connection.QueryAsync<Guid>(
            // Only from the PC holding the checked-out copy: releasing from elsewhere would orphan the edits on that PC.
            "DELETE FROM locks WHERE file_id = ANY(@ids) AND user_id = @userId AND machine = @machine RETURNING file_id",
            new { ids, userId = actor.UserId, machine = actor.Machine }, transaction)).ToList();

        await connection.ExecuteAsync("""
            INSERT INTO audit_events (user_id, site_id, action, file_id)
            SELECT @userId, @siteId, 'undo_checkout', id FROM unnest(@released) AS id
            """, new { released = released.ToArray(), userId = actor.UserId, siteId = actor.SiteId }, transaction);

        await transaction.CommitAsync(cancellationToken);
        return released;
    }

    /// <summary>Current holders for the given files (from the local replica; may lag the authority slightly).</summary>
    public async Task<IReadOnlyList<LockHolder>> GetLocksAsync(IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.Local.OpenConnectionAsync(cancellationToken);
        return await GetHoldersAsync(connection, fileIds.Distinct().ToArray());
    }

    private static async Task<IReadOnlyList<LockHolder>> GetHoldersAsync(NpgsqlConnection connection, Guid[] ids) =>
        (await connection.QueryAsync<LockHolder>("""
            SELECT l.file_id AS FileId, u.username AS Username, l.site_id AS SiteId, l.machine AS Machine,
                   l.state AS State, l.acquired_at AS AcquiredAt
            FROM locks l JOIN users u ON u.id = l.user_id
            WHERE l.file_id = ANY(@ids)
            ORDER BY l.file_id
            """, new { ids })).ToList();
}
