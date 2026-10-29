using System.Text.Json;
using Dapper;
using Npgsql;
using Openhold.Core.Configuration;
using Openhold.Core.Contracts;
using Openhold.Core.Numbering;
using Openhold.Server.Configuration;
using Openhold.Server.Data;
using Openhold.Server.Identity;

namespace Openhold.Server.Numbering;

/// <summary>
/// Issues part/document numbers as reservations. A number is held while the user saves; checking in a
/// file with that name commits it; cancelling releases it; abandoning it lets it expire. Every number's
/// fate is recorded, so gaps are explainable. Released numbers are only reused if the scheme allows it.
/// </summary>
/// <remarks>
/// A scheme is a template plus fields (free text or pick lists with meanings). Its counter counts separately for each
/// different text before it, so one scheme can number every area of every project from 001, or children under each
/// parent. A number already used anywhere (another scheme, a catalogue item, a file in the vault) is skipped, so a
/// scheme can be adopted part-way through. Once a scheme has issued numbers its structure is fixed.
/// </remarks>
public sealed class NumberingService(OpenholdDb db, ConfigurationService configuration)
{
    public const int MaxBatch = 500;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<NumberingScheme> SaveSchemeAsync(Actor actor, NumberingScheme scheme, CancellationToken cancellationToken = default)
    {
        var rules = (await configuration.GetAsync(cancellationToken)).Configuration;
        if (!rules.HasPrivilege(actor.Username, Privileges.ManageConfiguration))
            throw new OpenholdException(StatusCodes.Status403Forbidden, "not_allowed", "Changing numbering schemes needs the Manage Configuration privilege.");
        if (string.IsNullOrWhiteSpace(scheme.Id) || string.IsNullOrWhiteSpace(scheme.Name))
            throw OpenholdException.BadRequest("invalid_scheme", "A numbering scheme needs an id and a name.");
        NumberTemplate template;
        try
        {
            template = NumberTemplate.Parse(scheme.Template);
        }
        catch (FormatException ex)
        {
            throw OpenholdException.BadRequest("invalid_template", ex.Message);
        }
        if (scheme.ReservationTtl <= TimeSpan.Zero)
            throw OpenholdException.BadRequest("invalid_scheme", "Reservation TTL must be positive.");
        if (scheme.StartAt < 0)
            throw OpenholdException.BadRequest("invalid_scheme", "Numbers can't start below 0.");
        var fields = CleanFields(scheme.Fields, template);

        await using var connection = await db.Primary.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var existing = await connection.QuerySingleOrDefaultAsync<SchemeRow>(
            $"{SchemeSelect} WHERE s.id = @id FOR UPDATE OF s", new { id = scheme.Id }, transaction);
        if (existing is { InUse: true })
            RefuseStructuralChanges(existing, scheme, fields);

        await connection.ExecuteAsync("""
            INSERT INTO numbering_schemes (id, name, template, allow_reuse, reservation_ttl, enabled, fields, start_value, next_value, description)
            VALUES (@Id, @Name, @Template, @AllowReuse, @ReservationTtl, @Enabled, @fields::jsonb, @StartAt, @StartAt, @Description)
            ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name, template = EXCLUDED.template, allow_reuse = EXCLUDED.allow_reuse,
                reservation_ttl = EXCLUDED.reservation_ttl, enabled = EXCLUDED.enabled, fields = EXCLUDED.fields,
                start_value = EXCLUDED.start_value, description = EXCLUDED.description,
                next_value = GREATEST(numbering_schemes.next_value, EXCLUDED.start_value)
            """, new { scheme.Id, scheme.Name, scheme.Template, scheme.AllowReuse, scheme.ReservationTtl, scheme.Enabled,
                fields = JsonSerializer.Serialize(fields, Json), scheme.StartAt, scheme.Description }, transaction);
        await connection.ExecuteAsync("""
            INSERT INTO audit_events (user_id, site_id, action, data)
            VALUES (@userId, @siteId, 'numbering_scheme_saved', jsonb_build_object('scheme', @id::text, 'template', @template::text,
                'enabled', @enabled, 'before', @before::jsonb))
            """, new { userId = actor.UserId, siteId = actor.SiteId, id = scheme.Id, template = scheme.Template, enabled = scheme.Enabled,
                before = existing is null ? "null" : JsonSerializer.Serialize(new { existing.Template, existing.Enabled, existing.AllowReuse }, Json) }, transaction);
        await transaction.CommitAsync(cancellationToken);
        return scheme with { Fields = fields, InUse = existing?.InUse == true };
    }

