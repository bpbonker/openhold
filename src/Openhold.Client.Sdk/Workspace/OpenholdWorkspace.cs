using System.Globalization;
using Openhold.Core.Content;
using Openhold.Core.Contracts;
using Openhold.Core.Files;

namespace Openhold.Client.Sdk.Workspace;

public enum FileState
{
    /// <summary>Only on this PC; it will be added on check-in.</summary>
    NotInVault,
    UpToDate,
    /// <summary>A newer version is in the vault.</summary>
    Outdated,
    /// <summary>Changed on this PC without being checked out.</summary>
    ModifiedLocally,
    CheckedOutToMe,
    CheckedOutToSomeoneElse,
    /// <summary>In the vault but not on this PC.</summary>
    NotDownloaded,
}

/// <summary>The copy on this PC, whoever has the file checked out: what Autodesk Vault shows as the status bubble.</summary>
public enum LocalCopy
{
    /// <summary>Not on this PC (hollow bubble).</summary>
    None,
    /// <summary>The latest version (green).</summary>
    UpToDate,
    /// <summary>An older version (red).</summary>
    Outdated,
    /// <summary>Changed on this PC without being checked out (amber).</summary>
    ChangedHere,
    /// <summary>Only on this PC: not in the vault yet.</summary>
    OnlyHere,
}

public sealed record FileStatus(string LocalPath, FileState State, VaultFileInfo? File, string Description)
{
    /// <summary>The state of this PC's copy, independent of who has the file checked out.</summary>
    public LocalCopy LocalCopy { get; init; }
}

/// <summary>A search result with its status on this PC, where it lives, and what matched.</summary>
public sealed record WorkspaceSearchHit(FileStatus Status, string FolderPath, string? Match);

public sealed record WorkspaceSearchResults(IReadOnlyList<WorkspaceSearchHit> Hits, bool Truncated);

/// <summary>A vault folder combined with what is on this PC. <see cref="Subfolders"/> are folder names.</summary>
public sealed record WorkspaceFolder(string VaultPath, string LocalPath, IReadOnlyList<string> Subfolders, IReadOnlyList<FileStatus> Files);

/// <summary>What happened to one file, in words a user can act on.</summary>
public sealed record FileOutcome(string LocalPath, string Message);

/// <summary>Result of a workspace operation. <see cref="Summary"/> is written for the person, not for logs.</summary>
public sealed record WorkspaceResult(bool Succeeded, string Summary, IReadOnlyList<FileOutcome> Files)
{
    /// <summary>Local files that were replaced with a newer version from the vault during the operation.</summary>
    public IReadOnlyList<string> UpdatedFiles { get; init; } = [];

    /// <summary>
    /// The server couldn't be reached, so whether a check-in was committed is unknown. Nothing may be rolled back:
    /// checking in the same files again finishes it exactly once.
    /// </summary>
    public bool Retryable { get; init; }

    /// <summary>A state change was sent for approval rather than made: the files haven't changed yet.</summary>
    public bool AwaitingApproval { get; init; }
}

public sealed class CheckInOptions
{
    /// <summary>Keep the files checked out after checking in (save a version and carry on working).</summary>
    public bool KeepCheckedOut { get; init; }

    /// <summary>Vault properties to record for a file, e.g. read from its title block or iProperties.</summary>
    public Func<string, IReadOnlyDictionary<string, string>?>? Properties { get; init; }

    /// <summary>Properties kept in the vault only, because the file can't hold them; null keeps the previous version's.</summary>
    public Func<string, IReadOnlyDictionary<string, string>?>? VaultProperties { get; init; }

    /// <summary>
    /// Local paths of the files a file references (xrefs, assembly components). Return null for a file whose
    /// references weren't read (e.g. another drawing of the project set): it keeps the previous version's. An empty
    /// list means the file references nothing.
    /// </summary>
    public Func<string, IReadOnlyList<string>?>? References { get; init; }

    /// <summary>
    /// Check in every drawing of an AutoCAD Electrical project with any one of them, as project-wide changes touch
    /// them all (the default). Off for callers that already choose the batch, such as an import in dependency order.
    /// </summary>
    public bool WholeProjectSets { get; init; } = true;

    /// <summary>A PNG preview for a file, stored with its new version (e.g. from <c>ShellThumbnails</c>).</summary>
    public Func<string, byte[]?>? Thumbnail { get; init; }

    /// <summary>Files that get a new version even if their contents didn't change, because their vault properties did.</summary>
    public Func<string, bool>? PropertiesChanged { get; init; }
}

/// <summary>
/// A user's local working folder mirrored from the vault, shared by the CAD add-ins and the desktop app.
/// Files the user has not checked out are kept read-only, so CAD tools cannot save over them by accident.
/// Nothing the user changed is ever overwritten silently: local edits are copied to
/// <c>.openhold/backup</c> first and the user is told where.
/// </summary>
/// <remarks>Check-in and check-out never change a file's revision; they only add versions.</remarks>
public sealed class OpenholdWorkspace
{
    private readonly OpenholdClient _client;
    private readonly WorkspaceStateStore _state;

    public OpenholdWorkspace(string root, OpenholdClient client)
    {
        Root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        Directory.CreateDirectory(Root);
        _client = client;
        _state = new WorkspaceStateStore(Root);
    }

    public string Root { get; }

    public string ToVaultPath(string localPath)
    {
        var relative = Relative(localPath);
        return "$/" + relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    public string ToLocalPath(string vaultPath)
    {
        var trimmed = vaultPath.Replace('\\', '/').TrimStart('$').TrimStart('/');
        return Path.Combine(Root, trimmed.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// Adds every file of any AutoCAD Electrical project a path belongs to, so project-wide edits
    /// (wire numbers, tags, title blocks) are always checked out and in together.
    /// </summary>
    public IReadOnlyList<string> ExpandProjectSets(IEnumerable<string> localPaths)
    {
        var result = new List<string>();
        var projectsByFolder = new Dictionary<string, IReadOnlyList<AcadeProject>>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in localPaths.Select(Path.GetFullPath))
        {
            result.Add(path);
            if (FileKinds.IsAcadeProject(path) && File.Exists(path))
            {
                result.AddRange(AcadeProject.Load(path).ProjectSetFiles());
                continue;
            }
            foreach (var project in ProjectsContaining(path, projectsByFolder))
                result.AddRange(project.ProjectSetFiles());
        }

        return result.Where(p => !FileKinds.IsIgnored(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>AutoCAD Electrical projects (in the drawing's folder or the one above) that list this drawing.</summary>
    private static IEnumerable<AcadeProject> ProjectsContaining(string drawingPath, Dictionary<string, IReadOnlyList<AcadeProject>> projectsByFolder)
    {
        if (!Path.GetExtension(drawingPath).Equals(".dwg", StringComparison.OrdinalIgnoreCase))
            yield break;
        var folder = Path.GetDirectoryName(drawingPath)!;
        foreach (var candidate in new[] { folder, Path.GetDirectoryName(folder) }.OfType<string>())
        {
            if (!projectsByFolder.TryGetValue(candidate, out var projects))
            {
                projects = Directory.Exists(candidate)
                    ? Directory.GetFiles(candidate, "*.wdp").Select(AcadeProject.Load).ToList()
                    : [];
                projectsByFolder[candidate] = projects;
            }
            foreach (var project in projects.Where(p => p.Drawings.Any(d => d.Path.Equals(drawingPath, StringComparison.OrdinalIgnoreCase))))
                yield return project;
        }
    }

    public async Task<IReadOnlyList<FileStatus>> GetStatusAsync(IEnumerable<string> localPaths, CancellationToken cancellationToken = default)
    {
        var paths = localPaths.Select(Path.GetFullPath).ToList();
        var resolved = await ResolveAsync(paths, cancellationToken);
        var statuses = new List<FileStatus>(paths.Count);
        foreach (var (path, file) in resolved)
            statuses.Add(await StatusOfAsync(path, file, cancellationToken));
        return statuses;
    }

    /// <summary>
    /// A vault folder as this PC sees it: the vault's files with their local status, plus files that exist
    /// only in the local folder (not yet added). One server round trip for the whole folder.
    /// </summary>
    public async Task<WorkspaceFolder> GetFolderAsync(string vaultFolderPath, CancellationToken cancellationToken = default)
    {
        var localFolder = ToLocalPath(vaultFolderPath);
        FolderListing? listing = null;
        try
        {
            listing = await _client.ListFolderAsync(vaultFolderPath, cancellationToken);
        }
        catch (OpenholdApiException ex) when (ex.Code == "not_found")
        {
            // A folder that so far exists only on this PC.
        }

        var files = new List<FileStatus>();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in listing?.Files ?? [])
        {
            var path = Path.Combine(localFolder, file.Name);
            known.Add(path);
            files.Add(await StatusOfAsync(path, file, cancellationToken));
        }

        if (Directory.Exists(localFolder))
        {
            foreach (var path in Directory.GetFiles(localFolder).Where(p => !known.Contains(p) && !FileKinds.IsIgnored(p) && !p.EndsWith(".openhold-download", StringComparison.OrdinalIgnoreCase)))
                files.Add(await StatusOfAsync(path, null, cancellationToken));
        }

        var subfolders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in listing?.Folders ?? [])
            subfolders.Add(folder.Name);
        if (Directory.Exists(localFolder))
        {
            foreach (var directory in Directory.GetDirectories(localFolder).Select(Path.GetFileName).OfType<string>())
            {
                if (directory != ".openhold" && !FileKinds.IsIgnored(Path.Combine(localFolder, directory, "x")))
                    subfolders.Add(directory);
            }
        }

        return new WorkspaceFolder(vaultFolderPath, localFolder, subfolders.ToList(),
            files.OrderBy(f => Path.GetFileName(f.LocalPath), StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>
    /// Downloads one version to a read-only copy named like <c>Bracket (v2).ipt</c>, outside the workspace so it
    /// can't be mistaken for the current file, and returns its path.
    /// </summary>
    public async Task<string> GetVersionCopyAsync(Guid fileId, string fileName, int version, CancellationToken cancellationToken = default)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Openhold", "Versions", fileId.ToString("N"));
        Directory.CreateDirectory(folder);
        var copy = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(fileName)} (v{version}){Path.GetExtension(fileName)}");
        if (File.Exists(copy))
        {
            try
            {
                SetReadOnly(copy, false);
                File.Delete(copy);
            }
            catch (IOException)
            {
                // The earlier copy is still open (e.g. in AutoCAD): leave it alone and make another.
                copy = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(fileName)} (v{version}, {DateTime.Now:HHmmss}){Path.GetExtension(fileName)}");
            }
        }
        await using (var output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write))
            await _client.DownloadAsync(fileId, version, output, cancellationToken: cancellationToken);
        SetReadOnly(copy, true);
        return copy;
    }

    /// <summary>
    /// Downloads a file's newest published PDF (the stamped PDF made when it was released) to a read-only copy for
    /// viewing or sending. Returns its path and the version it was made from, or null if none has been published.
    /// </summary>
    public async Task<(string Path, int Version)?> GetPublishedPdfAsync(Guid fileId, string fileName, CancellationToken cancellationToken = default)
    {
        if (await _client.GetPublishedPdfAsync(fileId, cancellationToken: cancellationToken) is not { } pdf)
            return null;
        var folder = Path.Combine(Path.GetTempPath(), "Openhold", "Published", fileId.ToString("N"));
        Directory.CreateDirectory(folder);
        var copy = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(fileName)} (v{pdf.Version}).pdf");
        if (!File.Exists(copy))
        {
            var partial = copy + ".partial";
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write))
                await _client.DownloadAsync(pdf.Manifest, output, cancellationToken: cancellationToken);
            File.Move(partial, copy, overwrite: true);
            SetReadOnly(copy, true);
        }
        return (copy, pdf.Version);
    }

