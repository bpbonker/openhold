using Openhold.Client.Sdk;
using Openhold.Core.Contracts;
using Openhold.Server.Erp;

namespace Openhold.Server.Tests;

[Collection(VaultCollection.Name)]
public sealed class ErpTests(VaultFixture vault)
{
    private OpenholdClient Admin => vault.Client(VaultFixture.Administrator);

    private static string NewMpn() => $"ERP-{Random.Shared.Next(100000, 999999)}";

    private async Task<ItemInfo> ReleasedItemAsync(string description)
    {
        var item = await Admin.CreateItemAsync(new SaveItemRequest(description, ItemKinds.Bought, ClassId: "terminals",
            ManufacturerParts: [new ManufacturerPart("Phoenix Contact", NewMpn())]));
        return await Admin.ChangeItemStateAsync(item.Id, new ItemStateRequest(ItemStates.Released, item.RowVersion));
    }

    [Fact]
    public async Task Released_items_reach_the_erp_and_drafts_do_not()
    {
        var draft = await Admin.CreateItemAsync(new SaveItemRequest("Draft terminal, \"quoted\"", ItemKinds.Made));
        var released = await ReleasedItemAsync("Terminal block, 4 mm²");

        await Admin.SendToErpNowAsync();
        var rows = CsvErpConnector.Read(await File.ReadAllTextAsync(vault.ErpCsvPath));
        var status = await Admin.GetItemErpStatusAsync(released.Id);

        Assert.Contains(rows, r => r[0] == released.Number && r[1] == "Terminal block, 4 mm²" && r[6] == "Phoenix Contact");
        Assert.DoesNotContain(rows, r => r[0] == draft.Number);
        Assert.Equal(("done", released.Number), (status.Single(s => s.ConnectionId == "test-csv").Status, status.Single(s => s.ConnectionId == "test-csv").ExternalId));
        Assert.Empty(await Admin.GetItemErpStatusAsync(draft.Id));
    }

    [Fact]
    public async Task A_change_to_a_released_item_is_sent_again()
    {
        var released = await ReleasedItemAsync("Terminal, grey");
        await Admin.SendToErpNowAsync();

        await Admin.UpdateItemAsync(released.Id, new SaveItemRequest("Terminal, blue", ItemKinds.Bought, ClassId: "terminals",
            ManufacturerParts: released.ManufacturerParts, RowVersion: released.RowVersion));
        Assert.Equal("pending", (await Admin.GetItemErpStatusAsync(released.Id)).Single(s => s.ConnectionId == "test-csv").Status);
        await Admin.SendToErpNowAsync();
        var rows = CsvErpConnector.Read(await File.ReadAllTextAsync(vault.ErpCsvPath));

        Assert.Equal("Terminal, blue", rows.Single(r => r[0] == released.Number)[1]);
    }

    [Fact]
    public async Task A_failing_connection_records_why_and_retries_without_blocking_the_others()
    {
        var released = await ReleasedItemAsync("Terminal, red");

        await Admin.SendToErpNowAsync();
        var status = await Admin.GetItemErpStatusAsync(released.Id);
        var connections = await Admin.ListErpConnectionsAsync();
        var test = await Assert.ThrowsAsync<OpenholdApiException>(() => Admin.TestErpConnectionAsync("broken-simpro"));

        var broken = status.Single(s => s.ConnectionId == "broken-simpro");
        Assert.Equal(("failed", 1), (broken.Status, broken.Attempts));
        Assert.Contains("https", broken.LastError);
        Assert.Equal("done", status.Single(s => s.ConnectionId == "test-csv").Status);
        Assert.True(connections.Single(c => c.Id == "broken-simpro").Failed >= 1);
        Assert.Equal("erp_test_failed", test.Code);
        await Admin.TestErpConnectionAsync("test-csv");
    }

