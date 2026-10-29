using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Openhold.Core.Contracts;
using Openhold.Server.Erp;

namespace Openhold.Server.Tests;

/// <summary>The simPRO connector against a fake simPRO: what it sends, and what it refuses to touch.</summary>
public sealed class SimproConnectorTests
{
    private const long OpenholdGroup = 900;

    /// <summary>A fake simPRO: one group called "Openhold Parts", one old item (part number OLD-1) and anything created.</summary>
    private sealed class FakeSimpro : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, JsonObject? Body, string? Auth)> Requests { get; } = [];
        public Dictionary<string, long> GroupOf { get; } = new() { ["501"] = 7 };   // the old item, in another group
        public Dictionary<string, string> PartNumberOf { get; } = new() { ["501"] = "OLD-1" };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken)) as JsonObject;
            var path = request.RequestUri!.AbsolutePath;
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            Requests.Add((request.Method, path, body, request.Headers.Authorization?.ToString()));
            if (path.EndsWith("/catalogGroups/"))
                return Json(query["Name"] == "Openhold Parts" ? $$"""[{"ID":{{OpenholdGroup}},"Name":"Openhold Parts","ParentGroup":null}]""" : "[]");
            if (path.EndsWith("/catalogs/") && request.Method == HttpMethod.Get)
                return Json("[" + string.Join(",", PartNumberOf.Where(p => string.Equals(p.Value, query["PartNo"], StringComparison.OrdinalIgnoreCase))
                    .Select(p => $$"""{"ID":{{p.Key}},"PartNo":"{{p.Value}}"}""")) + "]");
            if (path.EndsWith("/catalogs/") && request.Method == HttpMethod.Post)
            {
                var id = (600 + PartNumberOf.Count).ToString();
                PartNumberOf[id] = body!["PartNo"]!.GetValue<string>();
                GroupOf[id] = body["Group"]!.GetValue<long>();
                return Json($$"""{"ID":{{id}}}""", HttpStatusCode.Created);
            }
            var itemId = path.TrimEnd('/').Split('/')[^1];
            if (request.Method == HttpMethod.Get)
                return Json($$$"""{"ID":{{{itemId}}},"PartNo":"{{{PartNumberOf[itemId]}}}","Group":{"ID":{{{GroupOf[itemId]}}},"Name":"x"}}""");
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static readonly ErpConnectionOptions Connection = new()
    {
        Id = "simpro", Name = "simPRO", Kind = "simpro",
        Settings = new() { ["BaseUrl"] = "https://example.simprosuite.com", ["CompanyId"] = "0", ["Group"] = "Openhold Parts", ["ApiKey"] = "test-key" },
    };

    private static ItemInfo Item(string number, string state = ItemStates.Released) => new(
        Guid.NewGuid(), number, "Contactor 9 A", "each", ItemKinds.Bought, "contactors", "Electrical / Contactors", state,
        new Dictionary<string, string>(), null, [new ManufacturerPart("Schneider Electric", "LC1D09BD", true)], [], [],
        "admin", DateTime.UtcNow, DateTime.UtcNow, "admin", DateTime.UtcNow, 3);

    [Fact]
    public async Task A_new_item_is_created_in_openhold_s_group_with_an_api_key()
    {
        var simpro = new FakeSimpro();
        var id = await new SimproErpConnector(new Factory(simpro)).PushAsync(Connection, Item("P-100001-5"), null, CancellationToken.None);

        var post = simpro.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal(OpenholdGroup, simpro.GroupOf[id]);
        Assert.Equal(("P-100001-5", "Contactor 9 A", "Schneider Electric"),
            (post.Body!["PartNo"]!.GetValue<string>(), post.Body!["Name"]!.GetValue<string>(), post.Body!["Manufacturer"]!.GetValue<string>()));
        Assert.Contains("LC1D09BD", post.Body["Notes"]!.GetValue<string>());
        Assert.All(simpro.Requests, r => Assert.Equal("Bearer test-key", r.Auth));
        Assert.DoesNotContain(simpro.Requests, r => r.Path.Contains("oauth2"));
    }

    [Fact]
    public async Task An_old_simpro_item_with_the_same_part_number_is_left_alone()
    {
        var simpro = new FakeSimpro();

        var refused = await Assert.ThrowsAsync<ErpException>(() =>
            new SimproErpConnector(new Factory(simpro)).PushAsync(Connection, Item("old-1"), null, CancellationToken.None));

        Assert.Contains("left as it is", refused.Message);
        Assert.DoesNotContain(simpro.Requests, r => r.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task An_update_changes_only_name_manufacturer_and_archived_of_openhold_s_own_item()
    {
        var simpro = new FakeSimpro();
        var connector = new SimproErpConnector(new Factory(simpro));
        var id = await connector.PushAsync(Connection, Item("P-100002-8"), null, CancellationToken.None);

        await connector.PushAsync(Connection, Item("P-100002-8", ItemStates.Obsolete), id, CancellationToken.None);

        var patch = simpro.Requests.Single(r => r.Method == HttpMethod.Patch);
        Assert.Equal(["Archived", "Manufacturer", "Name"], patch.Body!.Select(p => p.Key).Order());
        Assert.True(patch.Body!["Archived"]!.GetValue<bool>());
    }

    [Fact]
    public async Task An_item_moved_out_of_openhold_s_group_is_no_longer_changed()
    {
        var simpro = new FakeSimpro();

        var refused = await Assert.ThrowsAsync<ErpException>(() =>
            new SimproErpConnector(new Factory(simpro)).PushAsync(Connection, Item("OLD-1"), "501", CancellationToken.None));

        Assert.Contains("no longer in the Openhold Parts group", refused.Message);
        Assert.DoesNotContain(simpro.Requests, r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task A_missing_group_is_reported_and_nothing_is_created()
    {
        var simpro = new FakeSimpro();
        var connection = new ErpConnectionOptions { Id = "s", Name = "s", Kind = "simpro", Settings = new(Connection.Settings) { ["Group"] = "Nope" } };

        var refused = await Assert.ThrowsAsync<ErpException>(() =>
            new SimproErpConnector(new Factory(simpro)).PushAsync(connection, Item("P-100003-1"), null, CancellationToken.None));

        Assert.Contains("no top-level catalogue group called \"Nope\"", refused.Message);
        Assert.DoesNotContain(simpro.Requests, r => r.Method == HttpMethod.Post);
    }
}
