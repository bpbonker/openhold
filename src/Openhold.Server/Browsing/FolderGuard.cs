using System.Collections.Concurrent;
using Dapper;
using Openhold.Core.Configuration;
using Openhold.Core.Content;
using Openhold.Server.Configuration;
using Openhold.Server.Data;
using Openhold.Server.Identity;

namespace Openhold.Server.Browsing;

/// <summary>
/// Enforces folder permissions for one request: hides what the person may not read, and refuses changes where they
/// may only read. Hidden files answer "not found", so their existence isn't revealed. When no folder in the vault is
/// restricted for the person (the usual case), every check returns at once without touching the database.
/// </summary>
public sealed class FolderGuard(IHttpContextAccessor http, ConfigurationService configuration, OpenholdDb db, VaultInstance vault)
{
    private VaultConfiguration? _configuration;

    private string Username =>
        ActorResolver.NormalizeUsername(http.HttpContext?.User.Identity?.Name)
        ?? throw new OpenholdException(StatusCodes.Status401Unauthorized, "unauthenticated", "Sign-in required.");

    private async Task<VaultConfiguration?> RestrictionsAsync(CancellationToken cancellationToken)
    {
        _configuration ??= (await configuration.GetAsync(cancellationToken)).Configuration;
        return _configuration.Unrestricted(Username) ? null : _configuration;
    }

    /// <summary>Refuses unless the person has at least <paramref name="needed"/> on the folder.</summary>
    public async Task RequireFolderAsync(string folderPath, FolderAccess needed, CancellationToken cancellationToken)
    {
        if (await RestrictionsAsync(cancellationToken) is not { } rules)
            return;
        Refuse(rules, VaultBrowser.NormalizePath(folderPath), needed);
    }

    /// <summary>Refuses unless the person has at least <paramref name="needed"/> on every file's folder (deleted files included).</summary>
    public async Task RequireFilesAsync(IEnumerable<Guid> fileIds, FolderAccess needed, CancellationToken cancellationToken)
    {
        if (await RestrictionsAsync(cancellationToken) is not { } rules)
            return;
        foreach (var path in (await FolderPathsAsync(fileIds, cancellationToken)).Values.Distinct(StringComparer.OrdinalIgnoreCase))
            Refuse(rules, path, needed);
    }

    /// <summary>Refuses unless the person may change the folders new files go into (by folder id).</summary>
    public async Task RequireFolderIdsAsync(IEnumerable<Guid> folderIds, FolderAccess needed, CancellationToken cancellationToken)
    {
        if (await RestrictionsAsync(cancellationToken) is not { } rules)
            return;
        await using var connection = await db.Local.OpenConnectionAsync(cancellationToken);
        var paths = await connection.QueryAsync<string>("SELECT path FROM folders WHERE id = ANY(@ids)", new { ids = folderIds.Distinct().ToArray() });
        foreach (var path in paths)
            Refuse(rules, path, needed);
    }

    /// <summary>The items in folders where the person has at least <paramref name="needed"/> (read, unless given); everything when nothing is restricted.</summary>
    public async Task<IReadOnlyList<T>> FilterAsync<T>(IReadOnlyList<T> items, Func<T, string> folderPath, CancellationToken cancellationToken, FolderAccess needed = FolderAccess.Read)
    {
        if (await RestrictionsAsync(cancellationToken) is not { } rules)
            return items;
        var username = Username;
        return items.Where(item => rules.AccessTo(username, folderPath(item)) >= needed).ToList();
    }

    /// <summary>A folder listing with hidden folders and files left out; "not found" if the folder itself is hidden.</summary>
    public async Task<Openhold.Core.Contracts.FolderListing> FilterAsync(Openhold.Core.Contracts.FolderListing listing, CancellationToken cancellationToken)
    {
        if (await RestrictionsAsync(cancellationToken) is not { } rules)
            return listing;
        var username = Username;
        if (!rules.CanSee(username, listing.Folder.Path))
            throw OpenholdException.NotFound($"Folder '{listing.Folder.Path}' does not exist.");
        var files = rules.AccessTo(username, listing.Folder.Path) >= FolderAccess.Read ? listing.Files : [];
        return listing with { Folders = listing.Folders.Where(f => rules.CanSee(username, f.Path)).ToList(), Files = files };
    }

