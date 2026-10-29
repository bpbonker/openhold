using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Openhold.Core.Configuration;
using Openhold.Core.Contracts;
using Openhold.Server.Configuration;
using Openhold.Server.Data;
using Openhold.Server.Identity;
using Openhold.Server.Jobs;

namespace Openhold.Server.Lifecycles;

/// <summary>
/// Moves files between lifecycle states, e.g. releasing a drawing with the parts it uses. A state change is
/// a new version with the same content, so history shows who released what and when. Files change together
/// or not at all. A transition that needs sign-off becomes a change request, applied once the approvals are in.
/// Revisions never change here: revising is always a separate, explicit action.
/// </summary>
public sealed class LifecycleService(OpenholdDb db, IOptions<OpenholdOptions> options)
{
    public const int MaxFiles = 2000;

    private sealed record FileState(Guid Id, string Name, string FolderPath, string? CategoryId, int Version, string? State, string PropertiesJson, string? LockedBy);

    private sealed record Child(Guid ParentId, Guid ChildId, string ChildName, string? CategoryId, string? State);

    /// <summary>One file's planned move, with the transition that allows it.</summary>
    private sealed record Move(FileState File, LifecycleState From, LifecycleTransition Transition);

    public async Task<StateChangeResult> ChangeStateAsync(Actor actor, ChangeStateRequest request, CancellationToken cancellationToken = default)
    {
        var ids = request.FileIds.Distinct().ToArray();
        if (request.ChangesetId == Guid.Empty)
            throw OpenholdException.BadRequest("invalid_changeset", "A client-generated changeset id is required.");
        if (ids.Length is 0 or > MaxFiles)
            throw OpenholdException.BadRequest("invalid_count", $"Change the state of between 1 and {MaxFiles} files at a time.");
        if (string.IsNullOrWhiteSpace(request.ToState))
            throw OpenholdException.BadRequest("invalid_state", "Say which state to move the files to.");

        await using var connection = await db.Primary.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        if (!request.CheckOnly)
        {
            await SerialiseRetriesAsync(connection, transaction, request.ChangesetId);
            if (await FindExistingAsync(connection, transaction, actor, request.ChangesetId) is { } existing)
                return existing;
        }

        var configuration = (await ConfigurationService.LoadAsync(connection, transaction, options.Value)).Configuration;
        var (moves, problems) = await PlanAsync(connection, transaction, configuration, actor.Username, ids, request.ToState, request.Comment,
            checkPermission: true, excludeRequest: null);
        var needsApproval = moves.Any(m => m.Transition.NeedsApproval);

        if (request.CheckOnly)
            return new StateChangeResult(request.ChangesetId, false, needsApproval, [], problems);
        if (problems.Count > 0)
            throw Refused(problems);

        if (needsApproval)
        {
            await CreateRequestAsync(connection, transaction, actor, request, moves);
            await transaction.CommitAsync(cancellationToken);
            return new StateChangeResult(request.ChangesetId, false, true, [], []);
        }

        var versions = await ApplyAsync(connection, transaction, actor.UserId, actor.SiteId, actor.Machine, request.ChangesetId,
            Describe(moves, request.Comment, approvedBy: null), moves, request.ToState);
        await transaction.CommitAsync(cancellationToken);
        return new StateChangeResult(request.ChangesetId, true, false, versions, []);
    }

    private static OpenholdException Refused(IReadOnlyList<StateChangeProblem> problems)
    {
        var shown = problems.Take(5).Select(p => p.Message).ToList();
        if (problems.Count > shown.Count)
            shown.Add($"and {problems.Count - shown.Count} more");
        return OpenholdException.Conflict("state_change_refused",
            "Nothing was changed: " + string.Join("; ", shown) + ".", new { problems });
    }

