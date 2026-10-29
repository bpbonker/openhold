using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Openhold.Core.Configuration;
using Openhold.Core.Content;
using Openhold.Core.Contracts;
using Openhold.Core.Files;
using Openhold.Server.Configuration;
using Openhold.Server.Data;
using Openhold.Server.Identity;

namespace Openhold.Server.CheckIn;

/// <summary>
/// Commits a changeset: every file in it gets a new version in one transaction, or none do.
/// The changeset id comes from the client, so a retried check-in (e.g. after a dropped connection
/// or a site outbox draining after a WAN outage) returns the original result instead of committing twice.
/// </summary>
public sealed class CheckInService(OpenholdDb db, IChunkStore chunkStore, IOptions<OpenholdOptions> options)
{
    public const int MaxThumbnailBytes = 512 * 1024;

    public async Task<CheckInResult> CheckInAsync(Actor actor, CheckInRequest request, CancellationToken cancellationToken = default)
    {
        var manifests = Validate(request);
        await EnsureChunksPresentAsync(manifests, cancellationToken);

        await using var connection = await db.Primary.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var inserted = await connection.ExecuteScalarAsync<Guid?>("""
            INSERT INTO changesets (id, user_id, site_id, machine, comment)
            VALUES (@id, @userId, @siteId, @machine, @comment)
            ON CONFLICT (id) DO NOTHING
            RETURNING id
            """, new { id = request.ChangesetId, userId = actor.UserId, siteId = actor.SiteId, machine = actor.Machine, comment = request.Comment ?? "" }, transaction);

        if (inserted is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return await GetExistingResultAsync(connection, actor, request.ChangesetId);
        }

        var configuration = (await ConfigurationService.LoadAsync(connection, transaction, options.Value)).Configuration;
        var (fileIds, initialStates) = await LockOrCreateFilesAsync(connection, transaction, actor, request, configuration);
        var versions = new List<CommittedVersion>(request.Files.Count);

        for (var i = 0; i < request.Files.Count; i++)
        {
            var file = request.Files[i];
            var (version, name, folderId) = await connection.QuerySingleAsync<(int, string, Guid)>(
                "UPDATE files SET latest_version = latest_version + 1 WHERE id = @id RETURNING latest_version, name, folder_id",
                new { id = fileIds[i] }, transaction);

            await connection.ExecuteAsync("""
                INSERT INTO file_versions (file_id, version, changeset_id, length, content_hash, manifest, origin_site, thumbnail, properties, state, vault_properties)
                VALUES (@fileId, @version, @changesetId, @length, @contentHash, @manifest, @siteId,
                        -- Same content as the previous version (e.g. a rename): the previous picture is still right.
                        COALESCE(@thumbnail,
                                 (SELECT p.thumbnail FROM file_versions p
                                  WHERE p.file_id = @fileId AND p.version = @version - 1 AND p.content_hash = @contentHash)),
                        -- No properties sent (e.g. a check-in from Explorer): keep the previous version's, don't erase them.
                        COALESCE(@properties::jsonb,
                                 (SELECT p.properties FROM file_versions p WHERE p.file_id = @fileId AND p.version = @version - 1),
                                 '{}'::jsonb),
                        -- A new file starts in its lifecycle's first state; a check-in never changes a file's state.
                        COALESCE(@state, (SELECT p.state FROM file_versions p WHERE p.file_id = @fileId AND p.version = @version - 1)),
                        -- Vault-only properties (ones the file itself can't hold) are never dropped by a check-in from the CAD program.
                        COALESCE(@vaultProperties::jsonb,
                                 (SELECT p.vault_properties FROM file_versions p WHERE p.file_id = @fileId AND p.version = @version - 1),
                                 '{}'::jsonb))
                """, new
            {
                fileId = fileIds[i],
                version,
                changesetId = request.ChangesetId,
                length = manifests[i].Length,
                contentHash = manifests[i].FileHash.Bytes.ToArray(),
                manifest = file.Manifest,
                siteId = actor.SiteId,
                properties = file.Properties is null ? null : JsonSerializer.Serialize(file.Properties),
                vaultProperties = file.VaultProperties is null ? null : JsonSerializer.Serialize(file.VaultProperties),
                thumbnail = file.Thumbnail,
                state = initialStates[i],
            }, transaction);

            versions.Add(new CommittedVersion(fileIds[i], folderId, name, version));
        }

        // The chunks were checked to be in this site's store, so the other sites can fetch the contents from here.
        await connection.ExecuteAsync("""
            INSERT INTO content_presence (content_hash, site_id) SELECT unnest(@hashes), @siteId ON CONFLICT DO NOTHING
            """, new { hashes = manifests.Select(m => m.FileHash.Bytes.ToArray()).ToArray(), siteId = actor.SiteId }, transaction);

        // References are written after every file has its new version, so a parent can point at
        // the version of a child committed in this same changeset.
        for (var i = 0; i < request.Files.Count; i++)
        {
            if (request.Files[i].References is null && request.Files[i].FileId is not null)
            {
                // No references sent (e.g. a check-in from Explorer): keep the previous version's, pointing at
                // each child's current version, rather than silently dropping Uses / Where Used.
                await connection.ExecuteAsync("""
                    INSERT INTO file_references (parent_file_id, parent_version, child_file_id, child_version, ref_path)
                    SELECT r.parent_file_id, @parentVersion, r.child_file_id, c.latest_version, r.ref_path
                    FROM file_references r JOIN files c ON c.id = r.child_file_id
                    WHERE r.parent_file_id = @parentId AND r.parent_version = @parentVersion - 1
                    """, new { parentId = fileIds[i], parentVersion = versions[i].Version }, transaction);
                continue;
            }

            foreach (var reference in request.Files[i].References ?? [])
            {
                var childId = reference.ChildFileId ?? fileIds[reference.ChildIndex!.Value];
                var rows = await connection.ExecuteAsync("""
                    INSERT INTO file_references (parent_file_id, parent_version, child_file_id, child_version, ref_path)
                    SELECT @parentId, @parentVersion, f.id, COALESCE(@childVersion, f.latest_version), @refPath
                    FROM files f
                    WHERE f.id = @childId AND f.latest_version > 0
                    ON CONFLICT DO NOTHING
                    """, new { parentId = fileIds[i], parentVersion = versions[i].Version, childId, childVersion = reference.ChildVersion, refPath = reference.RefPath }, transaction);
                if (rows == 0 && !await ExistsAsync(connection, transaction, childId))
                    throw OpenholdException.BadRequest("unknown_reference", $"'{versions[i].Name}' references file {childId}, which is not in the vault.");
            }
        }

        await ReleaseOrRetainLocksAsync(connection, transaction, actor, request, fileIds, versions);
        await CommitNumberReservationsAsync(connection, transaction, actor, request, fileIds);

        await connection.ExecuteAsync("""
            INSERT INTO audit_events (user_id, site_id, action, file_id, changeset_id, data)
            SELECT @userId, @siteId, 'checkin', v.file_id, @changesetId, jsonb_build_object('version', v.version)
            FROM unnest(@fileIds, @versionNumbers) AS v(file_id, version)
            """, new
        {
            userId = actor.UserId,
            siteId = actor.SiteId,
            changesetId = request.ChangesetId,
            fileIds = versions.Select(v => v.FileId).ToArray(),
            versionNumbers = versions.Select(v => v.Version).ToArray(),
        }, transaction);

        await transaction.CommitAsync(cancellationToken);
        return new CheckInResult(request.ChangesetId, false, versions);
    }