    /// <summary>The files (by id) the person may read.</summary>
    public async Task<ISet<Guid>> ReadableAsync(IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken)
    {
        if (await RestrictionsAsync(cancellationToken) is not { } rules)
            return fileIds.ToHashSet();
        var username = Username;
        return (await FolderPathsAsync(fileIds, cancellationToken)).Where(p => rules.AccessTo(username, p.Value) >= FolderAccess.Read).Select(p => p.Key).ToHashSet();
    }

    /// <summary>Remembers that the person was shown this manifest, so its chunks may be downloaded.</summary>
    public async Task GrantAsync(byte[] encodedManifest, CancellationToken cancellationToken)
    {
        if (await RestrictionsAsync(cancellationToken) is null)
            return;
        vault.ChunkGrants.Grant(Username, ManifestCodec.Decode(encodedManifest).DistinctHashes());
    }

    /// <summary>
    /// Refuses chunks the person hasn't been given through a file they may read. Chunks are named by content hash,
    /// so without this anyone who learned a hash could download content from a folder hidden from them.
    /// </summary>
    public async Task RequireChunksAsync(IReadOnlyList<ContentHash> hashes, CancellationToken cancellationToken)
    {
        if (await RestrictionsAsync(cancellationToken) is not { } rules || rules.HasPrivilege(Username, Privileges.ReplicateContent))
            return; // site servers copy everything between sites
        if (!vault.ChunkGrants.AllGranted(Username, hashes))
            throw new OpenholdException(StatusCodes.Status403Forbidden, "chunks_not_granted",
                "Some of those chunks aren't part of a file you've opened. Ask for the file's manifest first.");
    }

    private void Refuse(VaultConfiguration rules, string folderPath, FolderAccess needed)
    {
        var access = rules.AccessTo(Username, folderPath);
        if (access >= needed)
            return;
        if (access == FolderAccess.None)
            throw OpenholdException.NotFound($"'{folderPath}' does not exist.");
        throw new OpenholdException(StatusCodes.Status403Forbidden, "read_only_folder",
            $"You can see {folderPath} but not change it. Ask an administrator if you need to.");
    }

    private async Task<Dictionary<Guid, string>> FolderPathsAsync(IEnumerable<Guid> fileIds, CancellationToken cancellationToken)
    {
        await using var connection = await db.Local.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<(Guid Id, string Path)>(
            "SELECT f.id, fo.path FROM files f JOIN folders fo ON fo.id = f.folder_id WHERE f.id = ANY(@ids)",
            new { ids = fileIds.Distinct().ToArray() })).ToDictionary(r => r.Id, r => r.Path);
    }
}

/// <summary>Per person, the chunk hashes they were shown in manifests recently. Kept in memory; grants last a working day.</summary>
public sealed class ChunkGrants
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);
    private const int MaxPerPerson = 2_000_000;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<ContentHash, DateTime>> _grants = new(StringComparer.OrdinalIgnoreCase);

    public void Grant(string username, IEnumerable<ContentHash> hashes)
    {
        var granted = _grants.GetOrAdd(username, _ => new());
        if (granted.Count > MaxPerPerson)
            granted.Clear();
        var expires = DateTime.UtcNow + Lifetime;
        foreach (var hash in hashes)
            granted[hash] = expires;
    }

    public bool AllGranted(string username, IEnumerable<ContentHash> hashes)
    {
        if (!_grants.TryGetValue(username, out var granted))
            return false;
        var now = DateTime.UtcNow;
        return hashes.All(h => granted.TryGetValue(h, out var expires) && expires > now);
    }
}