    /// <summary>Two copies of the same request (a retry racing the original) wait for each other instead of both inserting.</summary>
    private static Task SerialiseRetriesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id) =>
        connection.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))", new { key = "openhold.change:" + id }, transaction);

    /// <summary>
    /// A retried request returns what the first attempt did: the files it changed, or the change request it made
    /// (and, if that has since been approved, the files the approval changed).
    /// </summary>
    private static async Task<StateChangeResult?> FindExistingAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Actor actor, Guid id)
    {
        var changesetOwner = await connection.ExecuteScalarAsync<Guid?>("SELECT user_id FROM changesets WHERE id = @id", new { id }, transaction);
        var request = await connection.QuerySingleOrDefaultAsync<(Guid RequestedBy, string Status, Guid? ChangesetId)?>(
            "SELECT requested_by, status, changeset_id FROM change_requests WHERE id = @id", new { id }, transaction);
        if ((changesetOwner ?? request?.RequestedBy) is not { } owner)
            return null;
        if (owner != actor.UserId)
            throw OpenholdException.Conflict("changeset_id_taken", "That changeset id belongs to someone else's change.");
        if (request is { } found)
        {
            return found.ChangesetId is { } applied
                ? new StateChangeResult(id, true, false, await VersionsOfAsync(connection, transaction, applied), [])
                : new StateChangeResult(id, false, found.Status == "pending", [], []);
        }
        return new StateChangeResult(id, true, false, await VersionsOfAsync(connection, transaction, id), []);
    }

    private static async Task<List<CommittedVersion>> VersionsOfAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid changesetId) =>
        (await connection.QueryAsync<CommittedVersion>("""
            SELECT v.file_id AS FileId, f.folder_id AS FolderId, f.name AS Name, v.version AS Version
            FROM file_versions v JOIN files f ON f.id = v.file_id WHERE v.changeset_id = @changesetId ORDER BY f.name
            """, new { changesetId }, transaction)).ToList();

    /// <summary>
    /// Works out each file's transition and every reason the change can't happen. Takes row locks on the files,
    /// so nobody can check one out or change it between this check and the change itself.
    /// </summary>
    private static async Task<(List<Move> Moves, List<StateChangeProblem> Problems)> PlanAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, VaultConfiguration configuration, string username,
        Guid[] ids, string toState, string? comment, bool checkPermission, Guid? excludeRequest)
    {
        // Lock the rows first, in id order, then read in a new statement so the read sees anything committed while waiting.
        await connection.ExecuteAsync("SELECT id FROM files WHERE id = ANY(@ids) ORDER BY id FOR UPDATE", new { ids }, transaction);
        var files = (await connection.QueryAsync<FileState>("""
            SELECT f.id AS Id, f.name AS Name, fo.path AS FolderPath, f.category_id AS CategoryId, f.latest_version AS Version,
                   v.state AS State, (v.vault_properties || v.properties)::text AS PropertiesJson, u.username AS LockedBy
            FROM files f
            JOIN folders fo ON fo.id = f.folder_id
            JOIN file_versions v ON v.file_id = f.id AND v.version = f.latest_version
            LEFT JOIN locks l ON l.file_id = f.id
            LEFT JOIN users u ON u.id = l.user_id
            WHERE f.id = ANY(@ids) AND f.deleted_at IS NULL
            ORDER BY lower(f.name)
            """, new { ids }, transaction)).ToList();
        if (files.Count != ids.Length)
            throw OpenholdException.NotFound("Some of these files are no longer in the vault.");

        var pending = (await connection.QueryAsync<(Guid FileId, string Username)>("""
            SELECT crf.file_id, u.username
            FROM change_request_files crf
            JOIN change_requests cr ON cr.id = crf.request_id AND cr.status = 'pending'
            JOIN users u ON u.id = cr.requested_by
            WHERE crf.file_id = ANY(@ids) AND cr.id IS DISTINCT FROM @excludeRequest
            """, new { ids, excludeRequest }, transaction)).ToDictionary(p => p.FileId, p => p.Username);

        var moves = new List<Move>();
        var problems = new List<StateChangeProblem>();
        void Problem(FileState file, string message) => problems.Add(new StateChangeProblem(file.Id, file.Name, message));

        foreach (var file in files)
        {
            var lifecycle = configuration.LifecycleFor(file.CategoryId);
            if (lifecycle is null)
            {
                Problem(file, $"{file.Name} has no lifecycle (category: {configuration.Category(file.CategoryId)?.Name ?? "none"})");
                continue;
            }
            var from = configuration.StateOf(file.CategoryId, file.State)!;
            if (lifecycle.State(toState) is null)
            {
                Problem(file, $"{file.Name} follows the {lifecycle.Name} lifecycle, which has no '{toState}' state");
                continue;
            }
            if (string.Equals(from.Name, toState, StringComparison.OrdinalIgnoreCase))
            {
                Problem(file, $"{file.Name} is already {from.Name}");
                continue;
            }
            var transition = lifecycle.Transition(from.Name, toState);
            if (transition is null)
            {
                var allowed = lifecycle.TransitionsFrom(from.Name).Select(t => t.To).ToList();
                Problem(file, $"{file.Name} is {from.Name} and can't go straight to {toState}" +
                    (allowed.Count > 0 ? $" (it can go to {string.Join(" or ", allowed)})" : ""));
                continue;
            }
            if (checkPermission && !transition.Allows(configuration, username))
            {
                var groups = string.Join(" or ", (transition.Groups ?? []).Select(g => configuration.Group(g)?.Name ?? g));
                Problem(file, $"only {groups} can {transition.DisplayName.ToLowerInvariant()} {file.Name}");
                continue;
            }
            if (transition.RequireComment && string.IsNullOrWhiteSpace(comment))
                Problem(file, $"a comment is required to {transition.DisplayName.ToLowerInvariant()}");
            if (file.LockedBy is { } holder)
                Problem(file, $"{file.Name} is checked out to {holder}; check it in or undo the check-out first");
            if (pending.TryGetValue(file.Id, out var requester))
                Problem(file, $"{file.Name} is already waiting for approval of a change requested by {requester}");

            var properties = JsonSerializer.Deserialize<Dictionary<string, string>>(file.PropertiesJson) ?? [];
            var missing = (transition.RequiredProperties ?? [])
                .Where(p => !properties.TryGetValue(p, out var value) || string.IsNullOrWhiteSpace(value))
                .ToList();
            if (missing.Count > 0)
                Problem(file, $"{file.Name} needs {string.Join(", ", missing)} filled in first");

            moves.Add(new Move(file, from, transition));
        }

        await CheckChildrenAsync(connection, transaction, configuration, ids, toState, moves, Problem);
        return (moves, problems.DistinctBy(p => (p.FileId, p.Message)).ToList());
    }

    /// <summary>
    /// For transitions that require it, every file a file uses must be released already, or be moving to a
    /// released state in this same change (so an assembly and its new parts can be released together).
    /// </summary>
    private static async Task CheckChildrenAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, VaultConfiguration configuration,
        Guid[] ids, string toState, List<Move> moves, Action<FileState, string> problem)
    {
        var needChecking = moves.Where(m => m.Transition.RequireChildrenReleased).ToList();
        if (needChecking.Count == 0)
            return;

        var parents = needChecking.Select(m => m.File.Id).ToArray();
        var children = (await connection.QueryAsync<Child>("""
            SELECT r.parent_file_id AS ParentId, c.id AS ChildId, c.name AS ChildName, c.category_id AS CategoryId, cv.state AS State
            FROM files p
            JOIN file_references r ON r.parent_file_id = p.id AND r.parent_version = p.latest_version
            JOIN files c ON c.id = r.child_file_id
            JOIN file_versions cv ON cv.file_id = c.id AND cv.version = c.latest_version
            WHERE p.id = ANY(@parents)
            -- Nobody may reopen or change a used file until this change commits, so the released parent records
            -- exactly the versions checked here.
            FOR SHARE OF c
            """, new { parents }, transaction)).ToLookup(c => c.ParentId);

        var movingToReleased = moves
            .Where(m => configuration.LifecycleFor(m.File.CategoryId)?.State(toState)?.Released == true)
            .Select(m => m.File.Id)
            .ToHashSet();

        foreach (var move in needChecking)
        {
            foreach (var child in children[move.File.Id])
            {
                if (movingToReleased.Contains(child.ChildId))
                    continue;
                var state = configuration.StateOf(child.CategoryId, child.State);
                if (state is null || state.Released)
                    continue; // no lifecycle (e.g. standard library content) counts as released
                problem(move.File, ids.Contains(child.ChildId)
                    ? $"{move.File.Name} uses {child.ChildName}, which won't be released by this change"
                    : $"{move.File.Name} uses {child.ChildName}, which is {state.Name}; release it first or include it in this change");
            }
        }
    }

    /// <summary>
    /// Makes the new versions: same content, properties, preview and references as the latest version, in the
    /// new state. References point at each child's latest version, so a released assembly records the released parts.
    /// </summary>
    private static async Task<List<CommittedVersion>> ApplyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid userId, string siteId, string machine, Guid changesetId, string comment, IReadOnlyList<Move> moves, string toState)
    {
        await connection.ExecuteAsync("""
            INSERT INTO changesets (id, user_id, site_id, machine, comment) VALUES (@changesetId, @userId, @siteId, @machine, @comment)
            """, new { changesetId, userId, siteId, machine, comment }, transaction);

        var versions = new List<CommittedVersion>(moves.Count);
        foreach (var move in moves)
        {
            var target = move.Transition.To; // the lifecycle's own spelling of the state
            var (version, name, folderId) = await connection.QuerySingleAsync<(int, string, Guid)>(
                "UPDATE files SET latest_version = latest_version + 1 WHERE id = @id RETURNING latest_version, name, folder_id",
                new { id = move.File.Id }, transaction);
            await connection.ExecuteAsync("""
                INSERT INTO file_versions (file_id, version, changeset_id, length, content_hash, manifest, origin_site, properties, thumbnail, state, vault_properties)
                SELECT file_id, @version, @changesetId, length, content_hash, manifest, origin_site, properties, thumbnail, @target, vault_properties
                FROM file_versions WHERE file_id = @fileId AND version = @version - 1
                """, new { fileId = move.File.Id, version, changesetId, target }, transaction);
            versions.Add(new CommittedVersion(move.File.Id, folderId, name, version));
            // A release that publishes a PDF queues it for a job worker; the release itself doesn't wait.
            if (move.Transition.PublishPdf && JobService.Publishable(name))
                await JobService.QueueAsync(connection, transaction, JobService.Pdf, move.File.Id, version, userId);
        }

        foreach (var version in versions)
        {
            await connection.ExecuteAsync("""
                INSERT INTO file_references (parent_file_id, parent_version, child_file_id, child_version, ref_path)
                SELECT r.parent_file_id, @version, r.child_file_id, c.latest_version, r.ref_path
                FROM file_references r JOIN files c ON c.id = r.child_file_id
                WHERE r.parent_file_id = @fileId AND r.parent_version = @version - 1
                """, new { fileId = version.FileId, version = version.Version }, transaction);
        }

        await connection.ExecuteAsync("""
            INSERT INTO audit_events (user_id, site_id, action, file_id, changeset_id, data)
            SELECT @userId, @siteId, 'state_change', m.file_id, @changesetId,
                   jsonb_build_object('from', m.from_state, 'to', @toState::text, 'version', m.version)
            FROM unnest(@fileIds, @fromStates, @versionNumbers) AS m(file_id, from_state, version)
            """, new
        {
            userId,
            siteId,
            changesetId,
            toState,
            fileIds = moves.Select(m => m.File.Id).ToArray(),
            fromStates = moves.Select(m => m.From.Name).ToArray(),
            versionNumbers = versions.Select(v => v.Version).ToArray(),
        }, transaction);
        return versions.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The check-in comment for a state change, e.g. "Release (approved by jsmith): first issue".</summary>
    private static string Describe(IReadOnlyList<Move> moves, string? comment, IReadOnlyList<string>? approvedBy)
    {
        var action = moves.Select(m => m.Transition.DisplayName).Distinct().First();
        var approval = approvedBy is { Count: > 0 } ? $" (approved by {string.Join(", ", approvedBy)})" : "";
        return string.IsNullOrWhiteSpace(comment) ? action + approval : $"{action}{approval}: {comment.Trim()}";
    }

    private static async Task CreateRequestAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Actor actor, ChangeStateRequest request, List<Move> moves)
    {
        // Every rule from every transition in the change applies; where two ask for the same group, the larger count wins.
        var rules = Approvals.Merge(moves.SelectMany(m => m.Transition.Approvals ?? []));
        var submitterMayApprove = moves.Where(m => m.Transition.NeedsApproval).All(m => m.Transition.SubmitterMayApprove);

        await connection.ExecuteAsync("""
            INSERT INTO change_requests (id, to_state, comment, approvals, submitter_may_approve, requested_by, site_id)
            VALUES (@id, @toState, @comment, @approvals::jsonb, @submitterMayApprove, @userId, @siteId)
            """, new
        {
            id = request.ChangesetId,
            toState = moves[0].Transition.To,
            comment = request.Comment?.Trim() ?? "",
            approvals = JsonSerializer.Serialize(rules, ConfigurationService.Json),
            submitterMayApprove,
            userId = actor.UserId,
            siteId = actor.SiteId,
        }, transaction);
        await connection.ExecuteAsync("""
            INSERT INTO change_request_files (request_id, file_id, version, from_state)
            SELECT @id, m.file_id, m.version, m.from_state FROM unnest(@fileIds, @versions, @fromStates) AS m(file_id, version, from_state)
            """, new
        {
            id = request.ChangesetId,
            fileIds = moves.Select(m => m.File.Id).ToArray(),
            versions = moves.Select(m => m.File.Version).ToArray(),
            fromStates = moves.Select(m => m.From.Name).ToArray(),
        }, transaction);
        await connection.ExecuteAsync("""
            INSERT INTO audit_events (user_id, site_id, action, file_id, data)
            SELECT @userId, @siteId, 'state_change_requested', id, jsonb_build_object('request', @requestId::text, 'to', @toState::text)
            FROM unnest(@fileIds) AS id
            """, new { userId = actor.UserId, siteId = actor.SiteId, requestId = request.ChangesetId, toState = request.ToState, fileIds = moves.Select(m => m.File.Id).ToArray() }, transaction);
    }

    /// <summary>Signs off a change request. When the last required approval is in, the files change state.</summary>
    public async Task<ChangeRequestInfo> ApproveAsync(Actor actor, Guid requestId, string? comment, CancellationToken cancellationToken = default) =>
        await DecideAsync(actor, requestId, approve: true, comment, cancellationToken);

    /// <summary>Rejects a change request. The files stay as they are; the requester can make changes and ask again.</summary>
    public async Task<ChangeRequestInfo> RejectAsync(Actor actor, Guid requestId, string? comment, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(comment))
            throw OpenholdException.BadRequest("comment_required", "Say why you're rejecting it, so the requester knows what to change.");
        return await DecideAsync(actor, requestId, approve: false, comment, cancellationToken);
    }

    private sealed record RequestRow(Guid Id, string ToState, string Comment, string ApprovalsJson, bool SubmitterMayApprove, Guid RequestedBy, string RequesterName, string Status);

    private async Task<ChangeRequestInfo> DecideAsync(Actor actor, Guid requestId, bool approve, string? comment, CancellationToken cancellationToken)
    {
        await using var connection = await db.Primary.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var request = await connection.QuerySingleOrDefaultAsync<RequestRow>("""
            SELECT cr.id AS Id, cr.to_state AS ToState, cr.comment AS Comment, cr.approvals::text AS ApprovalsJson,
                   cr.submitter_may_approve AS SubmitterMayApprove, cr.requested_by AS RequestedBy, u.username AS RequesterName, cr.status AS Status
            FROM change_requests cr JOIN users u ON u.id = cr.requested_by
            WHERE cr.id = @requestId
            FOR UPDATE OF cr
            """, new { requestId }, transaction)
            ?? throw OpenholdException.NotFound("That change request doesn't exist.");
        if (request.Status != "pending")
            throw OpenholdException.Conflict("request_closed", $"That change request is already {request.Status}.");

        var configuration = (await ConfigurationService.LoadAsync(connection, transaction, options.Value)).Configuration;
        var rules = JsonSerializer.Deserialize<List<ApprovalRule>>(request.ApprovalsJson, ConfigurationService.Json) ?? [];
        var groups = rules.Where(r => configuration.IsMember(actor.Username, r.Group)).Select(r => r.Group).ToArray();
        if (groups.Length == 0)
        {
            throw new OpenholdException(StatusCodes.Status403Forbidden, "not_an_approver",
                $"Only {string.Join(" or ", rules.Select(r => configuration.Group(r.Group)?.Name ?? r.Group))} can approve this change.");
        }
        if (request.RequestedBy == actor.UserId && !request.SubmitterMayApprove)
            throw new OpenholdException(StatusCodes.Status403Forbidden, "own_request", "Someone else has to approve a change you asked for.");

        var inserted = await connection.ExecuteAsync("""
            INSERT INTO approval_decisions (request_id, user_id, decision, groups, comment)
            VALUES (@requestId, @userId, @decision, @groups, @comment)
            ON CONFLICT DO NOTHING
            """, new { requestId, userId = actor.UserId, decision = approve ? "approved" : "rejected", groups, comment = comment?.Trim() ?? "" }, transaction);
        if (inserted == 0)
            throw OpenholdException.Conflict("already_decided", "You've already given your decision on this change.");

        var files = (await connection.QueryAsync<(Guid FileId, int Version)>(
            "SELECT file_id, version FROM change_request_files WHERE request_id = @requestId", new { requestId }, transaction)).ToList();

        if (!approve)
        {
            await CloseAsync(connection, transaction, requestId, "rejected", null);
            await AuditAsync(connection, transaction, actor, "change_rejected", requestId, files.Select(f => f.FileId));
        }
        else
        {
            await AuditAsync(connection, transaction, actor, "change_approved", requestId, files.Select(f => f.FileId));
            var approvals = (await connection.QueryAsync<(string Username, string[] Groups)>("""
                SELECT u.username, d.groups FROM approval_decisions d JOIN users u ON u.id = d.user_id
                WHERE d.request_id = @requestId AND d.decision = 'approved'
                """, new { requestId }, transaction)).ToList();
            if (Approvals.Satisfied(rules, approvals))
                await CompleteAsync(connection, transaction, configuration, request, rules, files, approvals, actor);
        }

        await transaction.CommitAsync(cancellationToken);
        return (await LoadRequestsAsync(connection, actor, configuration, [requestId])).Single();
    }

    /// <summary>
    /// Applies a change once its approvals are in. Everything is checked again under today's configuration: the
    /// requester must still be allowed to make the change, any approval rule added since must also be met, and a
    /// file changed since it was submitted (or a part un-released meanwhile) stops it. A change that can no longer
    /// be applied is closed as blocked, with the reasons, so its files are free to be submitted again; the
    /// approvals given are kept on record.
    /// </summary>
    private static async Task CompleteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, VaultConfiguration configuration,
        RequestRow request, List<ApprovalRule> frozenRules, List<(Guid FileId, int Version)> submitted, List<(string Username, string[] Groups)> approvals, Actor approver)
    {
        var ids = submitted.Select(f => f.FileId).ToArray();
        List<Move> moves;
        List<StateChangeProblem> problems;
        try
        {
            (moves, problems) = await PlanAsync(connection, transaction, configuration, request.RequesterName, ids, request.ToState, request.Comment,
                checkPermission: true, excludeRequest: request.Id);
        }
        catch (OpenholdException ex) when (ex.StatusCode == StatusCodes.Status404NotFound)
        {
            (moves, problems) = ([], [new StateChangeProblem(Guid.Empty, "", "some of the files have been deleted since it was submitted")]);
        }
        foreach (var file in moves.Where(m => submitted.Single(s => s.FileId == m.File.Id).Version != m.File.Version))
            problems.Add(new StateChangeProblem(file.File.Id, file.File.Name, $"{file.File.Name} has changed since it was submitted"));
        if (problems.Count > 0)
        {
            await connection.ExecuteAsync(
                "UPDATE change_requests SET status = 'blocked', resolved_at = now(), problems = @problems::jsonb WHERE id = @id",
                new { id = request.Id, problems = JsonSerializer.Serialize(problems.Select(p => p.Message), ConfigurationService.Json) }, transaction);
            return;
        }

        // Rules made stricter since the request was submitted apply too; it waits for the extra sign-off.
        var current = Approvals.Merge(frozenRules.Concat(moves.SelectMany(m => m.Transition.Approvals ?? [])));
        if (!Approvals.Satisfied(current, approvals))
        {
            await connection.ExecuteAsync("UPDATE change_requests SET approvals = @rules::jsonb WHERE id = @id",
                new { id = request.Id, rules = JsonSerializer.Serialize(current, ConfigurationService.Json) }, transaction);
            return;
        }
        var approvers = approvals.Select(a => a.Username).ToList();

        // Recorded as the requester's change (it's their work being released), made where the final approval was given.
        var changesetId = Guid.NewGuid();
        await ApplyAsync(connection, transaction, request.RequestedBy, approver.SiteId, approver.Machine, changesetId,
            Describe(moves, request.Comment, approvers), moves, request.ToState);
        await CloseAsync(connection, transaction, request.Id, "approved", changesetId);
    }

    /// <summary>Withdraws a pending request (the requester or an administrator). The files stay as they are.</summary>
    public async Task<ChangeRequestInfo> CancelAsync(Actor actor, Guid requestId, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.Primary.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var configuration = (await ConfigurationService.LoadAsync(connection, transaction, options.Value)).Configuration;
        var (requestedBy, status) = await connection.QuerySingleOrDefaultAsync<(Guid, string)?>(
            "SELECT requested_by, status FROM change_requests WHERE id = @requestId FOR UPDATE", new { requestId }, transaction)
            ?? throw OpenholdException.NotFound("That change request doesn't exist.");
        if (status != "pending")
            throw OpenholdException.Conflict("request_closed", $"That change request is already {status}.");
        if (requestedBy != actor.UserId && !configuration.IsAdministrator(actor.Username))
            throw new OpenholdException(StatusCodes.Status403Forbidden, "not_requester", "Only the person who asked for this change, or an administrator, can cancel it.");

        await CloseAsync(connection, transaction, requestId, "cancelled", null);
        var files = await connection.QueryAsync<Guid>("SELECT file_id FROM change_request_files WHERE request_id = @requestId", new { requestId }, transaction);
        await AuditAsync(connection, transaction, actor, "change_cancelled", requestId, files);
        await transaction.CommitAsync(cancellationToken);
        return (await LoadRequestsAsync(connection, actor, configuration, [requestId])).Single();
    }

    private static Task CloseAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid requestId, string status, Guid? changesetId) =>
        connection.ExecuteAsync(
            "UPDATE change_requests SET status = @status, resolved_at = now(), changeset_id = @changesetId WHERE id = @requestId",
            new { requestId, status, changesetId }, transaction);

    private static Task AuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Actor actor, string action, Guid requestId, IEnumerable<Guid> fileIds) =>
        connection.ExecuteAsync("""
            INSERT INTO audit_events (user_id, site_id, action, file_id, data)
            SELECT @userId, @siteId, @action, id, jsonb_build_object('request', @requestId::text) FROM unnest(@fileIds) AS id
            """, new { userId = actor.UserId, siteId = actor.SiteId, action, requestId, fileIds = fileIds.ToArray() }, transaction);

    /// <summary>Pending change requests the caller can approve or asked for, newest first.</summary>
    public async Task<IReadOnlyList<ChangeRequestInfo>> ListOpenAsync(Actor actor, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.Local.OpenConnectionAsync(cancellationToken);
        var configuration = (await ConfigurationService.LoadAsync(connection, null, options.Value)).Configuration;
        var groups = configuration.Groups.Where(g => configuration.IsMember(actor.Username, g.Id)).Select(g => g.Id.ToLowerInvariant()).ToArray();
        var ids = (await connection.QueryAsync<Guid>("""
            SELECT id FROM change_requests cr
            WHERE status = 'pending'
              AND (requested_by = @userId OR EXISTS (SELECT 1 FROM jsonb_array_elements(cr.approvals) r WHERE lower(r->>'group') = ANY(@groups)))
            ORDER BY requested_at DESC
            LIMIT 500
            """, new { userId = actor.UserId, groups })).ToArray();
        var requests = await LoadRequestsAsync(connection, actor, configuration, ids);
        return requests.Where(r => r.CanApprove || r.IsMine).ToList();
    }

    public async Task<ChangeRequestInfo> GetAsync(Actor actor, Guid requestId, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.Local.OpenConnectionAsync(cancellationToken);
        var configuration = (await ConfigurationService.LoadAsync(connection, null, options.Value)).Configuration;
        return (await LoadRequestsAsync(connection, actor, configuration, [requestId])).SingleOrDefault()
            ?? throw OpenholdException.NotFound("That change request doesn't exist.");
    }

    private static async Task<List<ChangeRequestInfo>> LoadRequestsAsync(NpgsqlConnection connection, Actor actor,
        VaultConfiguration configuration, Guid[] ids)
    {
        if (ids.Length == 0)
            return [];
        var requests = (await connection.QueryAsync<(Guid Id, string ToState, string Comment, string ApprovalsJson, bool SubmitterMayApprove, Guid RequestedBy, string RequesterName, DateTime RequestedAt, string Status, string? ProblemsJson)>("""
            SELECT cr.id, cr.to_state, cr.comment, cr.approvals::text, cr.submitter_may_approve, cr.requested_by, u.username, cr.requested_at, cr.status,
                   cr.problems::text
            FROM change_requests cr JOIN users u ON u.id = cr.requested_by
            WHERE cr.id = ANY(@ids)
            ORDER BY cr.requested_at DESC
            """, new { ids })).ToList();
        var files = (await connection.QueryAsync<(Guid RequestId, Guid FileId, string Name, string FolderPath, int Version, string FromState)>("""
            SELECT crf.request_id, f.id, f.name, fo.path, crf.version, crf.from_state
            FROM change_request_files crf JOIN files f ON f.id = crf.file_id JOIN folders fo ON fo.id = f.folder_id
            WHERE crf.request_id = ANY(@ids)
            ORDER BY lower(f.name)
            """, new { ids })).ToLookup(f => f.RequestId);
        var decisions = (await connection.QueryAsync<(Guid RequestId, Guid UserId, string Username, string Decision, string[] Groups, string Comment, DateTime DecidedAt)>("""
            SELECT d.request_id, d.user_id, u.username, d.decision, d.groups, d.comment, d.decided_at
            FROM approval_decisions d JOIN users u ON u.id = d.user_id
            WHERE d.request_id = ANY(@ids)
            ORDER BY d.decided_at
            """, new { ids })).ToLookup(d => d.RequestId);

        return requests.Select(r =>
        {
            var rules = JsonSerializer.Deserialize<List<ApprovalRule>>(r.ApprovalsJson, ConfigurationService.Json) ?? [];
            var decided = decisions[r.Id].ToList();
            var isMine = r.RequestedBy == actor.UserId;
            var canApprove = r.Status == "pending"
                && rules.Any(rule => configuration.IsMember(actor.Username, rule.Group))
                && decided.All(d => d.UserId != actor.UserId)
                && (!isMine || r.SubmitterMayApprove);
            return new ChangeRequestInfo(r.Id, r.ToState, r.Comment, r.RequesterName, r.RequestedAt, r.Status,
                files[r.Id].Select(f => new ChangeRequestFile(f.FileId, f.Name, f.FolderPath, f.Version, f.FromState)).ToList(),
                Approvals.Progress(rules, decided.Where(d => d.Decision == "approved").Select(d => (d.Username, d.Groups)).ToList())
                    .Select(p => new ApprovalProgress(p.Rule.Group, configuration.Group(p.Rule.Group)?.Name ?? p.Rule.Group, p.Rule.Required, p.Approved)).ToList(),
                decided.Select(d => new ApprovalDecision(d.Username, d.Decision, d.Comment, d.DecidedAt)).ToList(),
                canApprove, isMine)
            {
                Problems = r.ProblemsJson is null ? [] : JsonSerializer.Deserialize<List<string>>(r.ProblemsJson, ConfigurationService.Json) ?? [],
            };
        }).ToList();
    }

    /// <summary>
    /// Puts files in another category, as a new version in the new lifecycle's first state. Needs the Change Category
    /// privilege, since a category change can un-release a file.
    /// </summary>
    public async Task<StateChangeResult> ChangeCategoryAsync(Actor actor, ChangeCategoryRequest request, CancellationToken cancellationToken = default)
    {
        var ids = request.FileIds.Distinct().ToArray();
        if (request.ChangesetId == Guid.Empty)
            throw OpenholdException.BadRequest("invalid_changeset", "A client-generated changeset id is required.");
        if (ids.Length is 0 or > MaxFiles)
            throw OpenholdException.BadRequest("invalid_count", $"Change the category of between 1 and {MaxFiles} files at a time.");

        await using var connection = await db.Primary.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (await FindExistingAsync(connection, transaction, actor, request.ChangesetId) is { } existing)
            return existing;

        var configuration = (await ConfigurationService.LoadAsync(connection, transaction, options.Value)).Configuration;
        var category = configuration.Category(request.CategoryId)
            ?? throw OpenholdException.BadRequest("unknown_category", $"There is no category '{request.CategoryId}'.");
        await connection.ExecuteAsync("SELECT id FROM files WHERE id = ANY(@ids) ORDER BY id FOR UPDATE", new { ids }, transaction);
        var files = (await connection.QueryAsync<FileState>("""
            SELECT f.id AS Id, f.name AS Name, fo.path AS FolderPath, f.category_id AS CategoryId, f.latest_version AS Version,
                   v.state AS State, (v.vault_properties || v.properties)::text AS PropertiesJson, u.username AS LockedBy
            FROM files f
            JOIN folders fo ON fo.id = f.folder_id
            JOIN file_versions v ON v.file_id = f.id AND v.version = f.latest_version
            LEFT JOIN locks l ON l.file_id = f.id
            LEFT JOIN users u ON u.id = l.user_id
            WHERE f.id = ANY(@ids) AND f.deleted_at IS NULL
            ORDER BY lower(f.name)
            """, new { ids }, transaction)).ToList();
        if (files.Count != ids.Length)
            throw OpenholdException.NotFound("Some of these files are no longer in the vault.");

        // A category decides the lifecycle, so moving a file to another one could skip approvals or release rules.
        if (!configuration.HasPrivilege(actor.Username, Privileges.ChangeCategory))
        {
            throw new OpenholdException(StatusCodes.Status403Forbidden, "not_administrator",
                "Changing a file's category needs the Change Category privilege, because the category decides how it's released. Ask your Openhold administrator.");
        }
        var problems = new List<StateChangeProblem>();
        foreach (var file in files)
        {
            if (string.Equals(file.CategoryId, category.Id, StringComparison.OrdinalIgnoreCase))
                problems.Add(new StateChangeProblem(file.Id, file.Name, $"{file.Name} is already {category.Name}"));
            if (file.LockedBy is { } holder)
                problems.Add(new StateChangeProblem(file.Id, file.Name, $"{file.Name} is checked out to {holder}; check it in first"));
        }
        if (problems.Count > 0)
            throw Refused(problems);

        var initial = configuration.Lifecycle(category.LifecycleId)?.Initial.Name;
        var comment = string.IsNullOrWhiteSpace(request.Comment) ? $"Category changed to {category.Name}" : $"Category changed to {category.Name}: {request.Comment.Trim()}";
        await connection.ExecuteAsync("""
            INSERT INTO changesets (id, user_id, site_id, machine, comment) VALUES (@id, @userId, @siteId, @machine, @comment)
            """, new { id = request.ChangesetId, userId = actor.UserId, siteId = actor.SiteId, machine = actor.Machine, comment }, transaction);
        var versions = new List<CommittedVersion>();
        foreach (var file in files)
        {
            var (version, name, folderId) = await connection.QuerySingleAsync<(int, string, Guid)>(
                "UPDATE files SET latest_version = latest_version + 1, category_id = @category WHERE id = @id RETURNING latest_version, name, folder_id",
                new { id = file.Id, category = category.Id }, transaction);
            await connection.ExecuteAsync("""
                INSERT INTO file_versions (file_id, version, changeset_id, length, content_hash, manifest, origin_site, properties, thumbnail, state, vault_properties)
                SELECT file_id, @version, @changesetId, length, content_hash, manifest, origin_site, properties, thumbnail, @initial, vault_properties
                FROM file_versions WHERE file_id = @fileId AND version = @version - 1
                """, new { fileId = file.Id, version, changesetId = request.ChangesetId, initial }, transaction);
            await connection.ExecuteAsync("""
                INSERT INTO file_references (parent_file_id, parent_version, child_file_id, child_version, ref_path)
                SELECT parent_file_id, @version, child_file_id, child_version, ref_path
                FROM file_references WHERE parent_file_id = @fileId AND parent_version = @version - 1
                """, new { fileId = file.Id, version }, transaction);
            versions.Add(new CommittedVersion(file.Id, folderId, name, version));
        }
        await connection.ExecuteAsync("""
            INSERT INTO audit_events (user_id, site_id, action, file_id, changeset_id, data)
            SELECT @userId, @siteId, 'category_change', id, @changesetId, jsonb_build_object('category', @category::text) FROM unnest(@ids) AS id
            """, new { userId = actor.UserId, siteId = actor.SiteId, changesetId = request.ChangesetId, category = category.Id, ids }, transaction);
        await transaction.CommitAsync(cancellationToken);
        return new StateChangeResult(request.ChangesetId, true, false, versions, []);
    }
}

