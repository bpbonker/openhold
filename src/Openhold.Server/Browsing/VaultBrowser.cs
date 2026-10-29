using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Openhold.Core.Configuration;
using Openhold.Core.Contracts;
using Openhold.Server.Configuration;
using Openhold.Server.Data;

namespace Openhold.Server.Browsing;

/// <summary>
/// Read-side queries. Served from the local database (a replica at remote sites), so browsing,
/// history and where-used never cross the WAN.
/// </summary>
public sealed class VaultBrowser(OpenholdDb db, IOptions<OpenholdOptions> options)
{
    public const string RootPath = "$";

    public static string NormalizePath(string? path)
    {
        var segments = (path ?? RootPath).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0 || segments[0] != RootPath)
            segments = [RootPath, .. segments];
        foreach (var segment in segments.Skip(1))
        {
            if (segment is "." or ".." || segment.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
                throw OpenholdException.BadRequest("invalid_path", $"'{segment}' is not a valid folder name.");
        }
        return string.Join('/', segments);
    }

    /// <summary>Creates the folder and any missing ancestors (authority write). Returns the folder.</summary>
    public async Task<FolderInfo> EnsureFolderAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizePath(path);
        await using var connection = await db.Primary.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var current = await GetFolderAsync(connection, RootPath, transaction)
            ?? throw new InvalidOperationException("Root folder is missing; migrations have not run.");
        foreach (var segment in normalized.Split('/').Skip(1))
        {
            var childPath = $"{current.Path}/{segment}";
            await connection.ExecuteAsync("""
                INSERT INTO folders (id, parent_id, name, path) VALUES (@id, @parentId, @name, @path)
                ON CONFLICT (lower(path)) DO NOTHING
                """, new { id = Guid.NewGuid(), parentId = current.Id, name = segment, path = childPath }, transaction);
            current = (await GetFolderAsync(connection, childPath, transaction))!;
        }

