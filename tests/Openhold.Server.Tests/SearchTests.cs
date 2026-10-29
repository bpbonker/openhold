using Openhold.Client.Sdk;
using Openhold.Core.Contracts;

namespace Openhold.Server.Tests;

[Collection(VaultCollection.Name)]
public sealed class SearchTests(VaultFixture vault)
{
    private readonly OpenholdClient _client = vault.Client("searcher");
    private readonly string _tag = Guid.NewGuid().ToString("N")[..10];

    private async Task<string> AddAsync(string folder, string name, string comment, Dictionary<string, string>? properties = null)
    {
        var target = await _client.EnsureFolderAsync(folder);
        var manifest = await _client.UploadContentAsync(new MemoryStream(VaultFixture.Content(500, name.GetHashCode())));
        await _client.CheckInAsync(new CheckInRequest(Guid.NewGuid(), comment, [new CheckInFile(null, target.Id, name, manifest, properties, null)]));
        return name;
    }

    [Fact]
    public async Task Finds_by_part_of_a_name()
    {
        var folder = VaultFixture.UniqueFolder("search-name");
        var name = await AddAsync(folder, $"PUMP-{_tag}-SKID.iam", "first");

        var results = await _client.SearchAsync($"{_tag}-sk");

        var hit = Assert.Single(results.Hits);
        Assert.Equal(name, hit.File.Name);
        Assert.Equal(folder, hit.FolderPath);
        Assert.Null(hit.Match); // the name says why it matched
    }

    [Fact]
    public async Task Finds_by_property_value_and_says_which_property_matched()
    {
        await AddAsync(VaultFixture.UniqueFolder("search-prop"), $"P-{_tag}.ipt", "first",
            new() { ["Description"] = $"AXLE ASSEMBLY {_tag}", ["Designer"] = "KAP" });

        var results = await _client.SearchAsync($"axle {_tag}");

        Assert.Equal($"Description: AXLE ASSEMBLY {_tag}", Assert.Single(results.Hits).Match);
    }

    [Fact]
    public async Task Property_filter_matches_only_that_property_with_wildcards()
    {
        var folder = VaultFixture.UniqueFolder("search-filter");
        await AddAsync(folder, $"A-{_tag}.ipt", "a", new() { ["Part Number"] = $"PRJ01-{_tag}-001" });
        await AddAsync(folder, $"B-{_tag}.ipt", "b", new() { ["Description"] = $"PRJ01-{_tag}-001 spare" });

        var results = await _client.SearchAsync($"\"Part Number\":PRJ01-{_tag}*");

        var hit = Assert.Single(results.Hits);
        Assert.Equal($"A-{_tag}.ipt", hit.File.Name);
        Assert.Equal($"Part Number: PRJ01-{_tag}-001", hit.Match);
    }

    [Fact]
    public async Task Finds_by_check_in_comment()
    {
        await AddAsync(VaultFixture.UniqueFolder("search-comment"), $"C-{_tag}.dwg", $"moved the incomer {_tag}");

        var results = await _client.SearchAsync($"incomer {_tag}");

        Assert.Equal($"Comment: moved the incomer {_tag}", Assert.Single(results.Hits).Match);
    }

    [Fact]
    public async Task Folder_scope_includes_subfolders_and_excludes_everything_else()
    {
        var root = VaultFixture.UniqueFolder("search-scope");
        await AddAsync($"{root}/Panel", $"IN-{_tag}.dwg", "x");
        await AddAsync(VaultFixture.UniqueFolder("search-elsewhere"), $"OUT-{_tag}.dwg", "x");

        var scoped = await _client.SearchAsync(_tag, root);
        var everywhere = await _client.SearchAsync(_tag);

        Assert.Equal([$"IN-{_tag}.dwg"], scoped.Hits.Select(h => h.File.Name));
        Assert.Equal(2, everywhere.Hits.Count);
    }

    [Fact]
    public async Task Every_term_must_match_and_like_wildcards_are_literal()
    {
        var folder = VaultFixture.UniqueFolder("search-and");
        await AddAsync(folder, $"VALVE-{_tag}.ipt", "x", new() { ["Description"] = "50% open" });
        await AddAsync(folder, $"VALVE-{_tag}-B.ipt", "x", new() { ["Description"] = "500 open" });

        var both = await _client.SearchAsync($"valve {_tag}");
        var percent = await _client.SearchAsync($"valve {_tag} 50%");

        Assert.Equal(2, both.Hits.Count);
        Assert.Equal([$"VALVE-{_tag}.ipt"], percent.Hits.Select(h => h.File.Name));
    }

    [Fact]
    public async Task Results_are_limited_and_say_so()
    {
        var folder = VaultFixture.UniqueFolder("search-limit");
        for (var i = 0; i < 4; i++)
            await AddAsync(folder, $"L{i}-{_tag}.pdf", "x");

        var results = await _client.SearchAsync(_tag, limit: 3);

        Assert.Equal(3, results.Hits.Count);
        Assert.True(results.Truncated);
    }

    [Fact]
    public async Task Empty_search_is_rejected_with_a_plain_message()
    {
        var error = await Assert.ThrowsAsync<OpenholdApiException>(() => _client.SearchAsync("   "));
        Assert.Equal("empty_search", error.Code);
    }
}