/// <summary>
/// Approval rules and the sign-offs given. Each person counts towards one rule only, so a transition needing one
/// Engineering and one Quality approval needs two different people even if someone is in both groups.
/// </summary>
internal static class Approvals
{
    /// <summary>Combines rules: every group listed must sign; where a group is listed twice, the larger count applies.</summary>
    public static List<ApprovalRule> Merge(IEnumerable<ApprovalRule> rules) =>
        rules.GroupBy(r => r.Group, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ApprovalRule(g.First().Group, g.Max(r => r.Required)))
            .ToList();

    public static bool Satisfied(IReadOnlyList<ApprovalRule> rules, IReadOnlyList<(string Username, string[] Groups)> approvals) =>
        Progress(rules, approvals).All(p => p.Approved >= p.Rule.Required);

    /// <summary>
    /// How many approvals count towards each rule, giving each person to one rule. Rules with the fewest people able
    /// to sign are filled first, so someone in two groups is used where they're most needed.
    /// </summary>
    public static List<(ApprovalRule Rule, int Approved)> Progress(IReadOnlyList<ApprovalRule> rules, IReadOnlyList<(string Username, string[] Groups)> approvals)
    {
        bool Eligible((string Username, string[] Groups) a, ApprovalRule r) => a.Groups.Contains(r.Group, StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<ApprovalRule, int>();
        foreach (var rule in rules.OrderBy(r => approvals.Count(a => Eligible(a, r))))
        {
            var taken = approvals.Where(a => Eligible(a, rule) && !used.Contains(a.Username)).Take(rule.Required).ToList();
            foreach (var approval in taken)
                used.Add(approval.Username);
            counts[rule] = taken.Count;
        }
        return rules.Select(r => (r, counts[r])).ToList();
    }
}
