using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Openhold.Core.Configuration;
using Openhold.Core.Contracts;
using Openhold.Server.Data;
using Openhold.Server.Identity;

namespace Openhold.Server.Configuration;

/// <summary>
/// Reads and saves a vault's configuration (categories, lifecycles, revision schemes and groups). Every save
/// is a new version; older versions stay for the record. Only administrators can save.
/// </summary>
public sealed class ConfigurationService(OpenholdDb db, IOptions<OpenholdOptions> options)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The configuration in force, read from the local database (a replica at remote sites).</summary>
    public async Task<VersionedConfiguration> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await db.Local.OpenConnectionAsync(cancellationToken);
        return await LoadAsync(connection, null, options.Value);
    }

    /// <summary>Every saved configuration, newest first: who saved it, when, and why.</summary>
    public async Task<IReadOnlyList<ConfigurationVersion>> HistoryAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await db.Local.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<ConfigurationVersion>("""
            SELECT c.version AS Version, c.saved_at AS SavedAt, COALESCE(u.username, '(set up with the vault)') AS SavedBy, c.comment AS Comment
            FROM vault_config c LEFT JOIN users u ON u.id = c.saved_by
            ORDER BY c.version DESC
            LIMIT 500
            """)).ToList();
    }

    /// <summary>One saved configuration, e.g. to look at or go back to (saving it again makes it a new version).</summary>
    public async Task<VersionedConfiguration> GetVersionAsync(int version, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.Local.OpenConnectionAsync(cancellationToken);
        var document = await connection.ExecuteScalarAsync<string?>("SELECT document::text FROM vault_config WHERE version = @version", new { version })
            ?? throw OpenholdException.NotFound($"There is no configuration version {version}.");
        return new VersionedConfiguration(version, JsonSerializer.Deserialize<VaultConfiguration>(document, Json) ?? VaultConfiguration.Empty);
    }

    /// <summary>Everyone who has used this vault, for choosing group members.</summary>
    public async Task<IReadOnlyList<UserInfo>> UsersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await db.Local.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<UserInfo>("""
            SELECT username AS Username, display_name AS DisplayName, home_site AS Site FROM users ORDER BY lower(username)
            """)).ToList();
    }

    /// <summary>
    /// The newest saved configuration, with the server's bootstrap administrators (<c>Openhold:Administrators</c>)
    /// added to the administrators group, so a vault can never lock everyone out of its own settings.
    /// </summary>
    internal static async Task<VersionedConfiguration> LoadAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, OpenholdOptions options)
    {
        // Changes made under one configuration finish before a new one is saved (saves take this lock exclusively),
        // so a state change can't put files into a state the configuration has just removed.
        if (transaction is not null)
            await connection.ExecuteAsync("SELECT pg_advisory_xact_lock_shared(hashtext('openhold.vault_config'))", transaction: transaction);
        var row = await connection.QuerySingleOrDefaultAsync<(int Version, string Document)?>(
            "SELECT version, document::text FROM vault_config ORDER BY version DESC LIMIT 1", transaction: transaction);
        var configuration = row is { } found
            ? JsonSerializer.Deserialize<VaultConfiguration>(found.Document, Json) ?? VaultConfiguration.Empty
            : VaultConfiguration.Empty;
        return new VersionedConfiguration(row?.Version ?? 0, WithBootstrapAdministrators(configuration, options));
    }

    private static VaultConfiguration WithBootstrapAdministrators(VaultConfiguration configuration, OpenholdOptions options)
    {
        if (options.Administrators.Count == 0)
            return configuration;
        var bootstrap = options.Administrators.Select(ActorResolver.NormalizeUsername).OfType<string>();
        var groups = configuration.Groups.ToList();
        var index = groups.FindIndex(g => string.Equals(g.Id, VaultConfiguration.AdministratorsGroup, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            groups.Add(new GroupDefinition(VaultConfiguration.AdministratorsGroup, "Administrators", bootstrap.ToList()));
        else
            groups[index] = groups[index] with { Members = groups[index].Members.Union(bootstrap, StringComparer.OrdinalIgnoreCase).ToList() };
        return configuration with { Groups = groups };
    }

    /// <summary>
    /// Saves a new configuration. <paramref name="basedOnVersion"/> must be the version the administrator
    /// started from, so two people editing at once can't silently overwrite each other.
    /// </summary>
    public async Task<VersionedConfiguration> SaveAsync(Actor actor, SaveConfigurationRequest request, CancellationToken cancellationToken = default)
    {
        var configuration = Normalize(request.Configuration);
        var problems = configuration.Validate().ToList();

        await using var connection = await db.Primary.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // Serialises saves: the advisory lock is released when the transaction ends.
        await connection.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtext('openhold.vault_config'))", transaction: transaction);

        var current = await LoadAsync(connection, transaction, options.Value);
        if (!current.Configuration.HasPrivilege(actor.Username, Privileges.ManageConfiguration))
            throw new OpenholdException(StatusCodes.Status403Forbidden, "not_administrator", "Changing the vault's configuration needs the Manage Configuration privilege.");
        if (request.BasedOnVersion != current.Version)
        {
            throw OpenholdException.Conflict("configuration_changed",
                "Someone else changed the configuration while you were editing it. Reload it and make your changes again.");
        }

        problems.AddRange(await FindInUseProblemsAsync(connection, transaction, current.Configuration, configuration));
        if (!WithBootstrapAdministrators(configuration, options.Value).HasPrivilege(actor.Username, Privileges.ManageConfiguration))
            problems.Add("You'd no longer be allowed to change the configuration, so nobody might be able to change it again. Keep yourself in a group that can.");
        if (problems.Count > 0)
        {
            throw OpenholdException.BadRequest("invalid_configuration",
                "The configuration wasn't saved: " + string.Join(" ", problems), new { problems });
        }

        var version = current.Version + 1;
        await connection.ExecuteAsync("""
            INSERT INTO vault_config (version, saved_by, comment, document) VALUES (@version, @userId, @comment, @document::jsonb)
            """, new { version, userId = actor.UserId, comment = request.Comment?.Trim() ?? "", document = JsonSerializer.Serialize(configuration, Json) }, transaction);
        await connection.ExecuteAsync("""
            INSERT INTO audit_events (user_id, site_id, action, data)
            VALUES (@userId, @siteId, 'configuration_saved', jsonb_build_object('version', @version))
            """, new { userId = actor.UserId, siteId = actor.SiteId, version }, transaction);
        await AssignCategoriesAsync(connection, transaction, configuration);
        await transaction.CommitAsync(cancellationToken);
        return new VersionedConfiguration(version, WithBootstrapAdministrators(configuration, options.Value));
    }

    /// <summary>Group members are stored as normalised usernames ("DOMAIN\jsmith" → "jsmith").</summary>
    private static VaultConfiguration Normalize(VaultConfiguration configuration) => configuration with
    {
        Groups = configuration.Groups
            .Select(g => g with { Members = g.Members.Select(ActorResolver.NormalizeUsername).OfType<string>().Distinct().ToList() })
            .ToList(),
    };

    /// <summary>Categories files are in, and states files are in, can't disappear from under those files.</summary>
    private static async Task<IEnumerable<string>> FindInUseProblemsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, VaultConfiguration current, VaultConfiguration proposed)
    {
        var problems = new List<string>();
        var inUse = await connection.QueryAsync<(string Category, string? State, long Files)>("""
            SELECT f.category_id, v.state, count(*)
            FROM files f JOIN file_versions v ON v.file_id = f.id AND v.version = f.latest_version
            WHERE f.category_id IS NOT NULL
            GROUP BY f.category_id, v.state
            """, transaction: transaction);
        foreach (var group in inUse)
        {
            var category = proposed.Category(group.Category);
            if (category is null)
            {
                problems.Add($"{group.Files} file(s) are in category '{current.Category(group.Category)?.Name ?? group.Category}', so it can't be removed.");
                continue;
            }
            var state = current.StateOf(group.Category, group.State);
            var lifecycle = proposed.Lifecycle(category.LifecycleId);
            if (state is not null && lifecycle is not null && lifecycle.State(state.Name) is null)
                problems.Add($"{group.Files} file(s) in category '{category.Name}' are '{state.Name}', which lifecycle '{lifecycle.Name}' doesn't have. Keep that state, or move those files out of it first.");
            // Without a lifecycle a file can always be edited, so taking it away would quietly un-release files.
            if (state is not null && lifecycle is null && state != current.LifecycleFor(group.Category)!.Initial)
                problems.Add($"{group.Files} file(s) in category '{category.Name}' are '{state.Name}', so the category can't lose its lifecycle. Move those files back to '{current.LifecycleFor(group.Category)!.Initial.Name}' first.");
        }

        // A catalogue class items still use can't go: they'd be left without a class (and its attributes).
        var classesInUse = await connection.QueryAsync<(string ClassId, long Items, string Example)>("""
            SELECT class_id, count(*), min(number) FROM items WHERE class_id IS NOT NULL GROUP BY class_id
            """, transaction: transaction);
        foreach (var used in classesInUse.Where(u => proposed.ItemClass(u.ClassId) is null))
        {
            problems.Add($"{used.Items} catalogue item(s) are in class '{current.ItemClassPath(used.ClassId) ?? used.ClassId}' (e.g. {used.Example}), " +
                "so it can't be removed. Move them to another class in the Catalogue first.");
        }
        return problems;
    }

    /// <summary>
    /// Runs on the authority at start-up: creates the default configuration for a new vault and gives every file
    /// without a category the one its rules choose.
    /// </summary>
    public static async Task InitializeAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtext('openhold.vault_config'))", transaction: transaction);

        var configuration = await connection.ExecuteScalarAsync<string?>(
            "SELECT document::text FROM vault_config ORDER BY version DESC LIMIT 1", transaction: transaction) is { } document
            ? JsonSerializer.Deserialize<VaultConfiguration>(document, Json)!
            : null;
        if (configuration is null)
        {
            configuration = DefaultConfiguration.Create();
            await connection.ExecuteAsync("""
                INSERT INTO vault_config (version, comment, document) VALUES (1, 'Default configuration', @document::jsonb)
                """, new { document = JsonSerializer.Serialize(configuration, Json) }, transaction);
        }
        else if (configuration.ItemClasses is null)
        {
            // A vault set up before the catalogue existed starts with the default item classes, saved as a new version.
            configuration = configuration with { ItemClasses = DefaultConfiguration.ItemClasses };
            await connection.ExecuteAsync("""
                INSERT INTO vault_config (version, comment, document)
                SELECT COALESCE(max(version), 0) + 1, 'Default item classes for the catalogue', @document::jsonb FROM vault_config
                """, new { document = JsonSerializer.Serialize(configuration, Json) }, transaction);
        }

        await AssignCategoriesAsync(connection, transaction, configuration);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Files that have no category yet get the first automatic category whose rules match them.</summary>
    private static async Task AssignCategoriesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, VaultConfiguration configuration)
    {
        var uncategorised = await connection.QueryAsync<(Guid Id, string Folder, string Name)>("""
            SELECT f.id, fo.path, f.name FROM files f JOIN folders fo ON fo.id = f.folder_id WHERE f.category_id IS NULL
            """, transaction: transaction);
        var assignments = uncategorised
            .Select(f => (f.Id, Category: configuration.CategoryForNewFile(f.Folder, f.Name)?.Id))
            .Where(a => a.Category is not null)
            .ToList();
        if (assignments.Count == 0)
            return;
        await connection.ExecuteAsync("""
            UPDATE files f SET category_id = a.category
            FROM unnest(@ids, @categories) AS a(id, category)
            WHERE f.id = a.id
            """, new { ids = assignments.Select(a => a.Id).ToArray(), categories = assignments.Select(a => a.Category).ToArray() }, transaction);
    }
}
