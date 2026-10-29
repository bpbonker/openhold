using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Openhold.Core.Contracts;

namespace Openhold.Server.Erp;

/// <summary>
/// Sends catalogue items to one kind of ERP. Openhold owns what a part is (number, description, manufacturer data);
/// the ERP owns sourcing (suppliers, costs, stock). Connectors are stateless; <see cref="ErpSyncService"/> handles
/// queueing, retries and remembering each item's id in the ERP. Add an ERP by implementing this and registering it.
/// </summary>
public interface IErpConnector
{
    /// <summary>Matches <see cref="ErpConnectionOptions.Kind"/>, e.g. "simpro".</summary>
    string Kind { get; }

    /// <summary>What administrators see when choosing the kind of a new connection, and the fields it needs.</summary>
    ErpKindInfo Describe();

    /// <summary>Creates or updates the item in the ERP. Returns the ERP's id for it.</summary>
    Task<string> PushAsync(ErpConnectionOptions connection, ItemInfo item, string? externalId, CancellationToken cancellationToken);

    /// <summary>Checks the settings and that the ERP answers; throws with a readable message if not.</summary>
    Task TestAsync(ErpConnectionOptions connection, CancellationToken cancellationToken);
}

/// <summary>Thrown for a connection's settings or an ERP's answer, with a message an administrator can act on.</summary>
public sealed class ErpException(string message, Exception? inner = null) : Exception(message, inner);

internal static class ErpSettings
{
    public static string Required(this ErpConnectionOptions connection, string name) =>
        connection.Settings.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new ErpException($"The ERP connection '{connection.Name}' needs the {name} setting.");
}

/// <summary>
/// The reference connector: keeps a CSV file of released items (one row per part number) that any ERP or
/// spreadsheet can import. Each write replaces the file atomically, so a reader never sees half a file.
/// Settings: Path.
/// </summary>
public sealed class CsvErpConnector : IErpConnector
{
    public static readonly string[] Columns =
        ["Part number", "Description", "Unit", "Kind", "Class", "State", "Manufacturer", "Manufacturer part number", "Other numbers", "Updated"];

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public string Kind => "csv";

    public ErpKindInfo Describe() => new(Kind, "CSV file", "Keeps a CSV file of released items, one row per part number, for any ERP or spreadsheet to import.",
        [new("Path", "File", Hint: @"Where to keep the file, as the server sees it, e.g. \\erp\imports\openhold-items.csv")]);

    public async Task<string> PushAsync(ErpConnectionOptions connection, ItemInfo item, string? externalId, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(connection.Required("Path"));
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var rows = File.Exists(path) ? Read(await File.ReadAllTextAsync(path, cancellationToken)) : [];
            var preferred = item.ManufacturerParts.FirstOrDefault();
            var row = new[]
            {
                item.Number, item.Description, item.Unit, item.Kind, item.ClassName ?? "", item.State,
                preferred?.Manufacturer ?? "", preferred?.PartNumber ?? "",
                string.Join("; ", item.CrossReferences.Select(r => $"{r.System}: {r.Number}")),
                item.UpdatedAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            };
            var index = rows.FindIndex(r => string.Equals(r[0], item.Number, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                rows[index] = row;
            else
                rows.Add(row);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".writing";
            var text = new StringBuilder().AppendLine(Line(Columns));
            foreach (var r in rows.OrderBy(r => r[0], StringComparer.OrdinalIgnoreCase))
                text.AppendLine(Line(r));
            await File.WriteAllTextAsync(temporary, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);
            File.Move(temporary, path, overwrite: true);
            return item.Number;
        }
        catch (IOException ex)
        {
            throw new ErpException($"Couldn't write {path}: {ex.Message}", ex);
        }
        finally
        {
            Gate.Release();
        }
    }

    public Task TestAsync(ErpConnectionOptions connection, CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(connection.Required("Path")))!;
        Directory.CreateDirectory(folder);
        var probe = Path.Combine(folder, $".openhold-test-{Guid.NewGuid():N}");
        File.WriteAllText(probe, "");
        File.Delete(probe);
        return Task.CompletedTask;
    }

    private static string Line(IEnumerable<string> values) =>
        string.Join(',', values.Select(v => v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{v.Replace("\"", "\"\"")}\"" : v));

