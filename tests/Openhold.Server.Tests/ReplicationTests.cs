using Openhold.Client.Sdk;
using Openhold.Core.Contracts;

namespace Openhold.Server.Tests;

[Collection(VaultCollection.Name)]
public sealed class ReplicationTests(VaultFixture vault)
{
    private OpenholdClient Australia => vault.Client(VaultFixture.Administrator);

    [Fact]
    public async Task A_check_in_in_australia_shows_as_syncing_in_india_until_it_arrives()
    {
        var india = await vault.SiteClientAsync(VaultFixture.Administrator);
        var folder = VaultFixture.UniqueFolder("replication");
        var ids = await VaultFixture.AddFilesAsync(Australia, folder, "FRAME.ipt");

        var before = (await india.ListFolderAsync(folder)).Files.Single();
        var atHome = (await Australia.ListFolderAsync(folder)).Files.Single();
        var waiting = (await india.ListSitesAsync()).Single(s => s.Id == "IN");
        var arrived = await india.CatchUpSiteAsync();
        var after = (await india.ListFolderAsync(folder)).Files.Single();

        Assert.True(before.Syncing);
        Assert.False(atHome.Syncing);
        Assert.True(waiting.Missing >= 1 && waiting.IsThisSite);
        Assert.True(arrived >= 1);
        Assert.False(after.Syncing);
        Assert.Equal(ids[0], after.Id);
    }

    [Fact]
    public async Task A_get_in_india_works_before_replication_by_fetching_from_australia()
    {
        var india = await vault.SiteClientAsync(VaultFixture.Administrator);
        var folder = VaultFixture.UniqueFolder("pull-through");
        var ids = await VaultFixture.AddFilesAsync(Australia, folder, "BRACKET.dwg");

        using var copy = new MemoryStream();
        await india.DownloadAsync(ids[0], null, copy);

        Assert.Equal(VaultFixture.Content(2000, seed: "BRACKET.dwg".GetHashCode()), copy.ToArray());
    }

    [Fact]
    public async Task A_check_in_in_india_reaches_australia()
    {
        var india = await vault.SiteClientAsync(VaultFixture.Administrator);
        var folder = VaultFixture.UniqueFolder("from-india");
        var ids = await VaultFixture.AddFilesAsync(india, folder, "PANEL.dwg");

        var syncingInAustralia = (await Australia.ListFolderAsync(folder)).Files.Single().Syncing;
        await Australia.CatchUpSiteAsync();
        var file = (await Australia.ListFolderAsync(folder)).Files.Single();
        using var copy = new MemoryStream();
        await Australia.DownloadAsync(ids[0], null, copy);
        var history = await Australia.GetHistoryAsync(ids[0]);

        Assert.True(syncingInAustralia);
        Assert.False(file.Syncing);
        Assert.Equal(VaultFixture.Content(2000, seed: "PANEL.dwg".GetHashCode()), copy.ToArray());
        Assert.Equal("IN", history.Single().OriginSite);
    }

    [Fact]
    public async Task A_site_cut_off_from_the_authority_still_browses_and_says_plainly_why_changes_wait()
    {
        var folder = VaultFixture.UniqueFolder("cut-off");
        var ids = await VaultFixture.AddFilesAsync(Australia, folder, "GUARD.ipt");
        await Australia.ListFolderAsync(folder); // the user is known before the link goes down
        var cutOff = vault.CutOffSiteClient(VaultFixture.Administrator);

        var listing = await cutOff.ListFolderAsync(folder);
        var checkOut = await Assert.ThrowsAsync<OpenholdApiException>(() => cutOff.CheckOutAsync(ids));

        Assert.Single(listing.Files);
        Assert.Equal((System.Net.HttpStatusCode.ServiceUnavailable, "authority_unreachable"), (checkOut.StatusCode, checkOut.Code));
    }

    [Fact]
    public async Task Only_administrators_start_replication_by_hand()
    {
        await vault.SiteClientAsync(VaultFixture.Administrator);

        var refused = await Assert.ThrowsAsync<OpenholdApiException>(() => vault.Client($"user{Guid.NewGuid():N}"[..14]).CatchUpSiteAsync());

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, refused.StatusCode);
    }
}
