using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Openhold.Core.Contracts;
using Openhold.Server.Data;
using Openhold.Server.Identity;

namespace Openhold.Server.Erp;

/// <summary>
/// The ERP connections of a vault: those administrators set up in Openhold (stored in the vault's database, secrets
/// encrypted) and any in the server's settings file. ERP connectors are optional: a vault with no connection sends
/// nothing anywhere.
/// </summary>
public sealed partial class ErpConnections(IOptions<OpenholdOptions> options, VaultRegistry vaults, SettingsSecrets secrets, IEnumerable<IErpConnector> connectors)
{
    /// <summary>The kinds of connection this server can make.</summary>
    public IReadOnlyList<ErpKindInfo> Kinds => connectors.Select(c => c.Describe()).OrderBy(k => k.Name).ToList();

    public IErpConnector? Connector(string kind) => connectors.FirstOrDefault(c => string.Equals(c.Kind, kind, StringComparison.OrdinalIgnoreCase));

    /// <summary>The vault's connections; with <paramref name="withSecrets"/>, their secrets decrypted into the settings.</summary>
    public async Task<IReadOnlyList<ErpConnectionOptions>> ForVaultAsync(VaultInstance vault, NpgsqlConnection connection, NpgsqlTransaction? transaction = null,
        bool enabledOnly = true, bool withSecrets = false)
    {
        var fromFile = options.Value.ErpConnections
            .Where(c => string.Equals(c.Vault ?? vaults.Vaults[0].Name, vault.Name, StringComparison.OrdinalIgnoreCase) && (c.Enabled || !enabledOnly));
        var rows = await connection.QueryAsync<Row>(
            "SELECT id AS Id, name AS Name, kind AS Kind, enabled AS Enabled, settings::text AS SettingsJson, secrets AS Secrets FROM erp_connections ORDER BY name",
            transaction: transaction);
        var fromOpenhold = rows.Where(r => r.Enabled || !enabledOnly).Select(r =>
        {
            var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(r.SettingsJson) ?? [];
            if (withSecrets && r.Secrets is not null)
            {
                foreach (var (name, value) in JsonSerializer.Deserialize<Dictionary<string, string>>(secrets.Unprotect(r.Secrets)) ?? [])
                    settings[name] = value;
            }
            return new ErpConnectionOptions { Id = r.Id, Name = r.Name, Kind = r.Kind, Enabled = r.Enabled, Vault = vault.Name, Settings = settings };
        });
        return [.. fromFile, .. fromOpenhold];
    }

    /// <summary>Every connection as administrators see it, secrets left out.</summary>
    public async Task<IReadOnlyList<ErpConnectionDetails>> DetailsAsync(VaultInstance vault, CancellationToken cancellationToken)
    {
        await using var connection = await vault.Db.Primary.OpenConnectionAsync(cancellationToken);
        var stored = (await connection.QueryAsync<Row>(
            "SELECT id AS Id, name AS Name, kind AS Kind, enabled AS Enabled, settings::text AS SettingsJson, secrets AS Secrets FROM erp_connections ORDER BY name")).ToList();
        var fileDetails = (await ForVaultAsync(vault, connection, enabledOnly: false))
            .Where(c => stored.All(s => s.Id != c.Id))
            .Select(c => Details(c.Id, c.Name, c.Kind, c.Enabled, true, c.Settings));
        var openholdDetails = stored.Select(r => Details(r.Id, r.Name, r.Kind, r.Enabled, false,
            JsonSerializer.Deserialize<Dictionary<string, string>>(r.SettingsJson) ?? [],
            r.Secrets is null ? [] : (JsonSerializer.Deserialize<Dictionary<string, string>>(secrets.Unprotect(r.Secrets)) ?? []).Keys));
        return [.. fileDetails, .. openholdDetails];
    }

    private ErpConnectionDetails Details(string id, string name, string kind, bool enabled, bool fromFile, IReadOnlyDictionary<string, string> settings, IEnumerable<string>? savedSecrets = null)
    {
        var secretNames = Connector(kind)?.Describe().Settings.Where(s => s.Secret).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        return new(id, name, kind, enabled, fromFile,
            settings.Where(s => !secretNames.Contains(s.Key)).ToDictionary(),
            [.. (savedSecrets ?? settings.Where(s => secretNames.Contains(s.Key) && s.Value.Length > 0).Select(s => s.Key)).Order()]);
    }