    /// <summary>
    /// Deletes a scheme nobody needs: it has never issued a number (one that has is switched off instead, so its
    /// numbers stay explained) and no category, item class or the catalogue uses it.
    /// </summary>
    public async Task DeleteSchemeAsync(Actor actor, string schemeId, CancellationToken cancellationToken = default)
    {
        var rules = (await configuration.GetAsync(cancellationToken)).Configuration;
        if (!rules.HasPrivilege(actor.Username, Privileges.ManageConfiguration))
            throw new OpenholdException(StatusCodes.Status403Forbidden, "not_allowed", "Deleting numbering schemes needs the Manage Configuration privilege.");
        var users = rules.Categories
            .Where(c => Same(c.NumberingSchemeId) || Same(c.ChildNumberingSchemeId)).Select(c => $"category {c.Name}")
            .Concat((rules.ItemClasses ?? []).Where(c => Same(c.NumberingSchemeId)).Select(c => $"item class {c.Name}"))
            .Concat(Same(rules.CatalogueNumberingSchemeId) ? ["the catalogue"] : [])
            .ToList();
        if (users.Count > 0)
            throw OpenholdException.Conflict("scheme_used", $"It's used by {string.Join(", ", users)}; choose another scheme there first.");

        await using var connection = await db.Primary.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var scheme = await connection.QuerySingleOrDefaultAsync<SchemeRow>($"{SchemeSelect} WHERE s.id = @schemeId FOR UPDATE OF s", new { schemeId }, transaction)
            ?? throw OpenholdException.NotFound($"Numbering scheme '{schemeId}' does not exist.");
        if (scheme.InUse)
            throw OpenholdException.Conflict("scheme_in_use", $"'{scheme.Name}' has issued numbers, so it stays to explain them. Switch it off (untick In use) instead.");
        await connection.ExecuteAsync("DELETE FROM numbering_counters WHERE scheme_id = @schemeId", new { schemeId }, transaction);
        await connection.ExecuteAsync("DELETE FROM numbering_schemes WHERE id = @schemeId", new { schemeId }, transaction);
        await connection.ExecuteAsync("""
            INSERT INTO audit_events (user_id, site_id, action, data)
            VALUES (@userId, @siteId, 'numbering_scheme_deleted', jsonb_build_object('scheme', @schemeId::text, 'name', @name::text, 'template', @template::text))
            """, new { userId = actor.UserId, siteId = actor.SiteId, schemeId, name = scheme.Name, template = scheme.Template }, transaction);
        await transaction.CommitAsync(cancellationToken);

        bool Same(string? id) => string.Equals(id, schemeId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A scheme that has issued numbers keeps its template, start and fields; lists may only gain codes (meanings can be reworded).</summary>
    private static void RefuseStructuralChanges(SchemeRow existing, NumberingScheme proposed, IReadOnlyList<NumberingField> fields)
    {
        const string why = "it has already issued numbers, so changing its structure would make old and new numbers disagree. Make a new scheme instead.";
        if (!string.Equals(existing.Template, proposed.Template, StringComparison.Ordinal))
            throw OpenholdException.Conflict("scheme_in_use", $"The template of '{existing.Name}' can't change: {why}");
        if (existing.StartAt != proposed.StartAt)
            throw OpenholdException.Conflict("scheme_in_use", $"Where '{existing.Name}' starts can't change: {why}");
        foreach (var old in existing.Fields)
        {
            var now = fields.FirstOrDefault(f => string.Equals(f.Name, old.Name, StringComparison.OrdinalIgnoreCase));
            var removed = (old.Choices ?? []).Where(c => now?.Choices?.Any(n => string.Equals(n.Code, c.Code, StringComparison.OrdinalIgnoreCase)) != true).ToList();
            if (old.Choices is { Count: > 0 } && (now?.Choices is not { Count: > 0 } || removed.Count > 0))
            {
                throw OpenholdException.Conflict("scheme_in_use",
                    $"Codes can be added to '{old.Name}' but not taken away ({string.Join(", ", removed.Select(r => r.Code))}): numbers using them exist.");
            }
        }
    }

    /// <summary>Field definitions tidied and checked: one per template field, codes unique and allowed in file names.</summary>
    private static IReadOnlyList<NumberingField> CleanFields(IReadOnlyList<NumberingField>? fields, NumberTemplate template)
    {
        var cleaned = new List<NumberingField>();
        foreach (var field in fields ?? [])
        {
            var name = field.Name?.Trim() ?? "";
            if (!template.FieldNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw OpenholdException.BadRequest("invalid_field", $"'{name}' isn't used in the template; add {{field:{name}}} to it or remove the field.");
            if (cleaned.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw OpenholdException.BadRequest("invalid_field", $"The field '{name}' is listed twice.");
            var choices = (field.Choices ?? []).Select(c => new NumberingChoice(c.Code?.Trim() ?? "", c.Meaning?.Trim() ?? "")).ToList();
            if (choices.FirstOrDefault(c => c.Code.Length == 0 || c.Code.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) is { } bad)
                throw OpenholdException.BadRequest("invalid_field", $"'{bad.Code}' in {name} isn't a code that can go in a file name.");
            if (choices.GroupBy(c => c.Code, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } twice)
                throw OpenholdException.BadRequest("invalid_field", $"The code '{twice.Key}' is listed twice in {name}.");
            var from = string.IsNullOrWhiteSpace(field.From) ? null : field.From.Trim().ToLowerInvariant();
            if (!NumberingSources.IsKnown(from))
                throw OpenholdException.BadRequest("invalid_field", $"{name} can't be filled in from '{field.From}'; use folder or parent.");
            var format = string.IsNullOrWhiteSpace(field.Format) ? null : field.Format.Trim().ToLowerInvariant();
            if (!NumberingFormats.IsKnown(format))
                throw OpenholdException.BadRequest("invalid_field", $"{name} can't be '{field.Format}'; use letters, digits or letters-digits.");
            if (field.MinLength is < 1 || field.MaxLength is < 1 || field.MinLength > field.MaxLength)
                throw OpenholdException.BadRequest("invalid_field", $"The length of {name} must be at least 1, and its shortest no longer than its longest.");
            var defaultValue = string.IsNullOrWhiteSpace(field.Default) ? null : field.Default.Trim();
            var definition = new NumberingField(name, string.IsNullOrWhiteSpace(field.Label) ? null : field.Label.Trim(), choices.Count > 0 ? choices : null,
                string.IsNullOrWhiteSpace(field.Hint) ? null : field.Hint.Trim(), from, defaultValue,
                choices.Count > 0 ? null : format, choices.Count > 0 ? null : field.MinLength, choices.Count > 0 ? null : field.MaxLength);
            if (defaultValue is not null && definition.Problem(defaultValue) is { } problem)
                throw OpenholdException.BadRequest("invalid_field", $"The default for {name}, '{defaultValue}', doesn't fit: {problem}");
            cleaned.Add(definition);
        }
        return cleaned;
    }

    /// <summary>One scheme, read in the caller's transaction (e.g. to check a new file's name at check-in); null if it doesn't exist.</summary>
    public static async Task<NumberingScheme?> LoadSchemeAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string schemeId) =>
        (await connection.QuerySingleOrDefaultAsync<SchemeRow>($"{SchemeSelect} WHERE s.id = @schemeId", new { schemeId }, transaction))?.ToScheme();

    public async Task<IReadOnlyList<NumberingScheme>> ListSchemesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await db.Local.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<SchemeRow>($"{SchemeSelect} ORDER BY s.name")).Select(r => r.ToScheme()).ToList();
    }

    /// <summary>
    /// Reserves numbers (e.g. for files about to be saved). Schemes that number catalogue items are refused here:
    /// only creating an item takes one of those, so none are taken and never used.
    /// </summary>
    public async Task<IReadOnlyList<ReservedNumber>> ReserveAsync(
        Actor actor, string schemeId, int count, IReadOnlyDictionary<string, string>? fields, CancellationToken cancellationToken = default)
    {
        if (count is < 1 or > MaxBatch)
            throw OpenholdException.BadRequest("invalid_count", $"Reserve between 1 and {MaxBatch} numbers at a time.");
        var rules = (await configuration.GetAsync(cancellationToken)).Configuration;
        if (rules.NumbersCatalogueItems(schemeId))
            throw OpenholdException.Conflict("catalogue_scheme", $"'{schemeId}' numbers catalogue items: a number is issued when the item is added in the Catalogue.");

        await using var connection = await db.Primary.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var reserved = await IssueAsync(connection, transaction, actor, schemeId, fields, count, committed: false, cancellationToken);
        await connection.ExecuteAsync("""
            INSERT INTO audit_events (user_id, site_id, action, data)
            VALUES (@userId, @siteId, 'numbers_reserved', jsonb_build_object('scheme', @schemeId::text, 'numbers', @numbers::text[]))
            """, new { userId = actor.UserId, siteId = actor.SiteId, schemeId, numbers = reserved.Select(r => r.Formatted).ToArray() }, transaction);
        await transaction.CommitAsync(cancellationToken);
        return reserved;
    }

    /// <summary>The number the scheme would issue next for these field values, without reserving it.</summary>
    public async Task<NumberPreview> PreviewAsync(Actor actor, string schemeId, IReadOnlyDictionary<string, string>? fields, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.Local.OpenConnectionAsync(cancellationToken);
        var scheme = await connection.QuerySingleOrDefaultAsync<SchemeRow>($"{SchemeSelect} WHERE s.id = @schemeId", new { schemeId })
            ?? throw OpenholdException.NotFound($"Numbering scheme '{schemeId}' does not exist.");
        var template = NumberTemplate.Parse(scheme.Template);
        var context = NumberContext.For(actor.SiteId, CheckFields(scheme, template, fields));
        var scope = Scope(template, context);
        var next = scope.Length == 0
            ? Math.Max(scheme.NextValue, scheme.StartAt)
            : await connection.ExecuteScalarAsync<long?>("SELECT next_value FROM numbering_counters WHERE scheme_id = @schemeId AND scope = @scope",
                new { schemeId, scope }) ?? scheme.StartAt;
        for (var tries = 0; tries < 10_000; tries++, next++)
        {
            var formatted = template.Format(next, context);
            if (!await TakenAsync(connection, null, formatted))
                return new NumberPreview(schemeId, formatted, scope.Length == 0 ? "one counter for the whole scheme" : $"counting within {scope}");
        }
        throw OpenholdException.Conflict("number_collision", "Couldn't find a free number; check the scheme's start.");
    }

    /// <summary>
    /// Takes the next free numbers from a scheme's counter for these field values, in the caller's transaction: as
    /// reservations (to be committed by a check-in) or straight away as committed (e.g. a new catalogue item).
    /// </summary>
    public static async Task<IReadOnlyList<ReservedNumber>> IssueAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Actor actor,
        string schemeId, IReadOnlyDictionary<string, string>? fields, int count, bool committed, CancellationToken cancellationToken)
    {
        // Row lock on the scheme serialises issuers of the same scheme; at tens of users this is never contended.
        var scheme = await connection.QuerySingleOrDefaultAsync<SchemeRow>($"{SchemeSelect} WHERE s.id = @schemeId FOR UPDATE OF s", new { schemeId }, transaction)
            ?? throw OpenholdException.NotFound($"Numbering scheme '{schemeId}' does not exist.");
        if (!scheme.Enabled)
            throw OpenholdException.Conflict("scheme_disabled", $"Numbering scheme '{scheme.Name}' is switched off.");
        var template = NumberTemplate.Parse(scheme.Template);
        var context = NumberContext.For(actor.SiteId, CheckFields(scheme, template, fields));
        var scope = Scope(template, context);

        var candidates = new Queue<long>();
        if (scheme.AllowReuse)
        {
            foreach (var value in await connection.QueryAsync<long>("""
                SELECT value FROM number_reservations
                WHERE scheme_id = @schemeId AND scope = @scope AND state IN ('released', 'expired')
                ORDER BY value LIMIT @count FOR UPDATE
                """, new { schemeId, scope, count }, transaction))
            {
                candidates.Enqueue(value);
            }
        }
        if (scope.Length > 0)
        {
            await connection.ExecuteAsync("""
                INSERT INTO numbering_counters (scheme_id, scope, next_value) VALUES (@schemeId, @scope, @start) ON CONFLICT DO NOTHING
                """, new { schemeId, scope, start = scheme.StartAt }, transaction);
        }

        var issued = new List<ReservedNumber>(count);
        for (var tries = 0; issued.Count < count; tries++)
        {
            if (tries > 10_000)
                throw OpenholdException.Conflict("number_collision", $"Couldn't find {count} free number(s) in '{scheme.Name}'; check its start.");
            var value = candidates.Count > 0 ? candidates.Dequeue() : await NextAsync(connection, transaction, schemeId, scope, scheme.StartAt);
            string formatted;
            try
            {
                formatted = template.Format(value, context);
            }
            catch (ArgumentException ex)
            {
                throw OpenholdException.BadRequest("missing_field", ex.Message);
            }
            // Used already (another scheme, a catalogue item, a file named after it): skip it, never issue it twice.
            if (await TakenAsync(connection, transaction, formatted, schemeId, scope, value))
                continue;
            var expiresAt = await connection.ExecuteScalarAsync<DateTime?>("""
                INSERT INTO number_reservations (scheme_id, scope, value, formatted, state, reserved_by, site_id, expires_at)
                SELECT @schemeId, @scope, @value, @formatted, @state, @userId, @siteId, CASE WHEN @state = 'committed' THEN now() ELSE now() + s.reservation_ttl END
                FROM numbering_schemes s WHERE s.id = @schemeId
                ON CONFLICT (scheme_id, scope, value) DO UPDATE SET
                    formatted = EXCLUDED.formatted, state = EXCLUDED.state, reserved_by = EXCLUDED.reserved_by,
                    site_id = EXCLUDED.site_id, reserved_at = now(), expires_at = EXCLUDED.expires_at, file_id = NULL, updated_at = now()
                WHERE number_reservations.state IN ('released', 'expired')
                RETURNING expires_at
                """, new { schemeId, scope, value, formatted, state = committed ? "committed" : "reserved", userId = actor.UserId, siteId = actor.SiteId }, transaction);
            if (expiresAt is not null)
                issued.Add(new ReservedNumber(schemeId, value, formatted, expiresAt.Value));
        }
        return issued;
    }