    private static List<ChunkManifest> Validate(CheckInRequest request)
    {
        if (request.ChangesetId == Guid.Empty)
            throw OpenholdException.BadRequest("invalid_changeset", "A client-generated changeset id is required.");
        if (request.Files.Count == 0)
            throw OpenholdException.BadRequest("empty_changeset", "A check-in must contain at least one file.");

        var manifests = new List<ChunkManifest>(request.Files.Count);
        var seenExisting = new HashSet<Guid>();
        var seenNew = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < request.Files.Count; i++)
        {
            var file = request.Files[i];
            if (file.FileId is { } id)
            {
                if (file.FolderId is not null || file.Name is not null)
                    throw OpenholdException.BadRequest("invalid_file", $"File {i}: give either an existing file id or a folder and name, not both.");
                if (file.NewName is { } newName && (string.IsNullOrWhiteSpace(newName) || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || FileKinds.IsIgnored(newName)))
                    throw OpenholdException.BadRequest("invalid_name", $"'{newName}' is not a valid name for a vault file.");
                if (!seenExisting.Add(id))
                    throw OpenholdException.BadRequest("duplicate_file", $"File {id} appears more than once in the changeset.");
            }
            else
            {
                if (file.NewName is not null || file.NewFolderId is not null)
                    throw OpenholdException.BadRequest("invalid_file", $"File {i}: only files already in the vault can be renamed or moved.");
                if (file.FolderId is null || string.IsNullOrWhiteSpace(file.Name))
                    throw OpenholdException.BadRequest("invalid_file", $"File {i}: a new file needs a folder and a name.");
                if (file.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || FileKinds.IsIgnored(file.Name))
                    throw OpenholdException.BadRequest("invalid_name", $"'{file.Name}' is not a valid name for a vault file.");
                if (!seenNew.Add($"{file.FolderId}/{file.Name}"))
                    throw OpenholdException.BadRequest("duplicate_file", $"'{file.Name}' appears more than once in the changeset.");
            }

            foreach (var reference in file.References ?? [])
            {
                var valid = (reference.ChildFileId, reference.ChildIndex) switch
                {
                    ({ }, null) => true,
                    (null, { } index) => index >= 0 && index < request.Files.Count && index != i,
                    _ => false,
                };
                if (!valid)
                    throw OpenholdException.BadRequest("invalid_reference", $"File {i} has a reference that must name exactly one child file id or a valid index in this changeset.");
            }

            if (file.Thumbnail is { Length: > MaxThumbnailBytes })
                throw OpenholdException.BadRequest("thumbnail_too_large", $"File {i}: thumbnails may be at most {MaxThumbnailBytes / 1024} KB.");

            try
            {
                manifests.Add(ManifestCodec.Decode(file.Manifest));
            }
            catch (FormatException ex)
            {
                throw OpenholdException.BadRequest("invalid_manifest", $"File {i}: {ex.Message}");
            }
        }
        return manifests;
    }

    private async Task EnsureChunksPresentAsync(IEnumerable<ChunkManifest> manifests, CancellationToken cancellationToken)
    {
        var missing = await chunkStore.FindMissingAsync(manifests.SelectMany(m => m.DistinctHashes()), cancellationToken);
        if (missing.Count > 0)
        {
            throw OpenholdException.Conflict("missing_chunks",
                $"{missing.Count} chunk(s) must be uploaded before this check-in can be committed.",
                new { missing = missing.Select(h => h.ToString()) });
        }
    }

    /// <summary>
    /// Verifies the caller holds the lock (on this machine) for every existing file and creates rows for new files,
    /// each in the category its rules choose. Returns file ids aligned with <c>request.Files</c>, and the starting
    /// lifecycle state of each new file (null for existing files, which keep theirs).
    /// </summary>
    private static async Task<(Guid[] FileIds, string?[] InitialStates)> LockOrCreateFilesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Actor actor, CheckInRequest request, VaultConfiguration configuration)
    {
        var fileIds = new Guid[request.Files.Count];
        var initialStates = new string?[request.Files.Count];
        var existingIds = request.Files.Where(f => f.FileId is not null).Select(f => f.FileId!.Value).ToArray();

        if (existingIds.Length > 0)
        {
            var locks = (await connection.QueryAsync<(Guid FileId, Guid UserId, string Machine)>(
                "SELECT file_id, user_id, machine FROM locks WHERE file_id = ANY(@existingIds) FOR UPDATE",
                new { existingIds }, transaction)).ToDictionary(l => l.FileId);

            var problems = existingIds
                .Where(id => !locks.TryGetValue(id, out var held) || held.UserId != actor.UserId || held.Machine != actor.Machine)
                .ToList();
            if (problems.Count > 0)
            {
                throw OpenholdException.Conflict("lock_not_held",
                    "You must have these files checked out on this machine to check them in.",
                    new { fileIds = problems });
            }
        }

        for (var i = 0; i < request.Files.Count; i++)
        {
            var file = request.Files[i];
            if (file.FileId is { } id)
            {
                fileIds[i] = id;
                if (file.NewName is not null || file.NewFolderId is not null)
                    await RenameOrMoveAsync(connection, transaction, id, file.NewName, file.NewFolderId);
                continue;
            }

            await EnsureNameAvailableAsync(connection, transaction, file.FolderId!.Value, file.Name!);
            fileIds[i] = Guid.NewGuid();
            var folderPath = await connection.ExecuteScalarAsync<string?>("SELECT path FROM folders WHERE id = @id", new { id = file.FolderId }, transaction)
                ?? throw OpenholdException.NotFound($"Folder {file.FolderId} does not exist.");
            var category = configuration.CategoryForNewFile(folderPath, file.Name!);
            if (category is { EnforceNaming: true })
            {
                var schemes = new List<NumberingScheme>();
                foreach (var schemeId in Openhold.Core.Numbering.NamingRules.SchemesOf(category))
                {
                    if (await Numbering.NumberingService.LoadSchemeAsync(connection, transaction, schemeId) is { } scheme)
                        schemes.Add(scheme);
                }
                if (schemes.Count > 0 && !schemes.Any(s => Openhold.Core.Numbering.NamingRules.Follows(s, file.Name!)))
                {
                    throw OpenholdException.BadRequest("naming_rule",
                        $"'{file.Name}' doesn't follow the naming rule for {category.Name} files in {folderPath}: names must look like " +
                        $"{string.Join(" or ", schemes.Select(s => $"{s.Template} ({s.Name})"))}. Save it with Save to Openhold, or get a number from Tools › New Number.",
                        new { name = file.Name });
                }
            }
            initialStates[i] = configuration.Lifecycle(category?.LifecycleId)?.Initial.Name;
            try
            {
                await connection.ExecuteAsync("""
                    INSERT INTO files (id, folder_id, name, requires_unique_name, created_by, category_id)
                    VALUES (@id, @folderId, @name, @unique, @userId, @category)
                    """, new { id = fileIds[i], folderId = file.FolderId, name = file.Name, unique = FileKinds.RequiresUniqueName(file.Name!), userId = actor.UserId, category = category?.Id }, transaction);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw OpenholdException.Conflict("name_conflict", $"'{file.Name}' was added by someone else just now.", new { name = file.Name });
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                throw OpenholdException.NotFound($"Folder {file.FolderId} does not exist.");
            }
        }
        return (fileIds, initialStates);
    }

    /// <summary>Renames and/or moves an existing file (its history goes with it), checking the new name is free.</summary>
    private static async Task RenameOrMoveAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid fileId, string? newName, Guid? newFolderId)
    {
        var (name, folderId) = await connection.QuerySingleAsync<(string, Guid)>("SELECT name, folder_id FROM files WHERE id = @fileId", new { fileId }, transaction);
        var targetName = newName?.Trim() ?? name;
        var targetFolder = newFolderId ?? folderId;
        if (string.Equals(targetName, name, StringComparison.Ordinal) && targetFolder == folderId)
            return;

        await EnsureNameAvailableAsync(connection, transaction, targetFolder, targetName, excludeFileId: fileId);
        try
        {
            await connection.ExecuteAsync(
                "UPDATE files SET name = @targetName, folder_id = @targetFolder, requires_unique_name = @unique WHERE id = @fileId",
                new { fileId, targetName, targetFolder, unique = FileKinds.RequiresUniqueName(targetName) }, transaction);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            throw OpenholdException.NotFound($"Folder {targetFolder} does not exist.");
        }
    }

    private static async Task EnsureNameAvailableAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid folderId, string name, Guid? excludeFileId = null)
    {
        var clash = await connection.QueryFirstOrDefaultAsync<string>("""
            SELECT fo.path || '/' || f.name
            FROM files f JOIN folders fo ON fo.id = f.folder_id
            WHERE f.deleted_at IS NULL AND lower(f.name) = lower(@name)
              AND (f.folder_id = @folderId OR (f.requires_unique_name AND @unique))
              AND f.id IS DISTINCT FROM @excludeFileId
            LIMIT 1
            """, new { folderId, name, unique = FileKinds.RequiresUniqueName(name), excludeFileId }, transaction);

        if (clash is not null)
        {
            var reason = FileKinds.RequiresUniqueName(name)
                ? "CAD file names must be unique across the vault so Inventor and AutoCAD never resolve the wrong file"
                : "a file with that name already exists in the folder";
            throw OpenholdException.Conflict("name_conflict", $"Cannot add '{name}': {reason} (existing: {clash}).", new { name, existing = clash });
        }
    }

    private static async Task ReleaseOrRetainLocksAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Actor actor, CheckInRequest request, Guid[] fileIds, List<CommittedVersion> versions)
    {
        var release = new List<Guid>();
        for (var i = 0; i < request.Files.Count; i++)
        {
            if (request.Files[i].KeepCheckedOut)
            {
                // New files kept checked out get a fresh lock; existing ones move their base to the new version.
                await connection.ExecuteAsync("""
                    INSERT INTO locks (file_id, user_id, site_id, machine, base_version)
                    VALUES (@fileId, @userId, @siteId, @machine, @version)
                    ON CONFLICT (file_id) DO UPDATE SET base_version = EXCLUDED.base_version, state = 'held'
                    """, new { fileId = fileIds[i], userId = actor.UserId, siteId = actor.SiteId, machine = actor.Machine, version = versions[i].Version }, transaction);
            }
            else if (request.Files[i].FileId is not null)
            {
                release.Add(fileIds[i]);
            }
        }

        if (release.Count > 0)
            await connection.ExecuteAsync("DELETE FROM locks WHERE file_id = ANY(@release)", new { release = release.ToArray() }, transaction);
    }

    /// <summary>A new file named after a number the caller reserved consumes that reservation.</summary>
    private static async Task CommitNumberReservationsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Actor actor, CheckInRequest request, Guid[] fileIds)
    {
        for (var i = 0; i < request.Files.Count; i++)
        {
            if (request.Files[i].Name is not { } name)
                continue;
            await connection.ExecuteAsync("""
                UPDATE number_reservations
                SET state = 'committed', file_id = @fileId, updated_at = now()
                WHERE lower(formatted) IN (lower(@stem), lower(@head)) AND state IN ('reserved', 'expired') AND reserved_by = @userId
                """, new
            {
                fileId = fileIds[i],
                stem = Path.GetFileNameWithoutExtension(name),
                // "P-100004-4 - Sensor.stp": the number is the part before " - ".
                head = Path.GetFileNameWithoutExtension(name).Split(" - ", 2)[0],
                userId = actor.UserId,
            }, transaction);
        }
    }

    private static async Task<bool> ExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid fileId) =>
        await connection.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM files WHERE id = @fileId AND latest_version > 0)", new { fileId }, transaction);

    /// <summary>
    /// Makes an earlier version the newest one, as a new version with that version's content, properties and
    /// references. History is never rewritten, so a restore can itself be undone. Like a check-in, it requires
    /// the file to be checked out to the caller on this machine, releases the lock, and is idempotent by changeset id.
    /// </summary>
    public async Task<CheckInResult> RestoreAsync(Actor actor, Guid fileId, RestoreRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ChangesetId == Guid.Empty)
            throw OpenholdException.BadRequest("invalid_changeset", "A client-generated changeset id is required.");

        await using var connection = await db.Primary.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var comment = string.IsNullOrWhiteSpace(request.Comment)
            ? $"Restored version {request.Version}"
            : $"Restored version {request.Version}: {request.Comment.Trim()}";
        var inserted = await connection.ExecuteScalarAsync<Guid?>("""
            INSERT INTO changesets (id, user_id, site_id, machine, comment)
            VALUES (@id, @userId, @siteId, @machine, @comment)
            ON CONFLICT (id) DO NOTHING
            RETURNING id
            """, new { id = request.ChangesetId, userId = actor.UserId, siteId = actor.SiteId, machine = actor.Machine, comment }, transaction);
        if (inserted is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return await GetExistingResultAsync(connection, actor, request.ChangesetId);
        }

        var held = await connection.QuerySingleOrDefaultAsync<(Guid UserId, string Machine)?>(
            "SELECT user_id, machine FROM locks WHERE file_id = @fileId FOR UPDATE", new { fileId }, transaction);
        if (held is not { } holder || holder.UserId != actor.UserId || holder.Machine != actor.Machine)
            throw OpenholdException.Conflict("lock_not_held", "Check the file out on this PC first, then restore the earlier version.", new { fileIds = new[] { fileId } });

        var exists = await connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM file_versions WHERE file_id = @fileId AND version = @version)", new { fileId, version = request.Version }, transaction);
        if (!exists)
            throw OpenholdException.NotFound($"This file has no version {request.Version}.");

        var (newVersion, name, folderId) = await connection.QuerySingleAsync<(int, string, Guid)>(
            "UPDATE files SET latest_version = latest_version + 1 WHERE id = @fileId RETURNING latest_version, name, folder_id",
            new { fileId }, transaction);
        var source = new { fileId, version = request.Version, newVersion, changesetId = request.ChangesetId, siteId = actor.SiteId };
        // The restored version takes the earlier content but keeps the file's current state: restoring a version
        // that was released must not make the file released again without going through its lifecycle.
        await connection.ExecuteAsync("""
            INSERT INTO file_versions (file_id, version, changeset_id, length, content_hash, manifest, origin_site, properties, thumbnail, state, vault_properties)
            SELECT o.file_id, @newVersion, @changesetId, o.length, o.content_hash, o.manifest, @siteId, o.properties, o.thumbnail, c.state, o.vault_properties
            FROM file_versions o JOIN file_versions c ON c.file_id = o.file_id AND c.version = @newVersion - 1
            WHERE o.file_id = @fileId AND o.version = @version
            """, source, transaction);
        await connection.ExecuteAsync("""
            INSERT INTO file_references (parent_file_id, parent_version, child_file_id, child_version, ref_path)
            SELECT parent_file_id, @newVersion, child_file_id, child_version, ref_path
            FROM file_references WHERE parent_file_id = @fileId AND parent_version = @version
            """, source, transaction);
        await connection.ExecuteAsync("DELETE FROM locks WHERE file_id = @fileId", new { fileId }, transaction);
        await connection.ExecuteAsync("""
            INSERT INTO audit_events (user_id, site_id, action, file_id, changeset_id, data)
            VALUES (@userId, @siteId, 'restore', @fileId, @changesetId, jsonb_build_object('from_version', @version, 'version', @newVersion))
            """, new { userId = actor.UserId, siteId = actor.SiteId, fileId, changesetId = request.ChangesetId, version = request.Version, newVersion }, transaction);

        await transaction.CommitAsync(cancellationToken);
        return new CheckInResult(request.ChangesetId, false, [new CommittedVersion(fileId, folderId, name, newVersion)]);
    }

    /// <summary>
    /// Gives a version a preview it was checked in without (e.g. from a client that couldn't capture one). Only fills a gap:
    /// a preview already recorded is never replaced. Later versions with the same content (releases, category
    /// changes) get it too. Returns how many versions were given the preview.
    /// </summary>
    public async Task<int> AddMissingThumbnailAsync(Guid fileId, int version, byte[] png, CancellationToken cancellationToken = default)
    {
        if (png.Length is 0 or > MaxThumbnailBytes)
            throw OpenholdException.BadRequest("thumbnail_too_large", $"Previews may be at most {MaxThumbnailBytes / 1024} KB.");
        if (!png.AsSpan().StartsWith(PngSignature))
            throw OpenholdException.BadRequest("not_png", "A preview must be a PNG image.");

        await using var connection = await db.Primary.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync("""
            UPDATE file_versions v SET thumbnail = @png
            FROM file_versions source
            WHERE source.file_id = @fileId AND source.version = @version
              AND v.file_id = @fileId AND v.version >= @version AND v.content_hash = source.content_hash AND v.thumbnail IS NULL
            """, new { fileId, version, png });
    }

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static async Task<CheckInResult> GetExistingResultAsync(NpgsqlConnection connection, Actor actor, Guid changesetId)
    {
        var owner = await connection.ExecuteScalarAsync<Guid>("SELECT user_id FROM changesets WHERE id = @changesetId", new { changesetId });
        if (owner != actor.UserId)
            throw OpenholdException.Conflict("changeset_id_taken", "That changeset id belongs to another user's check-in.");

        var versions = (await connection.QueryAsync<CommittedVersion>("""
            SELECT v.file_id AS FileId, f.folder_id AS FolderId, f.name AS Name, v.version AS Version
            FROM file_versions v JOIN files f ON f.id = v.file_id
            WHERE v.changeset_id = @changesetId
            ORDER BY f.name
            """, new { changesetId })).ToList();
        return new CheckInResult(changesetId, true, versions);
    }
}