    /// <summary>Adds or changes a connection set up in Openhold. Secrets left out keep their saved values.</summary>
    public async Task<ErpConnectionDetails> SaveAsync(VaultInstance vault, Actor actor, string id, SaveErpConnectionRequest request, CancellationToken cancellationToken)
    {
        if (!IdPattern().IsMatch(id))
            throw OpenholdException.BadRequest("invalid_id", "A connection's id is lower-case letters, digits and dashes, e.g. company-a-simpro.");
        if (options.Value.ErpConnections.Any(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)))
            throw OpenholdException.Conflict("set_in_server_settings", $"The connection '{id}' is set in the server's settings file; change it there.");
        if (string.IsNullOrWhiteSpace(request.Name))
            throw OpenholdException.BadRequest("invalid_name", "Give the connection a name people will recognise, e.g. simPRO (Company A).");
        var kind = Connector(request.Kind)?.Describe()
            ?? throw OpenholdException.BadRequest("unknown_kind", $"There is no '{request.Kind}' connector. Choose one of: {string.Join(", ", Kinds.Select(k => k.Name))}.");

        // Only the fields the kind declares are kept, each in its place: settings in the clear, secrets encrypted.
        var settings = kind.Settings.Where(s => !s.Secret && request.Settings.TryGetValue(s.Name, out var v) && !string.IsNullOrWhiteSpace(v))
            .ToDictionary(s => s.Name, s => request.Settings[s.Name].Trim());
        if (kind.Settings.FirstOrDefault(s => s.Required && !s.Secret && !settings.ContainsKey(s.Name)) is { } missing)
            throw OpenholdException.BadRequest("missing_setting", $"{kind.Name} needs the {missing.Label}.");

        await using var connection = await vault.Db.Primary.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var existing = await connection.ExecuteScalarAsync<byte[]?>("SELECT secrets FROM erp_connections WHERE id = @id FOR UPDATE", new { id }, transaction);
        var saved = existing is null ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(secrets.Unprotect(existing)) ?? [];
        foreach (var secret in kind.Settings.Where(s => s.Secret))
        {
            if (request.Secrets?.TryGetValue(secret.Name, out var value) != true)
                continue;
            if (string.IsNullOrWhiteSpace(value))
                saved.Remove(secret.Name);
            else
                saved[secret.Name] = value.Trim();
        }
        await connection.ExecuteAsync("""
            INSERT INTO erp_connections (id, name, kind, enabled, settings, secrets, updated_by, updated_at)
            VALUES (@id, @name, @kind, @enabled, @settings::jsonb, @secrets, @userId, now())
            ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name, kind = EXCLUDED.kind, enabled = EXCLUDED.enabled, settings = EXCLUDED.settings,
                secrets = EXCLUDED.secrets, updated_by = EXCLUDED.updated_by, updated_at = now()
            """, new
        {
            id, name = request.Name.Trim(), kind = kind.Kind, enabled = request.Enabled, settings = JsonSerializer.Serialize(settings),
            secrets = saved.Count > 0 ? secrets.Protect(JsonSerializer.Serialize(saved)) : null, userId = actor.UserId,
        }, transaction);
        await connection.ExecuteAsync("""
            INSERT INTO audit_events (user_id, site_id, action, data)
            VALUES (@userId, @siteId, 'erp_connection_saved', jsonb_build_object('id', @id::text, 'kind', @kind::text, 'enabled', @enabled, 'secrets', @names::text[]))
            """, new { userId = actor.UserId, siteId = actor.SiteId, id, kind = kind.Kind, enabled = request.Enabled, names = saved.Keys.ToArray() }, transaction);
        await transaction.CommitAsync(cancellationToken);
        return Details(id, request.Name.Trim(), kind.Kind, request.Enabled, false, settings, saved.Keys);
    }

    /// <summary>Removes a connection set up in Openhold. What was sent stays in the ERP; Openhold just stops sending.</summary>
    public async Task DeleteAsync(VaultInstance vault, Actor actor, string id, CancellationToken cancellationToken)
    {
        await using var connection = await vault.Db.Primary.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (await connection.ExecuteAsync("DELETE FROM erp_connections WHERE id = @id", new { id }, transaction) == 0)
            throw OpenholdException.NotFound($"There is no connection '{id}' set up in Openhold.");
        await connection.ExecuteAsync("DELETE FROM item_erp_links WHERE connection_id = @id AND status <> 'done'", new { id }, transaction);
        await connection.ExecuteAsync("""
            INSERT INTO audit_events (user_id, site_id, action, data) VALUES (@userId, @siteId, 'erp_connection_removed', jsonb_build_object('id', @id::text))
            """, new { userId = actor.UserId, siteId = actor.SiteId, id }, transaction);
        await transaction.CommitAsync(cancellationToken);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$")]
    private static partial Regex IdPattern();

    private sealed record Row(string Id, string Name, string Kind, bool Enabled, string SettingsJson, byte[]? Secrets);
}
