using System.Text.Json;
using Dapper;
using Npgsql;
using Openhold.Core.Configuration;
using Openhold.Core.Contracts;
using Openhold.Server.Configuration;
using Openhold.Server.Data;
using Openhold.Server.Erp;
using Openhold.Server.Identity;
using Openhold.Server.Numbering;

namespace Openhold.Server.Items;

/// <summary>
/// The item master: one universal part number per part. Anyone may create a Draft item, which takes the next number
/// from the items numbering scheme straight away (so nobody invents their own); a part librarian checks it and
/// releases it. Numbers are never reused, and items are made obsolete rather than deleted.
/// </summary>
public sealed class ItemService(OpenholdDb db, ConfigurationService configuration, ErpConnections erpConnections, VaultInstance vault)
{
    /// <summary>Released and obsolete items go to the ERPs; drafts stay in Openhold until a part librarian releases them.</summary>
    private async Task QueueForErpAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, string state)
    {
        if (state != ItemStates.Draft)
            await ErpSyncService.QueueAsync(connection, transaction, await erpConnections.ForVaultAsync(vault, connection, transaction), id);
    }

    public const int MaxSearchResults = 1000;

    private static readonly string[] FileRoles = ["model", "drawing", "datasheet", "other"];

    public async Task<ItemInfo> CreateAsync(Actor actor, SaveItemRequest request, CancellationToken cancellationToken = default)
    {
        var rules = (await configuration.GetAsync(cancellationToken)).Configuration;
        var item = Clean(rules, request);

        await using var connection = await db.Primary.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var id = Guid.NewGuid();
        await RefuseDuplicatesAsync(connection, transaction, id, item);
        // The item's class decides its numbering scheme (the catalogue's scheme unless a class says otherwise).
        var schemeId = rules.ItemNumberingSchemeFor(item.ClassId)
            ?? throw OpenholdException.Conflict("no_catalogue_numbering",
                "Catalogue items have no numbering scheme yet. An administrator chooses one in Administration › Item Classes.");
        var number = (await NumberingService.IssueAsync(connection, transaction, actor, schemeId,
            request.NumberFields, 1, committed: true, cancellationToken)).Single().Formatted;
        await connection.ExecuteAsync("""
            INSERT INTO items (id, number, description, unit, kind, class_id, attributes, notes, created_by, updated_by)
            VALUES (@id, @number, @Description, @Unit, @Kind, @ClassId, @attributes::jsonb, @Notes, @userId, @userId)
            """, new { id, number, item.Description, item.Unit, item.Kind, item.ClassId, attributes = Json(item.Attributes), item.Notes, userId = actor.UserId }, transaction);
        await WriteChildrenAsync(connection, transaction, id, item);
        await AuditAsync(connection, transaction, actor, "item_created", id, number, new { item.Description, item.Kind });
        await transaction.CommitAsync(cancellationToken);
        return await LoadAsync(connection, rules, id) ?? throw new InvalidOperationException("The new item was not found.");
    }

    public async Task<ItemInfo> UpdateAsync(Actor actor, Guid id, SaveItemRequest request, CancellationToken cancellationToken = default)
    {
        var rules = (await configuration.GetAsync(cancellationToken)).Configuration;
        var item = Clean(rules, request);

        await using var connection = await db.Primary.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var existing = await LockAsync(connection, transaction, id, request.RowVersion);
        var librarian = rules.HasPrivilege(actor.Username, Privileges.PartLibrarian);
        if (!librarian && (existing.State != ItemStates.Draft || existing.CreatedBy != actor.UserId))
        {
            throw new OpenholdException(StatusCodes.Status403Forbidden, "not_allowed", existing.State == ItemStates.Draft
                ? $"{existing.Number} is someone else's draft. Ask them or a part librarian to change it."
                : $"{existing.Number} is {existing.State.ToLowerInvariant()}; only a part librarian can change it.");
        }

        await RefuseDuplicatesAsync(connection, transaction, id, item);
        await connection.ExecuteAsync("""
            UPDATE items SET description = @Description, unit = @Unit, kind = @Kind, class_id = @ClassId, attributes = @attributes::jsonb,
                notes = @Notes, updated_by = @userId, updated_at = now(), row_version = row_version + 1
            WHERE id = @id
            """, new { id, item.Description, item.Unit, item.Kind, item.ClassId, attributes = Json(item.Attributes), item.Notes, userId = actor.UserId }, transaction);
        await WriteChildrenAsync(connection, transaction, id, item);
        await AuditAsync(connection, transaction, actor, "item_updated", id, existing.Number, new { item.Description });
        await QueueForErpAsync(connection, transaction, id, existing.State);
        await transaction.CommitAsync(cancellationToken);
        return (await LoadAsync(connection, rules, id))!;
    }

    /// <summary>
    /// Releases, obsoletes or reinstates an item (part librarians only). Release checks that the item is classified,
    /// has its class's required attributes and, if bought, at least one approved manufacturer part.
    /// </summary>
    public async Task<ItemInfo> ChangeStateAsync(Actor actor, Guid id, ItemStateRequest request, CancellationToken cancellationToken = default)
    {
        var rules = (await configuration.GetAsync(cancellationToken)).Configuration;
        if (!rules.HasPrivilege(actor.Username, Privileges.PartLibrarian))
            throw new OpenholdException(StatusCodes.Status403Forbidden, "not_allowed", "Releasing and obsoleting items needs the Part Librarian privilege.");

        await using var connection = await db.Primary.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var existing = await LockAsync(connection, transaction, id, request.RowVersion);
        var allowed = (existing.State, request.State) switch
        {
            (ItemStates.Draft, ItemStates.Released) => true,
            (ItemStates.Draft, ItemStates.Obsolete) => true,
            (ItemStates.Released, ItemStates.Obsolete) => true,
            (ItemStates.Obsolete, ItemStates.Released) => true,
            _ => false,
        };
        if (!allowed)
            throw OpenholdException.BadRequest("invalid_state_change", $"{existing.Number} can't go from {existing.State} to {request.State}.");
        if (request.State == ItemStates.Obsolete && string.IsNullOrWhiteSpace(request.Comment))
            throw OpenholdException.BadRequest("comment_required", "Say why the item is obsolete (e.g. what replaces it).");

        if (request.State == ItemStates.Released)
        {
            var item = (await LoadAsync(connection, rules, id, transaction))!;
            var problems = ReleaseProblems(rules, item);
            if (problems.Count > 0)
                throw OpenholdException.BadRequest("not_ready_to_release", $"{item.Number} isn't ready to release: {string.Join("; ", problems)}.", new { problems });
        }

        await connection.ExecuteAsync("""
            UPDATE items SET state = @state, updated_by = @userId, updated_at = now(), row_version = row_version + 1,
                released_by = CASE WHEN @state = 'Released' THEN @userId ELSE released_by END,
                released_at = CASE WHEN @state = 'Released' THEN now() ELSE released_at END
            WHERE id = @id
            """, new { id, state = request.State, userId = actor.UserId }, transaction);
        await AuditAsync(connection, transaction, actor, "item_state_changed", id, existing.Number,
            new { from = existing.State, to = request.State, comment = request.Comment });
        await QueueForErpAsync(connection, transaction, id, request.State);
        await transaction.CommitAsync(cancellationToken);
        return (await LoadAsync(connection, rules, id))!;
    }

    /// <summary>What stops an item being released, in words; empty when it's ready.</summary>
    public static IReadOnlyList<string> ReleaseProblems(VaultConfiguration rules, ItemInfo item)
    {
        var problems = new List<string>();
        if (item.ClassId is null)
            problems.Add("choose its class");
        foreach (var attribute in rules.ItemAttributesFor(item.ClassId).Where(a => a.Required && !item.Attributes.ContainsKey(a.Name)))
            problems.Add($"fill in {attribute.Name}");
        if (item.Kind == ItemKinds.Bought && item.ManufacturerParts.Count == 0)
            problems.Add("add the manufacturer and their part number");
        return problems;
    }

    public async Task<ItemInfo> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var rules = (await configuration.GetAsync(cancellationToken)).Configuration;
        await using var connection = await db.Local.OpenConnectionAsync(cancellationToken);
        return await LoadAsync(connection, rules, id) ?? throw OpenholdException.NotFound($"Item {id} does not exist.");
    }

    /// <summary>An item by its part number, or by another number it's known by (a cross-reference).</summary>
    public async Task<ItemInfo> GetByNumberAsync(string number, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.Local.OpenConnectionAsync(cancellationToken);
        var id = await connection.ExecuteScalarAsync<Guid?>("""
            SELECT id FROM items WHERE lower(number) = lower(@number)
            UNION ALL
            SELECT item_id FROM item_cross_references WHERE lower(number) = lower(@number)
            LIMIT 1
            """, new { number = number.Trim() });
        return id is { } found ? await GetAsync(found, cancellationToken) : throw OpenholdException.NotFound($"No item has the number '{number}'.");
    }

    /// <summary>
    /// Finds items: every word of the text must appear in the number, description, an attribute, a manufacturer or
    /// their part number (ignoring spaces and dashes), or a cross-reference. A class includes its sub-classes.
    /// </summary>
    public async Task<IReadOnlyList<ItemSummary>> SearchAsync(ItemSearchRequest request, CancellationToken cancellationToken = default)
    {
        var rules = (await configuration.GetAsync(cancellationToken)).Configuration;
        var conditions = new List<string>();
        var parameters = new DynamicParameters();
        var words = (request.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(8).ToList();
        for (var i = 0; i < words.Count; i++)
        {
            parameters.Add($"like{i}", $"%{EscapeLike(words[i])}%");
            parameters.Add($"key{i}", $"%{EscapeLike(Key(words[i]))}%");
            conditions.Add($"""
                (i.number ILIKE @like{i} OR i.description ILIKE @like{i} OR i.attributes::text ILIKE @like{i} OR i.notes ILIKE @like{i}
                 OR EXISTS (SELECT 1 FROM item_manufacturer_parts p WHERE p.item_id = i.id AND (p.manufacturer ILIKE @like{i} OR (length(@key{i}) > 2 AND p.part_number_key LIKE @key{i})))
                 OR EXISTS (SELECT 1 FROM item_cross_references x WHERE x.item_id = i.id AND x.number ILIKE @like{i}))
                """);
        }
        if (request.ClassId is { } classId)
        {
            parameters.Add("classes", (rules.ItemClasses ?? []).Where(c => rules.ItemClassChain(c.Id).Any(p => string.Equals(p.Id, classId, StringComparison.OrdinalIgnoreCase))).Select(c => c.Id).ToArray());
            conditions.Add("i.class_id = ANY(@classes)");
        }
        if (request.State is { } state)
        {
            parameters.Add("state", state);
            conditions.Add("i.state = @state");
        }
        if (request.Kind is { } kind)
        {
            parameters.Add("kind", kind);
            conditions.Add("i.kind = @kind");
        }
        parameters.Add("limit", Math.Clamp(request.Limit, 1, MaxSearchResults));

        await using var connection = await db.Local.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<SummaryRow>($"""
            {SummarySelect}
            {(conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "")}
            ORDER BY i.number
            LIMIT @limit
            """, parameters);
        return rows.Select(r => r.ToSummary(rules)).ToList();
    }

    /// <summary>
    /// Items that may already be this part: the same manufacturer part number (ignoring case, spaces and
    /// punctuation), from any manufacturer. Shown before creating an item so the same part doesn't get two numbers.
    /// </summary>
    public async Task<IReadOnlyList<DuplicateCandidate>> FindDuplicatesAsync(DuplicateCheckRequest request, CancellationToken cancellationToken = default)
    {
        var rules = (await configuration.GetAsync(cancellationToken)).Configuration;
        var keys = request.ManufacturerParts.Select(p => Key(p.PartNumber)).Where(k => k.Length > 0).Distinct().ToArray();
        if (keys.Length == 0)
            return [];
        await using var connection = await db.Local.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<(Guid Id, string Manufacturer, string PartNumber)>("""
            SELECT item_id, manufacturer, part_number FROM item_manufacturer_parts
            WHERE part_number_key = ANY(@keys) AND item_id IS DISTINCT FROM @exclude
            """, new { keys, exclude = request.ExcludeItemId });
        var candidates = new List<DuplicateCandidate>();
        foreach (var match in rows.GroupBy(r => r.Id))
        {
            var summary = (await connection.QueryAsync<SummaryRow>($"{SummarySelect} WHERE i.id = @id", new { id = match.Key })).Single().ToSummary(rules);
            candidates.Add(new DuplicateCandidate(summary,
                string.Join("; ", match.Select(m => $"{m.Manufacturer} {m.PartNumber} is already {summary.Number}"))));
        }
        return candidates;
    }

    /// <summary>The items a file defines or describes.</summary>
    public async Task<IReadOnlyList<ItemSummary>> ForFileAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        var rules = (await configuration.GetAsync(cancellationToken)).Configuration;
        await using var connection = await db.Local.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<SummaryRow>($"{SummarySelect} JOIN item_files l ON l.item_id = i.id WHERE l.file_id = @fileId ORDER BY i.number", new { fileId }))
            .Select(r => r.ToSummary(rules)).ToList();
    }

    /// <summary>Checks and tidies an item as typed: trimmed text, known class, allowed attribute values.</summary>
    private static SaveItemRequest Clean(VaultConfiguration rules, SaveItemRequest request)
    {
        var description = request.Description?.Trim() ?? "";
        if (description.Length is 0 or > 250)
            throw OpenholdException.BadRequest("invalid_description", "An item needs a description of up to 250 characters.");
        if (request.Kind is not (ItemKinds.Made or ItemKinds.Bought))
            throw OpenholdException.BadRequest("invalid_kind", "An item is either made or bought.");
        var unit = string.IsNullOrWhiteSpace(request.Unit) ? "each" : request.Unit.Trim();
        if (unit.Length > 20)
            throw OpenholdException.BadRequest("invalid_unit", "The unit can be at most 20 characters (e.g. each, m, kg).");
        var itemClass = request.ClassId is { Length: > 0 } classId
            ? rules.ItemClass(classId) ?? throw OpenholdException.BadRequest("unknown_class", $"There is no item class '{classId}'.")
            : null;

        var definitions = rules.ItemAttributesFor(itemClass?.Id).ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in request.Attributes ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value))
                continue;
            var definition = definitions.GetValueOrDefault(name.Trim());
            if (definition?.Choices is { Count: > 0 } choices && !choices.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase))
                throw OpenholdException.BadRequest("invalid_attribute", $"{definition.Name} must be one of: {string.Join(", ", choices)}.");
            attributes[definition?.Name ?? name.Trim()] = definition?.Choices?.First(c => string.Equals(c, value.Trim(), StringComparison.OrdinalIgnoreCase)) ?? value.Trim();
        }

        var parts = (request.ManufacturerParts ?? [])
            .Select(p => new ManufacturerPart(p.Manufacturer?.Trim() ?? "", p.PartNumber?.Trim() ?? "", p.Preferred))
            .ToList();
        if (parts.Any(p => p.Manufacturer.Length == 0 || Key(p.PartNumber).Length == 0))
            throw OpenholdException.BadRequest("invalid_manufacturer_part", "Each manufacturer part needs the manufacturer and their part number.");
        parts = parts.DistinctBy(p => (Key(p.Manufacturer), Key(p.PartNumber))).ToList();
        if (parts.Count > 0 && !parts.Any(p => p.Preferred))
            parts[0] = parts[0] with { Preferred = true };
        else if (parts.Count(p => p.Preferred) > 1)
            parts = parts.Select((p, i) => p with { Preferred = i == parts.FindIndex(q => q.Preferred) }).ToList();

        var references = (request.CrossReferences ?? [])
            .Select(r => new CrossReference(r.System?.Trim() ?? "", r.Number?.Trim() ?? ""))
            .ToList();
        if (references.Any(r => r.System.Length == 0 || r.Number.Length == 0))
            throw OpenholdException.BadRequest("invalid_cross_reference", "Each cross-reference needs the system (e.g. Legacy vault) and the number there.");

        var files = (request.Files ?? []).DistinctBy(f => f.FileId).ToList();
        if (files.FirstOrDefault(f => !FileRoles.Contains(f.Role)) is { } badRole)
            throw OpenholdException.BadRequest("invalid_file_role", $"'{badRole.Role}' isn't a file role; use {string.Join(", ", FileRoles)}.");

        return request with
        {
            Description = description, Unit = unit, ClassId = itemClass?.Id, Attributes = attributes,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            ManufacturerParts = parts, CrossReferences = references.DistinctBy(r => (r.System.ToLowerInvariant(), r.Number.ToLowerInvariant())).ToList(),
            Files = files,
        };
    }

    /// <summary>Refuses manufacturer parts or cross-references another item already has, naming that item.</summary>
    private static async Task RefuseDuplicatesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, SaveItemRequest item)
    {
        var parts = item.ManufacturerParts ?? [];
        if (parts.Count > 0)
        {
            var taken = await connection.QueryFirstOrDefaultAsync<TakenPart>("""
                SELECT i.number AS Number, m.manufacturer AS Manufacturer, m.part_number AS PartNumber
                FROM item_manufacturer_parts m JOIN items i ON i.id = m.item_id
                JOIN unnest(@manufacturers, @partNumbers) AS q(manufacturer_key, part_number_key)
                    ON q.manufacturer_key = m.manufacturer_key AND q.part_number_key = m.part_number_key
                WHERE m.item_id <> @id
                LIMIT 1
                """, new { id, manufacturers = parts.Select(p => Key(p.Manufacturer)).ToArray(), partNumbers = parts.Select(p => Key(p.PartNumber)).ToArray() }, transaction);
            if (taken is { } duplicate)
                throw OpenholdException.Conflict("duplicate_manufacturer_part",
                    $"{duplicate.Manufacturer} {duplicate.PartNumber} is already {duplicate.Number}. Use that item instead of making a second number for the same part.",
                    new { number = duplicate.Number });
        }

        var references = item.CrossReferences ?? [];
        if (references.Count > 0)
        {
            var taken = await connection.QueryFirstOrDefaultAsync<TakenReference>("""
                SELECT i.number AS Number, x.system AS System, x.number AS Reference
                FROM item_cross_references x JOIN items i ON i.id = x.item_id
                JOIN unnest(@systems, @numbers) AS q(system, number) ON lower(q.system) = lower(x.system) AND lower(q.number) = lower(x.number)
                WHERE x.item_id <> @id
                LIMIT 1
                """, new { id, systems = references.Select(r => r.System).ToArray(), numbers = references.Select(r => r.Number).ToArray() }, transaction);
            if (taken is { } duplicate)
                throw OpenholdException.Conflict("duplicate_cross_reference",
                    $"{duplicate.System} number {duplicate.Reference} already belongs to {duplicate.Number}.", new { number = duplicate.Number });
        }

        var files = (item.Files ?? []).Select(f => f.FileId).ToArray();
        if (files.Length > 0)
        {
            var found = await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM files WHERE id = ANY(@files) AND deleted_at IS NULL", new { files }, transaction);
            if (found != files.Length)
                throw OpenholdException.BadRequest("unknown_file", "Some linked files don't exist or are in the trash.");
        }
    }

    private static async Task WriteChildrenAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, SaveItemRequest item)
    {
        await connection.ExecuteAsync("""
            DELETE FROM item_manufacturer_parts WHERE item_id = @id;
            DELETE FROM item_cross_references WHERE item_id = @id;
            DELETE FROM item_files WHERE item_id = @id;
            """, new { id }, transaction);
        foreach (var part in item.ManufacturerParts ?? [])
        {
            await connection.ExecuteAsync("""
                INSERT INTO item_manufacturer_parts (item_id, manufacturer, part_number, manufacturer_key, part_number_key, preferred)
                VALUES (@id, @Manufacturer, @PartNumber, @manufacturerKey, @partNumberKey, @Preferred)
                """, new { id, part.Manufacturer, part.PartNumber, manufacturerKey = Key(part.Manufacturer), partNumberKey = Key(part.PartNumber), part.Preferred }, transaction);
        }
        foreach (var reference in item.CrossReferences ?? [])
            await connection.ExecuteAsync("INSERT INTO item_cross_references (item_id, system, number) VALUES (@id, @System, @Number)", new { id, reference.System, reference.Number }, transaction);
        foreach (var file in item.Files ?? [])
            await connection.ExecuteAsync("INSERT INTO item_files (item_id, file_id, role) VALUES (@id, @FileId, @Role)", new { id, file.FileId, file.Role }, transaction);
    }

    private static async Task<(string Number, string State, Guid CreatedBy)> LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, int? rowVersion)
    {
        var row = await connection.QuerySingleOrDefaultAsync<LockRow>(
            "SELECT number AS Number, state AS State, created_by AS CreatedBy, row_version AS RowVersion FROM items WHERE id = @id FOR UPDATE", new { id }, transaction)
            ?? throw OpenholdException.NotFound($"Item {id} does not exist.");
        if (rowVersion != row.RowVersion)
            throw OpenholdException.Conflict("item_changed", $"{row.Number} was changed by someone else since you opened it. Reopen it and make your change again.");
        return (row.Number, row.State, row.CreatedBy);
    }

    /// <summary>An item in full, or null; for services that already hold a connection and the configuration.</summary>
    internal static async Task<ItemInfo?> LoadAsync(NpgsqlConnection connection, VaultConfiguration rules, Guid id, NpgsqlTransaction? transaction = null)
    {
        var row = await connection.QuerySingleOrDefaultAsync<ItemRow>("""
            SELECT i.id AS Id, i.number AS Number, i.description AS Description, i.unit AS Unit, i.kind AS Kind, i.class_id AS ClassId,
                   i.state AS State, i.attributes::text AS AttributesJson, i.notes AS Notes, c.username AS CreatedBy, i.created_at AS CreatedAt,
                   i.updated_at AS UpdatedAt, r.username AS ReleasedBy, i.released_at AS ReleasedAt, i.row_version AS RowVersion
            FROM items i JOIN users c ON c.id = i.created_by LEFT JOIN users r ON r.id = i.released_by
            WHERE i.id = @id
            """, new { id }, transaction);
        if (row is null)
            return null;
        var parts = await connection.QueryAsync<ManufacturerPart>(
            "SELECT manufacturer AS Manufacturer, part_number AS PartNumber, preferred AS Preferred FROM item_manufacturer_parts WHERE item_id = @id ORDER BY preferred DESC, manufacturer",
            new { id }, transaction);
        var references = await connection.QueryAsync<CrossReference>(
            "SELECT system AS System, number AS Number FROM item_cross_references WHERE item_id = @id ORDER BY system, number", new { id }, transaction);
        var files = await connection.QueryAsync<(Guid FileId, string Role, string Name, string Path)>("""
            SELECT l.file_id, l.role, f.name, fo.path FROM item_files l JOIN files f ON f.id = l.file_id JOIN folders fo ON fo.id = f.folder_id
            WHERE l.item_id = @id ORDER BY l.role, f.name
            """, new { id }, transaction);
        return new ItemInfo(row.Id, row.Number, row.Description, row.Unit, row.Kind, row.ClassId, rules.ItemClassPath(row.ClassId), row.State,
            JsonSerializer.Deserialize<Dictionary<string, string>>(row.AttributesJson) ?? [], row.Notes,
            parts.ToList(), references.ToList(), files.Select(f => new ItemFileLink(f.FileId, f.Role) { Name = f.Name, FolderPath = f.Path }).ToList(),
            row.CreatedBy, row.CreatedAt, row.UpdatedAt, row.ReleasedBy, row.ReleasedAt, row.RowVersion);
    }

    private static Task AuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Actor actor, string action, Guid id, string number, object details) =>
        connection.ExecuteAsync("""
            INSERT INTO audit_events (user_id, site_id, action, data)
            VALUES (@userId, @siteId, @action, jsonb_build_object('item', @id::text, 'number', @number::text) || @details::jsonb)
            """, new { userId = actor.UserId, siteId = actor.SiteId, action, id, number, details = JsonSerializer.Serialize(details) }, transaction);

    /// <summary>A part number compared ignoring case, spaces and punctuation: "LC1-D09 M7" and "lc1d09m7" match.</summary>
    public static string Key(string? text) => new((text ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string EscapeLike(string text) => text.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    private static string Json(IReadOnlyDictionary<string, string>? attributes) => JsonSerializer.Serialize(attributes ?? new Dictionary<string, string>());

    private const string SummarySelect = """
        SELECT i.id AS Id, i.number AS Number, i.description AS Description, i.kind AS Kind, i.class_id AS ClassId, i.state AS State,
               m.manufacturer AS Manufacturer, m.part_number AS PartNumber
        FROM items i
        LEFT JOIN LATERAL (SELECT manufacturer, part_number FROM item_manufacturer_parts WHERE item_id = i.id ORDER BY preferred DESC, manufacturer LIMIT 1) m ON true
        """;

    private sealed record SummaryRow(Guid Id, string Number, string Description, string Kind, string? ClassId, string State, string? Manufacturer, string? PartNumber)
    {
        public ItemSummary ToSummary(VaultConfiguration rules) =>
            new(Id, Number, Description, Kind, rules.ItemClassPath(ClassId), State, Manufacturer, PartNumber);
    }

    private sealed record TakenPart(string Number, string Manufacturer, string PartNumber);

    private sealed record TakenReference(string Number, string System, string Reference);

    private sealed record LockRow(string Number, string State, Guid CreatedBy, int RowVersion);

    private sealed record ItemRow(Guid Id, string Number, string Description, string Unit, string Kind, string? ClassId, string State, string AttributesJson,
        string? Notes, string CreatedBy, DateTime CreatedAt, DateTime UpdatedAt, string? ReleasedBy, DateTime? ReleasedAt, int RowVersion);
}