    [Fact]
    public async Task An_erp_that_times_out_is_a_failed_send_not_a_server_crash()
    {
        var released = await ReleasedItemAsync("Terminal, black");

        await Admin.SendToErpNowAsync();
        var status = await Admin.GetItemErpStatusAsync(released.Id);

        var stalled = status.Single(s => s.ConnectionId == "stalled-erp");
        Assert.Equal(("failed", "The ERP didn't answer in time."), (stalled.Status, stalled.LastError));
        Assert.Equal("done", status.Single(s => s.ConnectionId == "test-csv").Status);
        await Admin.ListErpConnectionsAsync(); // still serving
    }

    [Fact]
    public async Task Administrators_set_up_a_connection_in_openhold_and_its_secrets_are_never_shown_again()
    {
        var id = $"csv-{Guid.NewGuid():N}"[..12];
        var path = Path.Combine(Path.GetDirectoryName(vault.ErpCsvPath)!, $"{id}.csv");

        var kinds = await Admin.ListErpKindsAsync();
        var saved = await Admin.SaveErpConnectionAsync(id, new SaveErpConnectionRequest("Second export", "csv", true, new Dictionary<string, string> { ["Path"] = path }));
        await Admin.TestErpConnectionAsync(id);
        var released = await ReleasedItemAsync("Terminal, green");
        await Admin.SendToErpNowAsync();
        var simpro = await Admin.SaveErpConnectionAsync($"{id}-s", new SaveErpConnectionRequest("simPRO trial", "simpro", false,
            new Dictionary<string, string> { ["BaseUrl"] = "https://example.simprosuite.com", ["CompanyId"] = "0", ["Group"] = "Openhold Parts", ["ApiKey"] = "not-kept-in-clear" },
            new Dictionary<string, string> { ["ApiKey"] = "secret-api-key" }));
        var setup = await Admin.ListErpSetupAsync();
        var fromFile = await Assert.ThrowsAsync<OpenholdApiException>(() =>
            Admin.SaveErpConnectionAsync("test-csv", new SaveErpConnectionRequest("Hijack", "csv", true, new Dictionary<string, string> { ["Path"] = path })));
        await using var db = new Npgsql.NpgsqlConnection(vault.ConnectionString);
        await db.OpenAsync();
        var stored = await new Npgsql.NpgsqlCommand($"SELECT settings::text || encode(secrets, 'escape') FROM erp_connections WHERE id = '{id}-s'", db).ExecuteScalarAsync();

        Assert.Contains(kinds, k => k.Kind == "simpro" && k.Settings.Any(s => s.Name == "ApiKey" && s.Secret));
        Assert.Equal("csv", saved.Kind);
        Assert.Contains(CsvErpConnector.Read(await File.ReadAllTextAsync(path)), r => r[0] == released.Number);
        Assert.Equal(["ApiKey"], simpro.SecretsSaved);
        Assert.DoesNotContain("ApiKey", simpro.Settings.Keys);
        Assert.DoesNotContain("secret-api-key", stored?.ToString());
        Assert.Contains(setup, c => c.Id == "test-csv" && c.FromServerSettings);
        Assert.Contains(setup, c => c.Id == $"{id}-s" && !c.Enabled && c.SecretsSaved.Contains("ApiKey"));
        Assert.Equal("set_in_server_settings", fromFile.Code);
        Assert.DoesNotContain(await Admin.GetItemErpStatusAsync(released.Id), s => s.ConnectionId == $"{id}-s"); // switched off: nothing queued
        await Admin.DeleteErpConnectionAsync(id);
        await Admin.DeleteErpConnectionAsync($"{id}-s");
    }

    [Fact]
    public async Task Only_administrators_and_part_librarians_see_connections()
    {
        var refused = await Assert.ThrowsAsync<OpenholdApiException>(() => vault.Client($"user{Guid.NewGuid():N}"[..14]).ListErpConnectionsAsync());

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, refused.StatusCode);
    }
}