    /// <summary>
    /// Makes an earlier version the newest one (as a new version, so history is kept) and updates this PC's copy.
    /// Checks the file out first if needed; local edits are backed up before the copy is replaced.
    /// </summary>
    public async Task<WorkspaceResult> RestoreVersionAsync(string localPath, int version, string? comment = null, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(localPath);
        if (RejectOutside([path]) is { } outside)
            return outside;
        var name = Path.GetFileName(path);
        if ((await ResolveAsync([path], cancellationToken))[0].File is not { } file)
            return new WorkspaceResult(false, $"{name} isn't in the vault, so it has no earlier versions.", []);

        if (!IsMine(file))
        {
            var checkout = await _client.CheckOutAsync([file.Id], cancellationToken);
            if (!checkout.Succeeded)
            {
                return new WorkspaceResult(false, $"{name} couldn't be restored because it couldn't be checked out.",
                    checkout.Conflicts.Select(c => new FileOutcome(path, HeldBy(name, c.Username, c.Machine, c.SiteId, c.AcquiredAt))).ToList());
            }
        }

        var restored = (await _client.RestoreVersionAsync(file.Id, new RestoreRequest(Guid.NewGuid(), version, comment), cancellationToken)).Versions.Single();
        var backup = await BackupIfModifiedAsync(path, _state.Get(Relative(path)), cancellationToken);
        await DownloadToAsync(path, file.Id, restored.Version, cancellationToken);

        var outcomes = backup is null ? new List<FileOutcome>() : [new FileOutcome(path, $"Your local changes to {name} were kept in {backup}.")];
        return new WorkspaceResult(true, $"Restored version {version} of {name}. It is now version {restored.Version}, and earlier versions are all still there.", outcomes)
        {
            UpdatedFiles = [path],
        };
    }

    /// <summary>
    /// Moves files to another lifecycle state together (e.g. releases a drawing and its parts), or sends them for
    /// approval when the transition needs it. Nothing is downloaded: the content doesn't change. A dropped
    /// connection is retried with the same id, so the change happens once.
    /// </summary>
    public async Task<WorkspaceResult> ChangeStateAsync(IEnumerable<string> localPaths, string toState, string? comment, CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveForLifecycleAsync(localPaths, cancellationToken);
        if (resolved.Refusal is { } refusal)
            return refusal;
        var request = new ChangeStateRequest(Guid.NewGuid(), resolved.Files.Values.ToList(), toState, comment);
        try
        {
            var result = await WithOneRetryAsync(() => _client.ChangeStateAsync(request, cancellationToken));
            var what = Plural(resolved.Files.Count, "file", "files");
            return result.Applied
                ? new WorkspaceResult(true, $"Changed {what} to {toState}.", [])
                : new WorkspaceResult(true, $"Sent {what} for approval. They'll change to {toState} once the approvals are in; you'll find the request under Approvals.", []) { AwaitingApproval = true };
        }
        catch (OpenholdApiException ex) when (ex.Code is "state_change_refused" or "not_found" or "invalid_state")
        {
            return Refused(ex, resolved.Files);
        }
    }

    /// <summary>Puts files in another category (and so another lifecycle and revision scheme).</summary>
    public async Task<WorkspaceResult> ChangeCategoryAsync(IEnumerable<string> localPaths, string categoryId, string categoryName, CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveForLifecycleAsync(localPaths, cancellationToken);
        if (resolved.Refusal is { } refusal)
            return refusal;
        var request = new ChangeCategoryRequest(Guid.NewGuid(), resolved.Files.Values.ToList(), categoryId);
        try
        {
            await WithOneRetryAsync(() => _client.ChangeCategoryAsync(request, cancellationToken));
            return new WorkspaceResult(true, $"Moved {Plural(resolved.Files.Count, "file", "files")} to the {categoryName} category.", []);
        }
        catch (OpenholdApiException ex) when (ex.Code is "state_change_refused" or "not_found" or "unknown_category" or "not_administrator")
        {
            return Refused(ex, resolved.Files);
        }
    }

    private async Task<(Dictionary<string, Guid> Files, WorkspaceResult? Refusal)> ResolveForLifecycleAsync(IEnumerable<string> localPaths, CancellationToken cancellationToken)
    {
        var paths = localPaths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (RejectOutside(paths) is { } outside)
            return ([], outside);
        var resolved = await ResolveAsync(paths, cancellationToken);
        var missing = resolved.Where(r => r.File is null).Select(r => new FileOutcome(r.Path, $"{Path.GetFileName(r.Path)} isn't in the vault yet. Check it in first.")).ToList();
        if (missing.Count > 0)
            return ([], new WorkspaceResult(false, "Nothing was changed.", missing));
        return (resolved.ToDictionary(r => r.Path, r => r.File!.Id, StringComparer.OrdinalIgnoreCase), null);
    }