    private static async Task<long> NextAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string schemeId, string scope, long start) =>
        scope.Length == 0
            ? await connection.ExecuteScalarAsync<long>("""
                UPDATE numbering_schemes SET next_value = GREATEST(next_value, @start) + 1 WHERE id = @schemeId RETURNING next_value - 1
                """, new { schemeId, start }, transaction)
            : await connection.ExecuteScalarAsync<long>("""
                UPDATE numbering_counters SET next_value = GREATEST(next_value, @start) + 1 WHERE scheme_id = @schemeId AND scope = @scope RETURNING next_value - 1
                """, new { schemeId, scope, start }, transaction);

    /// <summary>
    /// True when the number is already used: issued by any scheme (other than this very slot, when it's being reused),
    /// a catalogue item's number, or the name of a file in the vault ("PRJ01-02-001A-00.iam", "P-100004-4 - Sensor.stp").
    /// </summary>
    private static Task<bool> TakenAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string formatted,
        string? schemeId = null, string? scope = null, long? value = null) =>
        connection.ExecuteScalarAsync<bool>("""
            SELECT EXISTS (SELECT 1 FROM number_reservations
                           WHERE lower(formatted) = lower(@formatted) AND state IN ('reserved', 'committed')
                             AND NOT COALESCE(scheme_id = @schemeId AND scope = @scope AND value = @value, false))
                OR EXISTS (SELECT 1 FROM items WHERE lower(number) = lower(@formatted))
                OR EXISTS (SELECT 1 FROM files WHERE lower(name) = lower(@formatted)
                             OR starts_with(lower(name), lower(@formatted) || '.') OR starts_with(lower(name), lower(@formatted) || ' - '))
            """, new { formatted, schemeId, scope, value }, transaction);

    private static string Scope(NumberTemplate template, NumberContext context)
    {
        try
        {
            return template.CounterScope(context);
        }
        catch (ArgumentException ex)
        {
            throw OpenholdException.BadRequest("missing_field", ex.Message);
        }
    }

    /// <summary>
    /// Field values checked against the scheme: every field filled in, list fields one of their codes (as written there),
    /// typed fields of their format and length.
    /// </summary>
    private static Dictionary<string, string> CheckFields(SchemeRow scheme, NumberTemplate template, IReadOnlyDictionary<string, string>? fields)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in template.FieldNames)
        {
            var value = fields?.FirstOrDefault(f => string.Equals(f.Key, name, StringComparison.OrdinalIgnoreCase)).Value?.Trim();
            if (string.IsNullOrEmpty(value))
                throw OpenholdException.BadRequest("missing_field", $"Numbering field '{name}' is required.");
            var definition = scheme.Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            if (definition?.Problem(value) is { } problem)
                throw OpenholdException.BadRequest("invalid_field", problem);
            values[name] = definition?.Choices?.First(c => string.Equals(c.Code, value, StringComparison.OrdinalIgnoreCase)).Code ?? value;
        }
        return values;
    }

    /// <summary>Gives back numbers the caller reserved but did not use (e.g. a cancelled save). Returns the numbers released.</summary>
    public async Task<IReadOnlyList<string>> ReleaseAsync(Actor actor, IReadOnlyCollection<string> numbers, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.Primary.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<string>("""
            UPDATE number_reservations SET state = 'released', updated_at = now()
            WHERE lower(formatted) = ANY(@lowered) AND state = 'reserved' AND reserved_by = @userId
            RETURNING formatted
            """, new { lowered = numbers.Select(n => n.ToLowerInvariant()).ToArray(), userId = actor.UserId })).ToList();
    }

    /// <summary>Marks reservations past their TTL as expired. Returns how many expired.</summary>
    public static async Task<int> ExpireAsync(OpenholdDb db, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.Primary.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(
            "UPDATE number_reservations SET state = 'expired', updated_at = now() WHERE state = 'reserved' AND expires_at < now()");
    }

    private const string SchemeSelect = """
        SELECT s.id AS Id, s.name AS Name, s.template AS Template, s.allow_reuse AS AllowReuse, s.reservation_ttl AS ReservationTtl,
               s.enabled AS Enabled, s.fields::text AS FieldsJson, s.start_value AS StartAt, s.next_value AS NextValue, s.description AS Description,
               EXISTS (SELECT 1 FROM number_reservations r WHERE r.scheme_id = s.id) AS InUse
        FROM numbering_schemes s
        """;

    private sealed record SchemeRow(string Id, string Name, string Template, bool AllowReuse, TimeSpan ReservationTtl, bool Enabled,
        string FieldsJson, long StartAt, long NextValue, string? Description, bool InUse)
    {
        public IReadOnlyList<NumberingField> Fields { get; } = JsonSerializer.Deserialize<List<NumberingField>>(FieldsJson, Json) ?? [];

        public NumberingScheme ToScheme() => new(Id, Name, Template, AllowReuse, ReservationTtl, Enabled)
        {
            Fields = Fields, StartAt = StartAt, Description = Description, InUse = InUse,
        };
    }
}

/// <summary>Runs on the authority only: expires abandoned number reservations.</summary>
public sealed class ReservationExpiryService(VaultRegistry vaults, ILogger<ReservationExpiryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var vault in vaults.Vaults)
            {
                try
                {
                    var expired = await NumberingService.ExpireAsync(vault.Db, stoppingToken);
                    if (expired > 0)
                        logger.LogInformation("Expired {Count} unused number reservation(s) in vault {Vault}", expired, vault.Name);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Number reservation expiry failed in vault {Vault}; will retry", vault.Name);
                }
            }
        }
    }
}