        await transaction.CommitAsync(cancellationToken);
        return current;
    }

    /// <summary>
    /// Removes an empty folder (no subfolders, no files, none in the trash either, so nothing can be restored into a
    /// folder that's gone). The vault's top folder can't be removed. Returns the removed folder.
    /// </summary>
    public async Task<FolderInfo> DeleteEmptyFolderAsync(Identity.Actor actor, string path, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizePath(path);
        if (normalized == RootPath)
            throw OpenholdException.BadRequest("root_folder", "The vault's top folder can't be removed.");
        await using var connection = await db.Primary.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var folder = await GetFolderAsync(connection, normalized, transaction)
            ?? throw OpenholdException.NotFound($"Folder '{normalized}' does not exist.");
        await connection.ExecuteAsync("SELECT 1 FROM folders WHERE id = @id FOR UPDATE", new { id = folder.Id }, transaction);
        var (subfolders, files, trashed) = await connection.QuerySingleAsync<(long, long, long)>("""
            SELECT (SELECT count(*) FROM folders WHERE parent_id = @id),
                   (SELECT count(*) FROM files WHERE folder_id = @id AND deleted_at IS NULL),
                   (SELECT count(*) FROM files WHERE folder_id = @id AND deleted_at IS NOT NULL)
            """, new { id = folder.Id }, transaction);
        if (subfolders + files > 0)
            throw OpenholdException.Conflict("folder_not_empty", $"{folder.Name} isn't empty ({subfolders} folder(s), {files} file(s)). Move or delete what's in it first.");
        if (trashed > 0)
            throw OpenholdException.Conflict("folder_not_empty", $"{trashed} deleted file(s) from {folder.Name} are in the trash and would have nowhere to go back to. Restore and move them first.");
        await connection.ExecuteAsync("DELETE FROM folders WHERE id = @id", new { id = folder.Id }, transaction);
        await connection.ExecuteAsync("""
            INSERT INTO audit_events (user_id, site_id, action, data) VALUES (@userId, @siteId, 'folder_deleted', jsonb_build_object('path', @path::text))
            """, new { userId = actor.UserId, siteId = actor.SiteId, path = folder.Path }, transaction);
        await transaction.CommitAsync(cancellationToken);
        return folder;
    }

    public async Task<FolderListing> ListAsync(string? path, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizePath(path);
        await using var connection = await db.Local.OpenConnectionAsync(cancellationToken);
        var folder = await GetFolderAsync(connection, normalized)
            ?? throw OpenholdException.NotFound($"Folder '{normalized}' does not exist.");

        var folders = (await connection.QueryAsync<FolderInfo>(
            "SELECT id AS Id, parent_id AS ParentId, name AS Name, path AS Path FROM folders WHERE parent_id = @id ORDER BY lower(name)",
            new { id = folder.Id })).ToList();
        var configuration = await ConfigurationAsync(connection);
        var files = (await connection.QueryAsync<FileRow>(FileSelect + " WHERE f.folder_id = @id AND f.deleted_at IS NULL ORDER BY lower(f.name)",
            new { id = folder.Id })).Select(f => f.ToInfo(configuration)).ToList();

        return new FolderListing(folder, folders, files);
    }

    public async Task<VaultFileInfo> GetFileAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.Local.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<FileRow>(FileSelect + " WHERE f.id = @fileId", new { fileId })
            ?? throw OpenholdException.NotFound($"File {fileId} does not exist.");
        return row.ToInfo(await ConfigurationAsync(connection));
    }

    /// <summary>
    /// Looks up many vault paths (<c>$/Folder/name.dwg</c>) in one query, so a client resolving a whole
    /// assembly or project set pays one round trip. Paths not in the vault come back with no file.
    /// </summary>
    public async Task<IReadOnlyList<ResolvedPath>> ResolvePathsAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        if (paths.Count > 5000)
            throw OpenholdException.BadRequest("too_many_paths", "Resolve at most 5000 paths per request.");

        var split = paths.Select(p =>
        {
            var normalized = NormalizePath(p);
            var slash = normalized.LastIndexOf('/');
            if (slash <= 0)
                throw OpenholdException.BadRequest("invalid_path", $"'{p}' is not a file path.");
            return (Original: p, Folder: normalized[..slash], Name: normalized[(slash + 1)..]);
        }).ToList();

        await using var connection = await db.Local.OpenConnectionAsync(cancellationToken);
        var configuration = await ConfigurationAsync(connection);
        var found = (await connection.QueryAsync<string, FileRow, (string Folder, VaultFileInfo File)>(
            $"""
            SELECT lower(fo.path) AS Folder, {FileColumns}
            FROM unnest(@folders, @names) AS q(folder, name)
            JOIN folders fo ON lower(fo.path) = lower(q.folder)
            JOIN files f ON f.folder_id = fo.id AND lower(f.name) = lower(q.name) AND f.deleted_at IS NULL
            {FileJoins}
            """,
            (string folder, FileRow file) => (folder, file.ToInfo(configuration)),
            new { folders = split.Select(s => s.Folder).ToArray(), names = split.Select(s => s.Name).ToArray() },
            splitOn: "Id")).ToList();

        var byKey = found.ToDictionary(f => (f.Folder, f.File.Name.ToLowerInvariant()), f => f.File);
        return split.Select(s => new ResolvedPath(s.Original,
            byKey.GetValueOrDefault((s.Folder.ToLowerInvariant(), s.Name.ToLowerInvariant())))).ToList();
    }

    public async Task<IReadOnlyList<VersionInfo>> GetHistoryAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.Local.OpenConnectionAsync(cancellationToken);
        var configuration = await ConfigurationAsync(connection);
        var category = await connection.ExecuteScalarAsync<string?>("SELECT category_id FROM files WHERE id = @fileId", new { fileId });
        var rows = await connection.QueryAsync<HistoryRow>("""
            SELECT v.version AS Version, v.changeset_id AS ChangesetId, v.length AS Length,
                   encode(v.content_hash, 'hex') AS ContentHash, v.origin_site AS OriginSite,
                   u.username AS Username, c.comment AS Comment, v.created_at AS CreatedAt,
                   (v.vault_properties || v.properties)::text AS PropertiesJson, v.state AS State
            FROM file_versions v
            JOIN changesets c ON c.id = v.changeset_id
            JOIN users u ON u.id = c.user_id
            WHERE v.file_id = @fileId
            ORDER BY v.version DESC
            """, new { fileId });
        return rows.Select(r => new VersionInfo(r.Version, r.ChangesetId, r.Length, r.ContentHash, r.OriginSite, r.Username, r.Comment, r.CreatedAt)
        {
            Properties = JsonSerializer.Deserialize<Dictionary<string, string>>(r.PropertiesJson) ?? [],
            State = configuration.StateOf(category, r.State)?.Name,
        }).ToList();
    }

    private sealed record HistoryRow(int Version, Guid ChangesetId, long Length, string ContentHash, string OriginSite, string Username, string Comment, DateTime CreatedAt, string PropertiesJson, string? State);

    /// <summary>The configuration in force, to describe each file's category and state.</summary>
    internal async Task<VaultConfiguration> ConfigurationAsync(NpgsqlConnection connection) =>
        (await ConfigurationService.LoadAsync(connection, null, options.Value)).Configuration;

    public async Task<byte[]> GetManifestAsync(Guid fileId, int? version, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.Local.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<byte[]>("""
            SELECT v.manifest FROM file_versions v JOIN files f ON f.id = v.file_id
            WHERE v.file_id = @fileId AND v.version = COALESCE(@version, f.latest_version)
            """, new { fileId, version })
            ?? throw OpenholdException.NotFound($"File {fileId} has no version {version?.ToString() ?? "(latest)"}.");
    }

    /// <summary>The PNG preview stored with a version (defaults to the latest), or null if it has none.</summary>
    public async Task<byte[]?> GetThumbnailAsync(Guid fileId, int? version, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.Local.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<byte[]?>("""
            SELECT v.thumbnail FROM file_versions v JOIN files f ON f.id = v.file_id
            WHERE v.file_id = @fileId AND v.version = COALESCE(@version, f.latest_version)
            """, new { fileId, version });
    }

    /// <summary>Children referenced by a version of a file (defaults to the latest).</summary>
    public async Task<IReadOnlyList<ReferenceInfo>> GetUsesAsync(Guid fileId, int? version, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.Local.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<ReferenceInfo>("""
            SELECT c.id AS FileId, c.name AS Name, fo.path AS Path, r.child_version AS Version, r.ref_path AS RefPath
            FROM files p
            JOIN file_references r ON r.parent_file_id = p.id AND r.parent_version = COALESCE(@version, p.latest_version)
            JOIN files c ON c.id = r.child_file_id
            JOIN folders fo ON fo.id = c.folder_id
            WHERE p.id = @fileId
            ORDER BY lower(c.name)
            """, new { fileId, version })).ToList();
    }

    /// <summary>
    /// Every file a file uses at any depth (an assembly's sub-assemblies, their parts, ...), following the latest
    /// version of each child, in one query. Depth is capped so a reference cycle can't loop.
    /// </summary>
    public async Task<IReadOnlyList<ReferenceInfo>> GetAllUsesAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.Local.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<ReferenceInfo>("""
            WITH RECURSIVE tree(file_id, depth) AS (
                SELECT r.child_file_id, 1
                FROM files p JOIN file_references r ON r.parent_file_id = p.id AND r.parent_version = p.latest_version
                WHERE p.id = @fileId
                UNION
                SELECT r.child_file_id, t.depth + 1
                FROM tree t
                JOIN files c ON c.id = t.file_id
                JOIN file_references r ON r.parent_file_id = c.id AND r.parent_version = c.latest_version
                WHERE t.depth < 64
            )
            SELECT DISTINCT c.id AS FileId, c.name AS Name, fo.path AS Path, c.latest_version AS Version, NULL AS RefPath
            FROM tree t JOIN files c ON c.id = t.file_id JOIN folders fo ON fo.id = c.folder_id
            WHERE c.deleted_at IS NULL AND c.id <> @fileId
            ORDER BY Name
            """, new { fileId })).ToList();
    }

    /// <summary>Files whose latest version references any version of this file.</summary>
    public async Task<IReadOnlyList<ReferenceInfo>> GetWhereUsedAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.Local.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<ReferenceInfo>("""
            SELECT p.id AS FileId, p.name AS Name, fo.path AS Path, p.latest_version AS Version, r.ref_path AS RefPath
            FROM file_references r
            JOIN files p ON p.id = r.parent_file_id AND r.parent_version = p.latest_version
            JOIN folders fo ON fo.id = p.folder_id
            WHERE r.child_file_id = @fileId AND p.deleted_at IS NULL
            ORDER BY lower(p.name)
            """, new { fileId })).ToList();
    }

    /// <summary>Columns that map to <see cref="FileRow"/>; use with <see cref="FileJoins"/> on <c>files f</c>.</summary>
    internal const string FileColumns = """
        f.id AS Id, f.folder_id AS FolderId, f.name AS Name, f.latest_version AS LatestVersion,
        v.length AS Length, encode(v.content_hash, 'hex') AS ContentHash, v.created_at AS ModifiedAt,
        u.username AS LockedBy, l.site_id AS LockSite, l.machine AS LockMachine, l.state AS LockState,
        (v.vault_properties || v.properties)::text AS PropertiesJson, v.vault_properties::text AS VaultPropertiesJson,
        f.category_id AS CategoryId, v.state AS State
        """;

    /// <summary>A file as read from the database; <see cref="ToInfo"/> gives the wire shape.</summary>
    internal sealed record FileRow(Guid Id, Guid FolderId, string Name, int LatestVersion, long? Length, string? ContentHash, DateTime? ModifiedAt,
        string? LockedBy, string? LockSite, string? LockMachine, string? LockState, string? PropertiesJson, string? VaultPropertiesJson, string? CategoryId, string? State)
    {
        public VaultFileInfo ToInfo(VaultConfiguration configuration)
        {
            var state = configuration.StateOf(CategoryId, State);
            return new(Id, FolderId, Name, LatestVersion, Length, ContentHash, ModifiedAt, LockedBy, LockSite, LockMachine, LockState)
            {
                Properties = string.IsNullOrEmpty(PropertiesJson) ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(PropertiesJson) ?? [],
                VaultProperties = string.IsNullOrEmpty(VaultPropertiesJson) ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(VaultPropertiesJson) ?? [],
                Category = CategoryId,
                CategoryName = configuration.Category(CategoryId)?.Name,
                State = state?.Name,
                StateLocked = state?.Locked == true,
                StateReleased = state?.Released == true,
                StateObsolete = state?.Obsolete == true,
            };
        }
    }

    /// <summary>The latest version and current lock of <c>files f</c>, aliased as <c>v</c>, <c>l</c> and <c>u</c>.</summary>
    internal const string FileJoins = """
        LEFT JOIN file_versions v ON v.file_id = f.id AND v.version = f.latest_version
        LEFT JOIN locks l ON l.file_id = f.id
        LEFT JOIN users u ON u.id = l.user_id
        """;

    private const string FileSelect = $"SELECT {FileColumns} FROM files f {FileJoins}";

    private static Task<FolderInfo?> GetFolderAsync(NpgsqlConnection connection, string path, NpgsqlTransaction? transaction = null) =>
        connection.QuerySingleOrDefaultAsync<FolderInfo?>(
            "SELECT id AS Id, parent_id AS ParentId, name AS Name, path AS Path FROM folders WHERE lower(path) = lower(@path)",
            new { path }, transaction);
}