    /// <summary>The server's reasons, one per file where it gave one.</summary>
    private static WorkspaceResult Refused(OpenholdApiException ex, Dictionary<string, Guid> files)
    {
        var byId = files.ToDictionary(f => f.Value, f => f.Key);
        var outcomes = new List<FileOutcome>();
        if (ex.Details is { ValueKind: System.Text.Json.JsonValueKind.Object } details
            && details.TryGetProperty("problems", out var problems) && problems.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var problem in problems.EnumerateArray())
            {
                var id = problem.TryGetProperty("fileId", out var fileId) && fileId.TryGetGuid(out var guid) ? guid : Guid.Empty;
                var message = problem.TryGetProperty("message", out var text) ? text.GetString() : null;
                if (message is not null)
                    outcomes.Add(new FileOutcome(byId.GetValueOrDefault(id) ?? "", char.ToUpperInvariant(message[0]) + message[1..] + "."));
            }
        }
        return outcomes.Count > 0
            ? new WorkspaceResult(false, "Nothing was changed.", outcomes)
            : new WorkspaceResult(false, ex.Message, []);
    }

    /// <summary>Retries once after a dropped connection; safe because the request carries the same id both times.</summary>
    private static async Task<T> WithOneRetryAsync<T>(Func<Task<T>> send)
    {
        try
        {
            return await send();
        }
        catch (HttpRequestException)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            return await send();
        }
    }

    /// <summary>
    /// Moves files to the vault's trash bin (all-or-nothing) and removes them from this PC. Local edits are kept in a
    /// backup first. Files that exist only on this PC are moved to the backup folder rather than erased.
    /// </summary>
    public async Task<WorkspaceResult> DeleteAsync(IEnumerable<string> localPaths, CancellationToken cancellationToken = default)
    {
        var paths = localPaths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (RejectOutside(paths) is { } outside)
            return outside;
        var resolved = await ResolveAsync(paths, cancellationToken);
        var inVault = resolved.Where(r => r.File is not null).ToList();

        if (inVault.Count > 0)
        {
            try
            {
                await _client.DeleteFilesAsync(inVault.Select(r => r.File!.Id), cancellationToken);
            }
            catch (OpenholdApiException ex) when (ex.Code is "still_used" or "checked_out" or "not_found")
            {
                return new WorkspaceResult(false, ex.Message, []);
            }
        }

        var outcomes = new List<FileOutcome>();
        foreach (var (path, file) in resolved)
        {
            if (!File.Exists(path))
                continue;
            var backup = file is null ? NewBackupPath(path) : await BackupIfModifiedAsync(path, _state.Get(Relative(path)), cancellationToken);
            SetReadOnly(path, false);
            if (file is null)
                File.Move(path, backup!, overwrite: true);
            else
                File.Delete(path);
            _state.Remove(Relative(path));
            if (backup is not null)
                outcomes.Add(new FileOutcome(path, $"A copy of {Path.GetFileName(path)} was kept in {backup}."));
        }

        var trash = inVault.Count == 0 ? "" : " Files from the vault are in the Trash and can be restored with their history.";
        return new WorkspaceResult(true, $"Deleted {Plural(paths.Count, "file", "files")}.{trash}", outcomes);
    }

    /// <summary>Brings files back from the trash bin and downloads them to this PC.</summary>
    public async Task<WorkspaceResult> RestoreFromTrashAsync(IReadOnlyList<TrashItem> items, CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.RestoreFromTrashAsync(items.Select(i => i.FileId), cancellationToken);
        }
        catch (OpenholdApiException ex) when (ex.Code == "name_conflict")
        {
            return new WorkspaceResult(false, ex.Message, []);
        }
        var paths = items.Select(i => Path.Combine(ToLocalPath(i.FolderPath), i.Name)).ToList();
        await GetLatestAsync(paths, cancellationToken);
        return new WorkspaceResult(true, $"Restored {Plural(items.Count, "file", "files")} from the Trash, with their full history.",
            paths.Select(p => new FileOutcome(p, $"{Path.GetFileName(p)} is back in {Path.GetDirectoryName(p)}.")).ToList()) { UpdatedFiles = paths };
    }

    /// <summary>
    /// Renames a file in the vault and on this PC. Every file that uses it (Inventor assemblies, drawings,
    /// AutoCAD Electrical projects) has its references repaired and is checked in with it, as one changeset.
    /// </summary>
    public Task<WorkspaceResult> RenameAsync(string localPath, string newName, IReadOnlyList<IReferenceRepairer> repairers, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(localPath);
        return RelocateAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [path] = Path.Combine(Path.GetDirectoryName(path)!, newName.Trim()) },
            $"Renamed {Path.GetFileName(path)} to {newName.Trim()}", repairers, cancellationToken);
    }

    /// <summary>Moves files to another vault folder, repairing every file that uses them, as one changeset.</summary>
    public Task<WorkspaceResult> MoveAsync(IEnumerable<string> localPaths, string targetVaultFolder, IReadOnlyList<IReferenceRepairer> repairers, CancellationToken cancellationToken = default)
    {
        var target = ToLocalPath(targetVaultFolder);
        var moves = localPaths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(p => p, p => Path.Combine(target, Path.GetFileName(p)), StringComparer.OrdinalIgnoreCase);
        return RelocateAsync(moves, $"Moved to {targetVaultFolder}", repairers, cancellationToken);
    }

    private async Task<WorkspaceResult> RelocateAsync(
        Dictionary<string, string> moves, string comment, IReadOnlyList<IReferenceRepairer> repairers, CancellationToken cancellationToken)
    {
        if (RejectOutside([.. moves.Keys, .. moves.Values]) is { } outside)
            return outside;
        foreach (var (from, to) in moves)
        {
            if (string.Equals(from, to, StringComparison.Ordinal))
                return new WorkspaceResult(false, $"{Path.GetFileName(from)} already has that name and folder.", []);
            // A change of letter case only is a rename of the same file, not a clash.
            if (File.Exists(to) && !string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
                return new WorkspaceResult(false, $"Nothing was changed: {Path.GetFileName(to)} already exists in {Path.GetDirectoryName(to)}.", []);
        }

        var movedProject = moves.Keys.FirstOrDefault(FileKinds.IsAcadeProject);
        if (movedProject is not null)
        {
            return new WorkspaceResult(false,
                $"Nothing was changed: {Path.GetFileName(movedProject)} is an AutoCAD Electrical project, which lists its drawings relative to itself. " +
                "Rename or move it from AutoCAD Electrical's Project Manager.", []);
        }

        var resolved = await ResolveAsync(moves.Keys.ToList(), cancellationToken);
        var vaultFiles = resolved.Where(r => r.File is not null).ToDictionary(r => r.Path, r => r.File!, StringComparer.OrdinalIgnoreCase);

        // A drawing's own xrefs may be stored relative to its folder, which a move to another folder would break.
        foreach (var (from, file) in vaultFiles)
        {
            var changesFolder = !string.Equals(Path.GetDirectoryName(from), Path.GetDirectoryName(moves[from]), StringComparison.OrdinalIgnoreCase);
            if (changesFolder && Path.GetExtension(from).Equals(".dwg", StringComparison.OrdinalIgnoreCase)
                && (await _client.GetUsesAsync(file.Id, cancellationToken: cancellationToken)).Count > 0)
            {
                return new WorkspaceResult(false,
                    $"Nothing was changed: {Path.GetFileName(from)} has xrefs or images of its own, which may be stored relative to its folder. " +
                    "Move it together with them in AutoCAD for now; renaming it in place is fine.", []);
            }
        }

        // Everything that refers to the files being moved: Inventor parents (from the vault) and ACADE projects.
        var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in vaultFiles.Values)
        {
            foreach (var parent in await _client.GetWhereUsedAsync(file.Id, cancellationToken))
                parents.Add(Path.Combine(ToLocalPath(parent.Path), parent.Name));
        }
        var projectCache = new Dictionary<string, IReadOnlyList<AcadeProject>>(StringComparer.OrdinalIgnoreCase);
        foreach (var from in moves.Keys)
        {
            foreach (var project in ProjectsContaining(from, projectCache))
                parents.Add(project.ProjectPath);
        }

        var unrepairable = parents.Where(p => !repairers.Any(r => r.CanRepair(p))).ToList();
        if (unrepairable.Count > 0)
        {
            return new WorkspaceResult(false,
                "Nothing was changed: these files use what you're renaming or moving, and Openhold can't update the references inside them yet. " +
                "Rename it from within their CAD application instead.",
                unrepairable.Select(p => new FileOutcome(p, $"{Path.GetFileName(p)} uses it.")).ToList());
        }

        // Check out everything involved, all-or-nothing, at the latest version. Files already checked out to you
        // are refused: their work in progress would otherwise be checked in with the repair.
        var toLock = vaultFiles.Keys.Concat(parents).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var locked = ExpandProjectSets(toLock);
        var inProgress = (await GetStatusAsync(locked, cancellationToken)).Where(st => st.State == FileState.CheckedOutToMe).ToList();
        if (inProgress.Count > 0)
        {
            return new WorkspaceResult(false, "Nothing was changed: check these files in (or undo their check-out) first, as they would be updated too.",
                inProgress.Select(st => new FileOutcome(st.LocalPath, $"{Path.GetFileName(st.LocalPath)} is checked out to you.")).ToList());
        }
        if (toLock.Count > 0)
        {
            var checkout = await CheckOutAsync(toLock, cancellationToken);
            if (!checkout.Succeeded)
                return checkout with { Summary = $"Nothing was changed. {checkout.Summary}" };
        }

        // One round trip for every file that will be checked in: the moved files and their parents.
        var involved = new Dictionary<string, VaultFileInfo>(vaultFiles, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, file) in await ResolveAsync(parents.ToList(), cancellationToken))
        {
            if (file is not null)
                involved.TryAdd(path, file); // parents not in the vault are only repaired locally
        }

        var backups = parents.Where(File.Exists).ToDictionary(p => p, p => { var b = NewBackupPath(p, "before-repair-"); File.Copy(p, b, true); return b; },
            StringComparer.OrdinalIgnoreCase);
        var moved = new List<(string From, string To)>();
        CheckInResult result;
        List<(string Path, CheckInFile Part, string Hash, long Length, DateTime LastWrite)> uploads = [];
        try
        {
            foreach (var (from, to) in moves)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                SetReadOnly(from, false);
                File.Move(from, to);
                moved.Add((from, to));
            }

            foreach (var parent in parents)
            {
                var current = moves.GetValueOrDefault(parent, parent);
                var repairer = repairers.First(r => r.CanRepair(current));
                var foundReference = false;
                foreach (var (from, to) in moves)
                    foundReference |= repairer.Repair(current, from, to);
                // The vault says this file uses what is moving; if no reference was found to repair, checking it in
                // unchanged would leave it pointing at the old name.
                if (!foundReference)
                    throw new InvalidOperationException($"Couldn't find the reference inside {Path.GetFileName(current)} that needs updating.");
            }

            var folderIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, file) in involved)
            {
                var current = moves.GetValueOrDefault(path, path);
                string? newName = null;
                Guid? newFolderId = null;
                if (moves.ContainsKey(path))
                {
                    if (!string.Equals(Path.GetFileName(current), file.Name, StringComparison.Ordinal))
                        newName = Path.GetFileName(current);
                    var folderPath = ToVaultPath(current)[..ToVaultPath(current).LastIndexOf('/')];
                    if (!folderIds.TryGetValue(folderPath, out var folderId))
                        folderIds[folderPath] = folderId = (await _client.EnsureFolderAsync(folderPath, cancellationToken)).Id;
                    if (folderId != file.FolderId)
                        newFolderId = folderId;
                }

                var lastWrite = File.GetLastWriteTimeUtc(current);
                byte[] manifest;
                await using (var stream = OpenSharedRead(current))
                    manifest = await _client.UploadContentAsync(stream, cancellationToken);
                var decoded = ManifestCodec.Decode(manifest);
                uploads.Add((current, new CheckInFile(file.Id, null, null, manifest, null, null, NewName: newName, NewFolderId: newFolderId),
                    decoded.FileHash.ToString(), decoded.Length, lastWrite));
            }

            result = await CommitWithRetryAsync(new CheckInRequest(Guid.NewGuid(), comment, uploads.Select(u => u.Part).ToList()), cancellationToken);
        }
        catch (Exception ex) when (ex is OpenholdApiException or HttpRequestException or IOException or UnauthorizedAccessException
                                       or InvalidOperationException or OperationCanceledException)
        {
            // Put everything back as it was: no half-renamed files, no half-repaired parents.
            foreach (var (from, to) in Enumerable.Reverse(moved))
            {
                if (File.Exists(to))
                    File.Move(to, from, overwrite: true);
            }
            foreach (var (parent, backup) in backups)
            {
                SetReadOnly(parent, false);
                File.Copy(backup, parent, overwrite: true);
            }
            try
            {
                await ReleaseAsync(locked);
            }
            catch (Exception releaseError) when (releaseError is HttpRequestException or TaskCanceledException or OpenholdApiException)
            {
                // The server is unreachable; the files stay checked out to you and can be released later.
            }
            var reason = ex is HttpRequestException or TaskCanceledException
                ? "Couldn't reach the Openhold server. If it comes back showing the new name, use Get Latest on this folder."
                : ex.Message;
            return new WorkspaceResult(false, $"Nothing was changed on this PC: {reason} Everything has been put back as it was.", []);
        }

        // Other drawings of an AutoCAD Electrical project came along with the check-out; they didn't change.
        await ReleaseAsync(locked.Except(involved.Keys, StringComparer.OrdinalIgnoreCase).ToList());

        foreach (var (from, _) in moved)
            _state.Remove(Relative(from));
        var outcomes = new List<FileOutcome>();
        foreach (var upload in uploads)
        {
            var committed = result.Versions.Single(v => v.FileId == upload.Part.FileId);
            _state.Set(Relative(upload.Path), new LocalFileRecord
            {
                FileId = committed.FileId, Version = committed.Version, ContentHash = upload.Hash, Length = upload.Length, LastWriteUtc = upload.LastWrite,
            });
            SetReadOnly(upload.Path, true);
            outcomes.Add(new FileOutcome(upload.Path, moves.Values.Contains(upload.Path, StringComparer.OrdinalIgnoreCase)
                ? $"{committed.Name} is now version {committed.Version}."
                : $"{committed.Name} was updated to use the new location (version {committed.Version})."));
        }

        var repaired = parents.Count == 0 ? "" : $" {Plural(parents.Count, "file that uses it was", "files that use it were")} updated to match.";
        return new WorkspaceResult(true, $"{comment}.{repaired}", outcomes) { UpdatedFiles = [.. moves.Values] };
    }

    /// <summary>
    /// Copy Design: copies files (e.g. an assembly and the parts it uses) to new names and checks the copies in as
    /// new files. Copies are made from the latest vault versions; a copied parent's references to copied children
    /// are repaired to point at the copies, while uncopied children (library parts) stay shared. Properties and
    /// previews come with the copies. <paramref name="copies"/> maps each original's local path to its copy's.
    /// </summary>
    public async Task<WorkspaceResult> CopyDesignAsync(
        IReadOnlyDictionary<string, string> copies, string comment, IReadOnlyList<IReferenceRepairer> repairers, CancellationToken cancellationToken = default)
    {
        var map = copies.ToDictionary(c => Path.GetFullPath(c.Key), c => Path.GetFullPath(c.Value), StringComparer.OrdinalIgnoreCase);
        if (RejectOutside([.. map.Keys, .. map.Values]) is { } outside)
            return outside;
        var taken = map.Values.Where(File.Exists).ToList();
        if (taken.Count > 0)
            return new WorkspaceResult(false, "Nothing was copied, because some new names already exist in that folder.",
                taken.Select(t => new FileOutcome(t, $"{Path.GetFileName(t)} already exists.")).ToList());
        if (map.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != map.Count)
            return new WorkspaceResult(false, "Nothing was copied, because two copies would get the same name.", []);

        var sources = (await ResolveAsync(map.Keys.ToList(), cancellationToken)).ToDictionary(r => r.Path, r => r.File, StringComparer.OrdinalIgnoreCase);

        // What each original uses, so copies can point at copied children and keep using uncopied ones.
        var uses = new Dictionary<string, IReadOnlyList<ReferenceInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, file) in sources)
            uses[path] = file is null ? [] : await _client.GetUsesAsync(file.Id, cancellationToken: cancellationToken);
        string LocalOf(ReferenceInfo child) => Path.Combine(ToLocalPath(child.Path), child.Name);

        var needsRepair = map.Keys.Where(p => uses[p].Any(u => map.ContainsKey(LocalOf(u)))).ToList();
        var unrepairable = needsRepair.Where(p => !repairers.Any(r => r.CanRepair(p))).ToList();
        if (unrepairable.Count > 0)
        {
            return new WorkspaceResult(false,
                "Nothing was copied: these files would need their references updated to point at the copies, and Openhold can't do that for this file type yet.",
                unrepairable.Select(p => new FileOutcome(p, $"{Path.GetFileName(p)} would need updating.")).ToList());
        }

        var created = new List<string>();
        var properties = new Dictionary<string, IReadOnlyDictionary<string, string>?>(StringComparer.OrdinalIgnoreCase);
        var vaultProperties = new Dictionary<string, IReadOnlyDictionary<string, string>?>(StringComparer.OrdinalIgnoreCase);
        var thumbnails = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var (original, copy) in map)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                if (sources[original] is { } file)
                {
                    await using var output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write);
                    await _client.DownloadAsync(file.Id, file.LatestVersion, output, cancellationToken: cancellationToken);
                }
                else
                {
                    File.Copy(original, copy);
                }
                SetReadOnly(copy, false);
                created.Add(copy);
            }

            foreach (var original in needsRepair)
            {
                var repairer = repairers.First(r => r.CanRepair(original));
                foreach (var child in uses[original].Select(LocalOf).Where(map.ContainsKey))
                {
                    // The vault will record that the copy uses the copied child, so the file must really say so.
                    if (!repairer.Repair(map[original], child, map[child]))
                        throw new IOException($"{Path.GetFileName(original)} doesn't refer to {Path.GetFileName(child)} where the vault says it does, so its copy couldn't be pointed at the copies.");
                }
            }

            foreach (var (original, copy) in map)
            {
                properties[copy] = sources[original] is { } source ? FileHeld(source) : null;
                vaultProperties[copy] = sources[original]?.VaultProperties;
                thumbnails[copy] = sources[original] is { } file ? await _client.GetThumbnailAsync(file.Id, cancellationToken: cancellationToken) : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OpenholdApiException or HttpRequestException
                                       or OperationCanceledException or InvalidOperationException)
        {
            DeleteCopies(created);
            return new WorkspaceResult(false, $"Nothing was copied: {ex.Message}", []);
        }
        var references = map.ToDictionary(m => m.Value, m => (IReadOnlyList<string>)uses[m.Key]
            .Select(LocalOf).Select(child => map.GetValueOrDefault(child, child)).ToList(), StringComparer.OrdinalIgnoreCase);

        var result = await CheckInAsync(map.Values.ToList(), comment, new CheckInOptions
        {
            Properties = p => properties.GetValueOrDefault(p),
            VaultProperties = p => vaultProperties.GetValueOrDefault(p),
            References = p => references.GetValueOrDefault(p) ?? [],
            Thumbnail = p => thumbnails.GetValueOrDefault(p),
        }, cancellationToken);
        if (result.Retryable)
        {
            // The server may already have the copies, so they must stay: checking them in again finishes the job.
            return result with { Summary = "Couldn't reach the Openhold server to confirm the copies. They are kept on this PC; check them in again when the connection is back." };
        }
        if (!result.Succeeded)
        {
            DeleteCopies(created);
            return result with { Summary = $"Nothing was copied. {result.Summary}" };
        }
        return result with { Summary = $"Copied {Plural(map.Count, "file", "files")}. The copies are new files in the vault; the originals are unchanged." };
    }

    /// <summary>
    /// Commits a changeset, resending it after a dropped connection. Resending is safe because the server applies a
    /// changeset id once and answers a repeat with the original result, so a commit is never lost or doubled.
    /// </summary>
    private async Task<CheckInResult> CommitWithRetryAsync(CheckInRequest request, CancellationToken cancellationToken)
    {
        TimeSpan[] waits = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await _client.CheckInAsync(request, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && attempt < waits.Length && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(waits[attempt], cancellationToken);
            }
        }
    }

    /// <summary>Releases check-outs this operation took, leaving the files as the vault has them (read-only).</summary>
    private async Task ReleaseAsync(IReadOnlyList<string> paths)
    {
        var ids = (await ResolveAsync(paths, CancellationToken.None)).Where(r => r.File is not null).ToList();
        if (ids.Count == 0)
            return;
        await _client.UndoCheckOutAsync(ids.Select(r => r.File!.Id), CancellationToken.None);
        foreach (var (path, _) in ids.Where(r => File.Exists(r.Path)))
            SetReadOnly(path, true);
    }

    /// <summary>The properties that come from the file itself: everything shown except those kept in the vault only.</summary>
    private static Dictionary<string, string> FileHeld(VaultFileInfo file) =>
        file.Properties.Where(p => !file.VaultProperties.ContainsKey(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    private static void DeleteCopies(IEnumerable<string> copies)
    {
        foreach (var copy in copies.Where(File.Exists))
        {
            SetReadOnly(copy, false);
            File.Delete(copy);
        }
    }

    /// <summary>
    /// Edits properties in one step, for one file or many: checks the files out, writes the new values into each
    /// file (iProperties, title blocks) and checks them in as a new version, all-or-nothing. Files without embedded
    /// properties (PDFs, Office, CODESYS) keep the values in the vault only. Only the properties given change;
    /// an empty value removes one. The revision changes only if it is one of the edited properties.
    /// </summary>
    public async Task<WorkspaceResult> EditPropertiesAsync(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> changes, string comment, IReadOnlyList<IPropertyWriter> writers,
        CancellationToken cancellationToken = default)
    {
        var edits = changes.ToDictionary(c => Path.GetFullPath(c.Key), c => c.Value, StringComparer.OrdinalIgnoreCase);
        var paths = edits.Keys.ToList();
        if (RejectOutside(paths) is { } outside)
            return outside;

        var statuses = await GetStatusAsync(paths, cancellationToken);
        var blocked = new List<FileOutcome>();
        foreach (var status in statuses)
        {
            var name = Path.GetFileName(status.LocalPath);
            if (status.File is null)
                blocked.Add(new FileOutcome(status.LocalPath, $"{name} isn't in the vault yet. Check it in first."));
            else if (status.State == FileState.CheckedOutToMe)
                blocked.Add(new FileOutcome(status.LocalPath, $"{name} is checked out to you. Check it in (or undo) first, or change its properties where you're editing it."));
            else if (FileKinds.HasEmbeddedProperties(status.LocalPath) && !writers.Any(w => w.CanWrite(status.LocalPath)))
                blocked.Add(new FileOutcome(status.LocalPath, $"Openhold can't write properties into {Path.GetExtension(name)} files on this PC yet. Change them in the CAD application."));
        }
        // Drawings check in with their AutoCAD Electrical project, so work in progress elsewhere in it would be swept up.
        var siblings = ExpandProjectSets(paths).Except(paths, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var sibling in siblings.Count == 0 ? [] : await GetStatusAsync(siblings, cancellationToken))
        {
            if (sibling.State == FileState.CheckedOutToMe)
            {
                blocked.Add(new FileOutcome(sibling.LocalPath,
                    $"{Path.GetFileName(sibling.LocalPath)}, in the same AutoCAD Electrical project, is checked out to you. Check it in (or undo) first."));
            }
        }
        if (blocked.Count > 0)
            return new WorkspaceResult(false, "No properties were changed.", blocked);

        var checkOut = await CheckOutAsync(paths, cancellationToken);
        if (!checkOut.Succeeded)
            return checkOut with { Summary = $"No properties were changed. {checkOut.Summary}" };

        var merged = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var vaultOnly = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var thumbnails = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var status in statuses)
            {
                var path = status.LocalPath;
                var values = edits[path];
                var writer = writers.FirstOrDefault(w => w.CanWrite(path));
                // What the file can hold is written into it; anything else (e.g. a DWG property with no title-block
                // attribute) is kept in the vault only, and survives later check-ins from the CAD program.
                var intoFile = values.Where(v => writer is null || writer.CanHold(path, v.Key)).ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
                if (writer is not null && intoFile.Count > 0)
                    writer.Write(path, intoFile);

                var properties = new Dictionary<string, string>(FileHeld(status.File!), StringComparer.OrdinalIgnoreCase);
                var vaultProperties = new Dictionary<string, string>(status.File!.VaultProperties, StringComparer.OrdinalIgnoreCase);
                foreach (var (key, value) in values)
                {
                    var target = intoFile.ContainsKey(key) ? properties : vaultProperties;
                    (intoFile.ContainsKey(key) ? vaultProperties : properties).Remove(key);
                    if (value.Length == 0)
                        target.Remove(key);
                    else
                        target[key] = value;
                }
                merged[path] = properties;
                vaultOnly[path] = vaultProperties;
                // Writing properties doesn't change how the file looks, so it keeps its preview.
                thumbnails[path] = await _client.GetThumbnailAsync(status.File.Id, cancellationToken: cancellationToken);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
                                       or OpenholdApiException or HttpRequestException or OperationCanceledException)
        {
            // Everything was checked out by this edit (files already checked out were refused above), so undoing
            // restores exactly what was there before; files a writer already changed are kept in a backup.
            await UndoCheckOutAsync(paths, CancellationToken.None);
            return new WorkspaceResult(false, $"No properties were changed: {ex.Message}", []);
        }

        var result = await CheckInAsync(paths, comment, new CheckInOptions
        {
            Properties = p => merged.GetValueOrDefault(p),
            VaultProperties = p => vaultOnly.GetValueOrDefault(p),
            Thumbnail = p => thumbnails.GetValueOrDefault(p),
            PropertiesChanged = merged.ContainsKey,
        }, cancellationToken);
        if (result.Succeeded)
            return result with { Summary = $"Updated the properties of {Plural(paths.Count, "file", "files")} and checked in a new version." };
        if (result.Retryable)
        {
            return result with
            {
                Summary = "Couldn't reach the Openhold server to confirm the new version. The files are written and still checked out to you; check them in again when the connection is back.",
            };
        }
        await UndoCheckOutAsync(paths, CancellationToken.None);
        return result with { Summary = $"No properties were changed. {result.Summary}" };
    }

    /// <summary>
    /// Pack and Go: writes the latest vault version of each file and everything it uses (at any depth) into a zip,
    /// keeping their vault folders, e.g. to send an assembly to a supplier. Local, uncommitted edits are never
    /// included. Returns the number of files packed.
    /// </summary>
    public async Task<int> PackAndGoAsync(IEnumerable<string> localPaths, string zipPath, CancellationToken cancellationToken = default)
    {
        var paths = await WithDependenciesAsync(localPaths, cancellationToken);
        var files = (await ResolveAsync(paths, cancellationToken)).Where(r => r.File is not null).ToList();

        var temp = zipPath + ".partial";
        await using (var zipStream = new FileStream(temp, FileMode.Create, FileAccess.Write))
        using (var zip = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Create))
        {
            foreach (var (path, file) in files)
            {
                var entry = zip.CreateEntry(Relative(path).Replace(Path.DirectorySeparatorChar, '/'), System.IO.Compression.CompressionLevel.Optimal);
                entry.LastWriteTime = file!.ModifiedAt ?? DateTime.Now;
                await using var output = entry.Open();
                await _client.DownloadAsync(file.Id, file.LatestVersion, output, cancellationToken: cancellationToken);
            }
        }
        File.Move(temp, zipPath, overwrite: true);
        return files.Count;
    }

    /// <summary>
    /// The given files plus every file they use at any depth, from the vault's reference records, e.g. an assembly
    /// with all its sub-assemblies and parts, so they can be got or checked out together.
    /// </summary>
    public async Task<IReadOnlyList<string>> WithDependenciesAsync(IEnumerable<string> localPaths, CancellationToken cancellationToken = default)
    {
        var paths = localPaths.Select(Path.GetFullPath).ToList();
        var result = new List<string>(paths);
        foreach (var (_, file) in await ResolveAsync(paths, cancellationToken))
        {
            if (file is null)
                continue;
            foreach (var child in await _client.GetAllUsesAsync(file.Id, cancellationToken))
                result.Add(Path.Combine(ToLocalPath(child.Path), child.Name));
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Searches the vault (optionally within a folder) and reports each hit's status on this PC.</summary>
    public async Task<WorkspaceSearchResults> SearchAsync(string text, string? vaultFolder = null, CancellationToken cancellationToken = default)
    {
        var results = await _client.SearchAsync(text, vaultFolder, cancellationToken: cancellationToken);
        var hits = new List<WorkspaceSearchHit>(results.Hits.Count);
        foreach (var hit in results.Hits)
        {
            var localPath = Path.Combine(ToLocalPath(hit.FolderPath), hit.File.Name);
            hits.Add(new WorkspaceSearchHit(await StatusOfAsync(localPath, hit.File, cancellationToken), hit.FolderPath, hit.Match));
        }
        return new WorkspaceSearchResults(hits, results.Truncated);
    }

    /// <summary>
    /// True when the vault's latest version has exactly the content this PC already has, e.g. after a release or a
    /// category change (new versions with the same content). There's nothing to download.
    /// </summary>
    private static bool SameContent(LocalFileRecord record, VaultFileInfo file) =>
        record.FileId == file.Id && file.ContentHash is { } hash && string.Equals(record.ContentHash, hash, StringComparison.OrdinalIgnoreCase);

    /// <summary>Records that the local copy is the latest version, when only the version number moved on.</summary>
    private void CatchUp(string path, LocalFileRecord record, VaultFileInfo file)
    {
        record.Version = file.LatestVersion;
        _state.Set(Relative(path), record);
    }

    private async Task<FileStatus> StatusOfAsync(string path, VaultFileInfo? file, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(path);
        var record = _state.Get(Relative(path));
        // A record for a different file (e.g. one at this path in another vault) says nothing about this one.
        if (record is not null && file is not null && record.FileId != file.Id)
            record = null;
        var local = await LocalCopyOfAsync(path, file, record, cancellationToken);
        FileStatus Status(FileState state, string description) => new(path, state, file, description) { LocalCopy = local };

        if (file is null)
            return Status(FileState.NotInVault, $"{name} isn't in the vault yet. It will be added when you check it in.");
        if (IsMine(file))
            return Status(FileState.CheckedOutToMe, $"{name} is checked out to you.");
        if (file.LockedBy is not null)
            return Status(FileState.CheckedOutToSomeoneElse, HeldBy(name, file.LockedBy, file.LockMachine, file.LockSite, null));
        return local switch
        {
            LocalCopy.None => Status(FileState.NotDownloaded, $"{name} hasn't been downloaded to this PC."),
            LocalCopy.ChangedHere => Status(FileState.ModifiedLocally, $"{name} was changed on this PC without being checked out."),
            LocalCopy.Outdated => Status(FileState.Outdated, $"A newer version of {name} is in the vault (version {file.LatestVersion})."),
            _ => Status(FileState.UpToDate, $"{name} is up to date."),
        };
    }

    /// <summary>
    /// The copy on this PC compared with the vault, whoever has the file checked out. A file checked out to this user
    /// is the working copy, so it counts as up to date.
    /// </summary>
    private async Task<LocalCopy> LocalCopyOfAsync(string path, VaultFileInfo? file, LocalFileRecord? record, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return LocalCopy.None;
        if (file is null)
            return LocalCopy.OnlyHere;
        if (IsMine(file))
            return LocalCopy.UpToDate;
        if (await IsLocallyModifiedAsync(path, record, cancellationToken))
            return LocalCopy.ChangedHere;
        if (record is not null && record.Version < file.LatestVersion && SameContent(record, file))
            CatchUp(path, record, file);
        return record is null || record.Version < file.LatestVersion ? LocalCopy.Outdated : LocalCopy.UpToDate;
    }

    /// <summary>
    /// Checks out the files (and their project sets) all-or-nothing. Each file is brought up to the latest
    /// version before it becomes editable, so nobody ever edits a stale copy.
    /// </summary>
    public async Task<WorkspaceResult> CheckOutAsync(IEnumerable<string> localPaths, CancellationToken cancellationToken = default)
    {
        var paths = ExpandProjectSets(localPaths);
        if (RejectOutside(paths) is { } outside)
            return outside;
        var resolved = await ResolveAsync(paths, cancellationToken);
        var outcomes = new List<FileOutcome>();

        foreach (var (path, _) in resolved.Where(r => r.File is null))
            outcomes.Add(new FileOutcome(path, $"{Path.GetFileName(path)} isn't in the vault yet. It will be added when you check it in."));

        var inVault = resolved.Where(r => r.File is not null).ToList();
        if (inVault.Count == 0)
            return new WorkspaceResult(true, "Nothing to check out: these files are new and will be added when you check them in.", outcomes);

        CheckOutResult result;
        try
        {
            result = await _client.CheckOutAsync(inVault.Select(r => r.File!.Id), cancellationToken);
        }
        catch (OpenholdApiException ex) when (ex.Code == "state_locked")
        {
            return new WorkspaceResult(false, $"Nothing was checked out. {ex.Message}", outcomes);
        }
        if (!result.Succeeded)
        {
            var byId = inVault.ToDictionary(r => r.File!.Id, r => r.Path);
            foreach (var holder in result.Conflicts)
                outcomes.Add(new FileOutcome(byId[holder.FileId], HeldBy(Path.GetFileName(byId[holder.FileId]), holder.Username, holder.Machine, holder.SiteId, holder.AcquiredAt)));
            foreach (var missing in result.NotFound)
                outcomes.Add(new FileOutcome(byId[missing], $"{Path.GetFileName(byId[missing])} has been removed from the vault."));

            var summary = result.Conflicts.Count > 0
                ? $"Nothing was checked out, because {Plural(result.Conflicts.Count, "file is", "files are")} checked out by someone else. Files are checked out together so a set never ends up half-edited."
                : "Nothing was checked out, because some files are no longer in the vault.";
            return new WorkspaceResult(false, summary, outcomes);
        }

        var latest = result.Files.ToDictionary(f => f.FileId, f => f.LatestVersion);
        var updated = new List<string>();
        foreach (var (path, file) in inVault)
        {
            var record = _state.Get(Relative(path));
            var version = latest[file!.Id];
            // A copy based on the latest version is kept as it is, edits and all: those edits aren't stale, and the
            // file may already be checked out to this user (e.g. a parent pulled into a rename or a project set).
            var current = record is not null && record.FileId == file.Id && record.Version == version && File.Exists(path);
            // Same for a copy whose content is the latest's and only the version number moved on (e.g. a release).
            if (!current && record is not null && file.LatestVersion == version && SameContent(record, file) && File.Exists(path))
            {
                CatchUp(path, record, file);
                current = true;
            }

            if (!current)
            {
                var backup = await BackupIfModifiedAsync(path, record, cancellationToken);
                await DownloadToAsync(path, file.Id, version, cancellationToken);
                updated.Add(path);
                if (backup is not null)
                    outcomes.Add(new FileOutcome(path, $"Your earlier local changes to {Path.GetFileName(path)} were kept in {backup}."));
            }
            SetReadOnly(path, false);
        }

        var note = updated.Count > 0 ? $" {Plural(updated.Count, "file was", "files were")} updated to the latest version first." : "";
        return new WorkspaceResult(true, $"Checked out {Plural(inVault.Count, "file", "files")}.{note}", outcomes) { UpdatedFiles = updated };
    }

    /// <summary>Checks the files in as one changeset: all get a new version, or none do.</summary>
    public async Task<WorkspaceResult> CheckInAsync(IEnumerable<string> localPaths, string comment, CheckInOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new CheckInOptions();
        var paths = options.WholeProjectSets
            ? ExpandProjectSets(localPaths)
            : localPaths.Select(Path.GetFullPath).Where(p => !FileKinds.IsIgnored(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if ((RejectOutside(paths) ?? RejectMissing(paths)) is { } invalid)
            return invalid;

        var resolved = await ResolveAsync(paths, cancellationToken);
        List<(string Path, VaultFileInfo? File)> changed;
        List<FileOutcome> unchangedOutcomes;
        var interrupted = await FindInterruptedAsync(paths, cancellationToken);
        if (interrupted is not null)
        {
            // An earlier attempt with exactly this content may already be committed (and its locks released) with
            // only the reply lost. Resending the same changeset lets the server confirm it once, or apply it now.
            var resent = new HashSet<string>(interrupted.Paths, StringComparer.OrdinalIgnoreCase);
            changed = resolved.Where(r => resent.Contains(Relative(r.Path))).ToList();
            unchangedOutcomes = [];
        }
        else
        {
            if (RejectNotCheckedOut(resolved) is { } notCheckedOut)
                return notCheckedOut;
            (changed, unchangedOutcomes) = await ReleaseUnchangedAsync(resolved, options, cancellationToken);
            if (changed.Count == 0)
                return NothingChanged(unchangedOutcomes, options.KeepCheckedOut);
        }

        var pending = interrupted ?? StartOrResumePending(changed.Select(c => c.Path).ToList());
        List<Upload> uploads;
        CheckInResult result;
        try
        {
            uploads = await UploadAsync(changed, options, cancellationToken);
            pending = MatchPendingToContent(pending, uploads);
            result = await _client.CheckInAsync(new CheckInRequest(pending.ChangesetId, comment, uploads.Select(u => u.File).ToList()), cancellationToken);
        }
        catch (OpenholdApiException ex) when (ex.Code is "lock_not_held" or "name_conflict" or "invalid_name" or "missing_chunks")
        {
            // The server refused the changeset as a whole; nothing was committed, so a fresh attempt gets a new id.
            _state.RemovePending(pending.ChangesetId);
            return new WorkspaceResult(false, $"Nothing was checked in: {ex.Message}", []);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new WorkspaceResult(false,
                "Couldn't reach the Openhold server. Nothing is lost: your files are unchanged and still checked out to you. Check in again when the connection is back.",
                []) { Retryable = true };
        }
        catch (IOException ex)
        {
            return new WorkspaceResult(false,
                $"Nothing was checked in, because a file couldn't be read: {ex.Message} Close it in any other program and try again.",
                []);
        }

        // From here the vault has the new versions; a local problem must not be reported as a failed check-in.
        _state.RemovePending(pending.ChangesetId);
        return RecordCommitted(result, uploads, unchangedOutcomes, options.KeepCheckedOut);
    }

    private sealed record Upload(string Path, CheckInFile File, string Hash, long Length, DateTime LastWriteUtc);

    private static WorkspaceResult? RejectMissing(IReadOnlyList<string> paths)
    {
        var missing = paths.Where(p => !File.Exists(p)).ToList();
        return missing.Count == 0 ? null : new WorkspaceResult(false, "Nothing was checked in, because some files are missing from your workspace.",
            missing.Select(p => new FileOutcome(p, $"{Path.GetFileName(p)} is missing from {Path.GetDirectoryName(p)}.")).ToList());
    }

    private WorkspaceResult? RejectNotCheckedOut(List<(string Path, VaultFileInfo? File)> resolved)
    {
        var notMine = resolved.Where(r => r.File is not null && !IsMine(r.File)).ToList();
        return notMine.Count == 0 ? null : new WorkspaceResult(false, "Nothing was checked in. Check these files out first, then check them in.",
            notMine.Select(r => new FileOutcome(r.Path, r.File!.LockedBy is null
                ? $"{Path.GetFileName(r.Path)} isn't checked out to you."
                : HeldBy(Path.GetFileName(r.Path), r.File.LockedBy, r.File.LockMachine, r.File.LockSite, null))).ToList());
    }

    /// <summary>
    /// A file identical to the version it was checked out at needs no new version: it is released instead
    /// (or kept checked out if asked), so history only grows when something actually changed.
    /// </summary>
    private async Task<(List<(string Path, VaultFileInfo? File)> Changed, List<FileOutcome> UnchangedOutcomes)> ReleaseUnchangedAsync(
        List<(string Path, VaultFileInfo? File)> resolved, CheckInOptions options, CancellationToken cancellationToken)
    {
        var changed = new List<(string Path, VaultFileInfo? File)>();
        var unchanged = new List<(string Path, VaultFileInfo File, int Version)>();
        foreach (var (path, file) in resolved)
        {
            var record = _state.Get(Relative(path));
            if (file is not null && record is not null && record.FileId == file.Id && record.Version == file.LatestVersion
                && options.PropertiesChanged?.Invoke(path) != true
                && await HashFileAsync(path, cancellationToken) == record.ContentHash)
            {
                unchanged.Add((path, file, record.Version));
            }
            else
            {
                changed.Add((path, file));
            }
        }

        if (unchanged.Count > 0 && !options.KeepCheckedOut)
        {
            await _client.UndoCheckOutAsync(unchanged.Select(u => u.File.Id), cancellationToken);
            foreach (var u in unchanged)
                SetReadOnly(u.Path, true);
        }
        return (changed, unchanged.Select(u => new FileOutcome(u.Path,
            $"{Path.GetFileName(u.Path)} hasn't changed since version {u.Version}, so no new version was made.")).ToList());
    }

    private static WorkspaceResult NothingChanged(List<FileOutcome> outcomes, bool keptCheckedOut)
    {
        var one = outcomes.Count == 1;
        var after = keptCheckedOut
            ? one ? "It is still checked out to you." : "They are still checked out to you."
            : one ? "It has been released." : "They have been released.";
        return new WorkspaceResult(true, $"Nothing had changed, so no new version was made. {after}", outcomes);
    }

    private PendingCheckIn StartOrResumePending(IReadOnlyList<string> paths)
    {
        var relatives = paths.Select(Relative).ToList();
        if (_state.FindPending(relatives) is { } pending)
            return pending;
        pending = new PendingCheckIn { ChangesetId = Guid.NewGuid(), Paths = relatives, StartedUtc = DateTime.UtcNow };
        _state.AddPending(pending);
        return pending;
    }

    /// <summary>An interrupted check-in of some of these files whose content hasn't changed since, if there is one.</summary>
    private async Task<PendingCheckIn?> FindInterruptedAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        foreach (var pending in _state.PendingWithin(paths.Select(Relative).ToList()))
        {
            if (pending.Hashes.Count != pending.Paths.Count)
                continue; // stopped before anything was sent
            var same = true;
            foreach (var (relative, hash) in pending.Hashes)
            {
                var path = Path.Combine(Root, relative);
                if (!File.Exists(path) || await HashFileAsync(path, cancellationToken) != hash)
                {
                    same = false;
                    break;
                }
            }
            if (same)
                return pending;
        }
        return null;
    }

    /// <summary>
    /// A changeset id may only be resent for the same content: the server answers a repeated id with its first
    /// result, so files edited since an interrupted attempt get a new id rather than being silently dropped.
    /// </summary>
    private PendingCheckIn MatchPendingToContent(PendingCheckIn pending, List<Upload> uploads)
    {
        var hashes = uploads.ToDictionary(u => Relative(u.Path), u => u.Hash, StringComparer.OrdinalIgnoreCase);
        if (pending.Hashes.Count > 0 && pending.Hashes.Count == hashes.Count
            && hashes.All(h => pending.Hashes.TryGetValue(h.Key, out var earlier) && earlier == h.Value))
        {
            return pending;
        }
        _state.RemovePending(pending.ChangesetId);
        var fresh = new PendingCheckIn
        {
            ChangesetId = pending.Hashes.Count == 0 ? pending.ChangesetId : Guid.NewGuid(),
            Paths = pending.Paths, StartedUtc = DateTime.UtcNow, Hashes = hashes,
        };
        _state.AddPending(fresh);
        return fresh;
    }

    /// <summary>Uploads each file's missing chunks and builds its part of the changeset.</summary>
    private async Task<List<Upload>> UploadAsync(List<(string Path, VaultFileInfo? File)> changed, CheckInOptions options, CancellationToken cancellationToken)
    {
        var paths = changed.Select(c => c.Path).ToList();
        var indexByPath = paths.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => x.i, StringComparer.OrdinalIgnoreCase);
        var externalReferences = await ResolveExternalReferencesAsync(paths, indexByPath, options, cancellationToken);
        var folderIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var uploads = new List<Upload>(changed.Count);

        foreach (var (path, file) in changed)
        {
            var lastWrite = File.GetLastWriteTimeUtc(path);
            byte[] manifest;
            await using (var stream = OpenSharedRead(path))
                manifest = await _client.UploadContentAsync(stream, cancellationToken);
            var decoded = ManifestCodec.Decode(manifest);

            // No reference callback (e.g. a check-in from Explorer), or none read for this file, sends null, so the
            // server keeps the previous version's references; an empty list would mean "this file uses nothing".
            var references = options.References?.Invoke(path)?
                .Select(Path.GetFullPath)
                .Select(child => indexByPath.TryGetValue(child, out var index)
                    ? new CheckInReference(null, index, null, Relative(child))
                    : externalReferences.TryGetValue(child, out var childId) ? new CheckInReference(childId, null, null, Relative(child)) : null)
                .OfType<CheckInReference>()
                .ToList();
            var properties = options.Properties?.Invoke(path);
            var vaultProperties = options.VaultProperties?.Invoke(path);
            var thumbnail = options.Thumbnail?.Invoke(path);

            CheckInFile part;
            if (file is not null)
            {
                part = new CheckInFile(file.Id, null, null, manifest, properties, references, options.KeepCheckedOut, thumbnail, VaultProperties: vaultProperties);
            }
            else
            {
                var vaultPath = ToVaultPath(path);
                var folderPath = vaultPath[..vaultPath.LastIndexOf('/')];
                if (!folderIds.TryGetValue(folderPath, out var folderId))
                    folderIds[folderPath] = folderId = (await _client.EnsureFolderAsync(folderPath, cancellationToken)).Id;
                part = new CheckInFile(null, folderId, Path.GetFileName(path), manifest, properties, references, options.KeepCheckedOut, thumbnail, VaultProperties: vaultProperties);
            }
            uploads.Add(new Upload(path, part, decoded.FileHash.ToString(), decoded.Length, lastWrite));
        }
        return uploads;
    }

    /// <summary>Records the committed versions locally. Runs after the server committed, so it never reports failure.</summary>
    private WorkspaceResult RecordCommitted(CheckInResult result, List<Upload> uploads, List<FileOutcome> unchangedOutcomes, bool keptCheckedOut)
    {
        var outcomes = new List<FileOutcome>(unchangedOutcomes);
        var localProblems = new List<string>();
        foreach (var upload in uploads)
        {
            // Match by file id for existing files and by folder + name for new ones; names alone can repeat across folders.
            var committed = upload.File.FileId is { } existingId
                ? result.Versions.Single(v => v.FileId == existingId)
                : result.Versions.Single(v => v.FolderId == upload.File.FolderId && v.Name.Equals(upload.File.Name, StringComparison.OrdinalIgnoreCase));
            outcomes.Add(new FileOutcome(upload.Path, $"{committed.Name} is now version {committed.Version}."));
            try
            {
                _state.Set(Relative(upload.Path), new LocalFileRecord
                {
                    FileId = committed.FileId, Version = committed.Version, ContentHash = upload.Hash, Length = upload.Length, LastWriteUtc = upload.LastWriteUtc,
                });
                SetReadOnly(upload.Path, !keptCheckedOut);
            }
            catch (IOException ex)
            {
                localProblems.Add($"{committed.Name}: {ex.Message}");
            }
        }

        var summary = $"Checked in {Plural(uploads.Count, "file", "files")}." + (keptCheckedOut ? " They are still checked out to you." : "");
        if (localProblems.Count > 0)
            summary += $" The vault has your changes, but this PC's copy couldn't be updated ({string.Join("; ", localProblems)}). Use Get Latest to refresh it.";
        return new WorkspaceResult(true, summary, outcomes);
    }

    /// <summary>
    /// Opens a file for reading while CAD may hold it open. AutoCAD and Inventor keep open files locked for
    /// writing, so a reader must allow writers or Windows refuses with a sharing violation. Callers save first,
    /// so the content read is consistent.
    /// </summary>
    private static FileStream OpenSharedRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

    /// <summary>Releases the files without checking in and restores the vault's copy. Local edits are backed up first.</summary>
    public async Task<WorkspaceResult> UndoCheckOutAsync(IEnumerable<string> localPaths, CancellationToken cancellationToken = default)
    {
        var paths = ExpandProjectSets(localPaths);
        if (RejectOutside(paths) is { } outside)
            return outside;
        var resolved = (await ResolveAsync(paths, cancellationToken)).Where(r => r.File is not null).ToList();
        var released = (await _client.UndoCheckOutAsync(resolved.Select(r => r.File!.Id), cancellationToken)).ToHashSet();
        var outcomes = new List<FileOutcome>();

        foreach (var (path, file) in resolved.Where(r => released.Contains(r.File!.Id)))
        {
            var record = _state.Get(Relative(path));
            var backup = await BackupIfModifiedAsync(path, record, cancellationToken);
            await DownloadToAsync(path, file!.Id, record?.FileId == file.Id ? record.Version : null, cancellationToken);
            outcomes.Add(new FileOutcome(path, backup is null
                ? $"{Path.GetFileName(path)} was released."
                : $"{Path.GetFileName(path)} was released. Your changes were kept in {backup} in case you need them."));
        }

        return new WorkspaceResult(true, released.Count == 0
            ? "None of these files were checked out to you."
            : $"Released {Plural(released.Count, "file", "files")} without checking in.", outcomes);
    }

    /// <summary>Brings files up to the latest vault version. Files checked out to you are left alone.</summary>
    public async Task<WorkspaceResult> GetLatestAsync(IEnumerable<string> localPaths, CancellationToken cancellationToken = default)
    {
        var paths = localPaths.Select(Path.GetFullPath).ToList();
        if (RejectOutside(paths) is { } outside)
            return outside;
        var resolved = await ResolveAsync(paths, cancellationToken);
        var outcomes = new List<FileOutcome>();
        var updated = 0;

        foreach (var (path, entry) in resolved)
        {
            if (entry is not { } file)
                continue;
            if (IsMine(file))
            {
                outcomes.Add(new FileOutcome(path, $"{Path.GetFileName(path)} is checked out to you, so your copy was kept."));
                continue;
            }
            var record = _state.Get(Relative(path));
            if (record is not null && record.FileId == file.Id && File.Exists(path) && !await IsLocallyModifiedAsync(path, record, cancellationToken))
            {
                if (record.Version == file.LatestVersion)
                    continue;
                if (SameContent(record, file))
                {
                    CatchUp(path, record, file);
                    continue;
                }
            }

            var backup = await BackupIfModifiedAsync(path, record, cancellationToken);
            await DownloadToAsync(path, file.Id, file.LatestVersion, cancellationToken);
            updated++;
            if (backup is not null)
                outcomes.Add(new FileOutcome(path, $"{Path.GetFileName(path)} had been changed on this PC without being checked out. Those changes were kept in {backup}."));
        }

        return new WorkspaceResult(true, updated == 0 ? "Everything is up to date." : $"Updated {Plural(updated, "file", "files")} to the latest version.", outcomes);
    }

    private async Task<List<(string Path, VaultFileInfo? File)>> ResolveAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        if (paths.Count == 0)
            return [];
        var resolved = await _client.ResolvePathsAsync(paths.Select(ToVaultPath), cancellationToken);
        return paths.Zip(resolved, (path, r) => (path, r.File)).ToList();
    }

    private async Task<Dictionary<string, Guid>> ResolveExternalReferencesAsync(
        IReadOnlyList<string> paths, Dictionary<string, int> inChangeset, CheckInOptions options, CancellationToken cancellationToken)
    {
        if (options.References is null)
            return new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        var external = paths.SelectMany(p => options.References(p) ?? [])
            .Select(Path.GetFullPath)
            .Where(c => !inChangeset.ContainsKey(c) && Contains(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var resolved = await ResolveAsync(external, cancellationToken);
        return resolved.Where(r => r.File is not null).ToDictionary(r => r.Path, r => r.File!.Id, StringComparer.OrdinalIgnoreCase);
    }

    private async Task DownloadToAsync(string path, Guid fileId, int? version, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".openhold-download";
        ChunkManifest manifest;
        await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            manifest = await _client.DownloadAsync(fileId, version, output, cancellationToken: cancellationToken);

        if (File.Exists(path))
            SetReadOnly(path, false);
        File.Move(temp, path, overwrite: true);

        var resolvedVersion = version ?? (await _client.GetFileAsync(fileId, cancellationToken)).LatestVersion;
        _state.Set(Relative(path), new LocalFileRecord
        {
            FileId = fileId,
            Version = resolvedVersion,
            ContentHash = manifest.FileHash.ToString(),
            Length = manifest.Length,
            LastWriteUtc = File.GetLastWriteTimeUtc(path),
        });
        SetReadOnly(path, true);
    }

    private static async Task<bool> IsLocallyModifiedAsync(string path, LocalFileRecord? record, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return false;
        if (record is null)
            return true; // a file we never downloaded: treat as the user's own work
        var info = new FileInfo(path);
        if (info.Length == record.Length && info.LastWriteTimeUtc == record.LastWriteUtc)
            return false;
        return await HashFileAsync(path, cancellationToken) != record.ContentHash;
    }

    private async Task<string?> BackupIfModifiedAsync(string path, LocalFileRecord? record, CancellationToken cancellationToken)
    {
        if (!await IsLocallyModifiedAsync(path, record, cancellationToken))
            return null;
        var backup = NewBackupPath(path);
        File.Copy(path, backup, overwrite: true);
        SetReadOnly(backup, false);
        return backup;
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        var hasher = Blake3.Hasher.New();
        try
        {
            var buffer = new byte[1024 * 1024];
            await using var stream = OpenSharedRead(path);
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
                hasher.Update(buffer.AsSpan(0, read));
            return ContentHash.FromBytes(hasher.Finalize().AsSpan()).ToString();
        }
        finally
        {
            hasher.Dispose();
        }
    }

    private bool IsMine(VaultFileInfo file) =>
        string.Equals(file.LockedBy, _client.Username, StringComparison.OrdinalIgnoreCase)
        && string.Equals(file.LockMachine, _client.Machine, StringComparison.OrdinalIgnoreCase);

    private static string HeldBy(string name, string username, string? machine, string? site, DateTime? since)
    {
        var where = machine is null ? "" : $" on {machine}";
        var siteText = site is null ? "" : $" ({site})";
        var when = since is null ? "" : $" since {since.Value.ToLocalTime().ToString("d MMM h:mm tt", CultureInfo.CurrentCulture)}";
        return $"{name} is checked out to {username}{where}{siteText}{when}.";
    }

    private WorkspaceResult? RejectOutside(IReadOnlyList<string> paths)
    {
        var outside = paths.Where(p => !Contains(p)).ToList();
        return outside.Count == 0 ? null : new WorkspaceResult(false,
            $"Some files are outside your Openhold workspace ({Root}). Save or move them inside the workspace first.",
            outside.Select(p => new FileOutcome(p, $"{Path.GetFileName(p)} is outside your Openhold workspace.")).ToList());
    }

    private static string Plural(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count} {many}";

    /// <summary>True if the path is inside this workspace (and so can be in the vault).</summary>
    public bool Contains(string path) =>
        !string.IsNullOrEmpty(path) && Path.IsPathRooted(path)
        && Path.GetFullPath(path).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Where to keep a safety copy of a workspace file before anything could overwrite it:
    /// <c>.openhold/backup/&lt;yyyyMMdd-HHmmss&gt;/&lt;relative path&gt;</c>, optionally with a prefix on the name.
    /// </summary>
    public string NewBackupPath(string localPath, string namePrefix = "")
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var relative = Relative(localPath);
        var backup = Path.Combine(Root, ".openhold", "backup", stamp, Path.GetDirectoryName(relative) ?? "", namePrefix + Path.GetFileName(relative));
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        return backup;
    }

    private string Relative(string localPath)
    {
        var full = Path.GetFullPath(localPath);
        if (!Contains(full))
            throw new ArgumentException($"{full} is outside your Openhold workspace ({Root}). Move or save it inside the workspace first.");
        return full[(Root.Length + 1)..];
    }

    private static void SetReadOnly(string path, bool readOnly)
    {
        var attributes = File.GetAttributes(path);
        File.SetAttributes(path, readOnly ? attributes | FileAttributes.ReadOnly : attributes & ~FileAttributes.ReadOnly);
    }
}