    /// <summary>Reads rows written by this connector (RFC 4180 quoting), skipping the header.</summary>
    public static List<string[]> Read(string text)
    {
        var rows = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(field.ToString()); field.Clear(); }
            else if (c == '\n')
            {
                fields.Add(field.ToString().TrimEnd('\r'));
                field.Clear();
                rows.Add([.. fields]);
                fields.Clear();
            }
            else field.Append(c);
        }
        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            rows.Add([.. fields]);
        }
        return rows.Skip(1).Where(r => r.Length > 0 && r[0].Length > 0).ToList();
    }
}

/// <summary>
/// simPRO: released items become catalogue items in one catalogue group of their own (setting Group, e.g.
/// "Openhold Parts"), keyed on the part number (PartNo). Catalogue items Openhold didn't create are never changed: an
/// existing item with the same part number is reported, not overwritten, and before every update the item is checked
/// to still be in Openhold's group. Only the name, manufacturer and archived flag are updated, so notes, prices and
/// suppliers people keep in simPRO are left alone.
/// Settings: BaseUrl (e.g. https://yourbuild.simprosuite.com), CompanyId, Group, and either ApiKey (an API key made
/// in simPRO) or ClientId and ClientSecret (OAuth 2 client credentials). Keys belong in environment variables.
/// </summary>
public sealed class SimproErpConnector(IHttpClientFactory httpClients) : IErpConnector
{
    public string Kind => "simpro";

    public ErpKindInfo Describe() => new(Kind, "simPRO",
        "Creates released items as catalogue items in one catalogue group of their own, and keeps their name, manufacturer and archived flag up to date. " +
        "Catalogue items Openhold didn't create are never changed.",
        [
            new("BaseUrl", "simPRO address", Hint: "e.g. https://yourcompany.simprosuite.com"),
            new("CompanyId", "Company ID", Hint: "0 on a single-company build"),
            new("Group", "Catalogue group", Hint: "A top-level catalogue group for Openhold's items, e.g. Openhold Parts. Create it in simPRO first."),
            new("ApiKey", "API key", Secret: true, Required: false, Hint: "Made in simPRO (Setup › System › API keys). Or use a client ID and secret instead."),
            new("ClientId", "Client ID", Required: false, Hint: "Only for OAuth sign-in instead of an API key"),
            new("ClientSecret", "Client secret", Secret: true, Required: false),
        ]);

    public async Task<string> PushAsync(ErpConnectionOptions connection, ItemInfo item, string? externalId, CancellationToken cancellationToken)
    {
        using var http = await ClientAsync(connection, cancellationToken);
        var company = $"api/v1.0/companies/{Uri.EscapeDataString(connection.Required("CompanyId"))}/";
        var group = await GroupAsync(http, company, connection.Required("Group"), cancellationToken);
        var preferred = item.ManufacturerParts.FirstOrDefault();

        if (externalId is null)
        {
            if (await FindAsync(http, company, item.Number, cancellationToken) is { Count: > 0 } existing)
            {
                throw new ErpException($"simPRO already has {(existing.Count == 1 ? "an item" : $"{existing.Count} items")} with part number {item.Number} " +
                    "that Openhold didn't create, so it was left as it is. Give the Openhold item another number, or remove the old one in simPRO.");
            }
            var body = new JsonObject
            {
                ["PartNo"] = item.Number,
                ["Name"] = item.Description,
                ["Group"] = group,
                ["Notes"] = string.Join("\n", new[]
                {
                    preferred is null ? null : $"Manufacturer part: {preferred.Manufacturer} {preferred.PartNumber}",
                    item.ClassName is null ? null : $"Class: {item.ClassName}",
                    "Created by Openhold from the item master; the name and manufacturer are kept up to date from there.",
                }.Where(l => l is not null)),
                ["Archived"] = item.State == ItemStates.Obsolete,
            };
            if (preferred is not null)
                body["Manufacturer"] = preferred.Manufacturer;
            using var created = await http.PostAsJsonAsync($"{company}catalogs/", body, cancellationToken);
            await EnsureAsync(created, $"add {item.Number}", cancellationToken);
            var answer = await created.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            return answer.TryGetProperty("ID", out var id) ? id.ToString() : throw new ErpException($"simPRO didn't return an ID for {item.Number}.");
        }

        // Only ever change what Openhold made: the item must still be in Openhold's group.
        using (var current = await http.GetAsync($"{company}catalogs/{Uri.EscapeDataString(externalId)}?columns=ID,PartNo,Group", cancellationToken))
        {
            await EnsureAsync(current, $"read {item.Number}", cancellationToken);
            var found = await current.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            var inGroup = found.TryGetProperty("Group", out var g) && g.ValueKind == JsonValueKind.Object && g.TryGetProperty("ID", out var gid) && gid.GetInt64() == group;
            if (!inGroup)
                throw new ErpException($"simPRO item {externalId} ({item.Number}) is no longer in the {connection.Required("Group")} group, so Openhold left it alone.");
        }
        var changes = new JsonObject
        {
            ["Name"] = item.Description,
            ["Archived"] = item.State == ItemStates.Obsolete,
        };
        if (preferred is not null)
            changes["Manufacturer"] = preferred.Manufacturer;
        using var updated = await http.PatchAsJsonAsync($"{company}catalogs/{Uri.EscapeDataString(externalId)}", changes, cancellationToken);
        await EnsureAsync(updated, $"update {item.Number}", cancellationToken);
        return externalId;
    }

    /// <summary>Signs in, reads one catalogue item and finds Openhold's group: changes nothing.</summary>
    public async Task TestAsync(ErpConnectionOptions connection, CancellationToken cancellationToken)
    {
        using var http = await ClientAsync(connection, cancellationToken);
        var company = $"api/v1.0/companies/{Uri.EscapeDataString(connection.Required("CompanyId"))}/";
        using var response = await http.GetAsync($"{company}catalogs/?pageSize=1", cancellationToken);
        await EnsureAsync(response, "read the catalogue", cancellationToken);
        await GroupAsync(http, company, connection.Required("Group"), cancellationToken);
    }

    /// <summary>The catalogue items with exactly this part number (simPRO compares ignoring capitals).</summary>
    private static async Task<IReadOnlyList<string>> FindAsync(HttpClient http, string company, string partNumber, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"{company}catalogs/?PartNo={Uri.EscapeDataString(partNumber)}&columns=ID,PartNo&pageSize=250", cancellationToken);
        await EnsureAsync(response, $"look up {partNumber}", cancellationToken);
        var found = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return found.ValueKind == JsonValueKind.Array
            ? found.EnumerateArray().Where(e => e.TryGetProperty("PartNo", out var p) && string.Equals(p.GetString()?.Trim(), partNumber, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.GetProperty("ID").ToString()).ToList()
            : [];
    }

    /// <summary>The id of the top-level catalogue group Openhold's items go in. It must exist; Openhold doesn't create it.</summary>
    private static async Task<long> GroupAsync(HttpClient http, string company, string name, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"{company}catalogGroups/?Name={Uri.EscapeDataString(name)}&columns=ID,Name,ParentGroup&pageSize=250", cancellationToken);
        await EnsureAsync(response, "read the catalogue groups", cancellationToken);
        var groups = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var matches = groups.ValueKind == JsonValueKind.Array
            ? groups.EnumerateArray().Where(g => string.Equals(g.GetProperty("Name").GetString()?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)
                && (!g.TryGetProperty("ParentGroup", out var parent) || parent.ValueKind == JsonValueKind.Null)).ToList()
            : [];
        return matches switch
        {
            [var only] => only.GetProperty("ID").GetInt64(),
            [] => throw new ErpException($"simPRO has no top-level catalogue group called \"{name}\". Create it in simPRO (Setup › Materials › Catalogue groups), then try again."),
            _ => throw new ErpException($"simPRO has {matches.Count} top-level catalogue groups called \"{name}\"; rename all but one."),
        };
    }

    private async Task<HttpClient> ClientAsync(ErpConnectionOptions connection, CancellationToken cancellationToken)
    {
        var baseUrl = connection.Required("BaseUrl").TrimEnd('/') + "/";
        if (!baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new ErpException($"The ERP connection '{connection.Name}' must use https.");
        var http = httpClients.CreateClient("erp");
        http.BaseAddress = new Uri(baseUrl);
        // An API key made in simPRO is used as it is; otherwise sign in with OAuth 2 client credentials.
        if (connection.Settings.TryGetValue("ApiKey", out var apiKey) && !string.IsNullOrWhiteSpace(apiKey))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
            return http;
        }
        using var token = await http.PostAsync("oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = connection.Required("ClientId"),
            ["client_secret"] = connection.Required("ClientSecret"),
        }), cancellationToken);
        await EnsureAsync(token, "sign in", cancellationToken);
        var issued = await token.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            issued.TryGetProperty("access_token", out var accessToken) ? accessToken.GetString() : throw new ErpException("simPRO didn't return an access token."));
        return http;
    }

    private static async Task EnsureAsync(HttpResponseMessage response, string what, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;
        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new ErpException($"simPRO refused to {what}: {(int)response.StatusCode} {response.ReasonPhrase} {detail[..Math.Min(detail.Length, 300)]}".Trim());
    }
}
