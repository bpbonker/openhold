using Openhold.Client.Sdk;
using Openhold.Core.Contracts;

namespace Openhold.Server.Tests;

[Collection(VaultCollection.Name)]
public sealed class LockingTests(VaultFixture vault)
{
    [Fact]
    public async Task Batch_checkout_is_all_or_nothing()
    {
        var alice = vault.Client("alice");
        var bob = vault.Client("bob");
        var ids = await VaultFixture.AddFilesAsync(alice, VaultFixture.UniqueFolder("batch"), "A1.ipt", "A2.ipt", "A3.ipt");

        Assert.True((await bob.CheckOutAsync([ids[2]])).Succeeded);
        var result = await alice.CheckOutAsync(ids);

        Assert.False(result.Succeeded);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(ids[2], conflict.FileId);
        Assert.Equal("bob", conflict.Username);
        // Alice must hold nothing: the batch rolled back entirely.
        var holders = await alice.GetLocksAsync(ids);
        Assert.All(holders, h => Assert.Equal("bob", h.Username));
    }

    [Fact]
    public async Task Concurrent_overlapping_checkouts_never_double_lock()
    {
        var setup = vault.Client("setup");
        var names = Enumerable.Range(0, 12).Select(i => $"RACE-{Guid.NewGuid():N}-{i}.ipt").ToArray();
        var ids = await VaultFixture.AddFilesAsync(setup, VaultFixture.UniqueFolder("race"), names);

        // 24 users each grab an overlapping window of 4 files at the same moment.
        var attempts = Enumerable.Range(0, 24).Select(async u =>
        {
            var client = vault.Client($"racer{u}");
            var batch = Enumerable.Range(0, 4).Select(k => ids[(u + k * 3) % ids.Length]).ToArray();
            var result = await client.CheckOutAsync(batch);
            return (User: $"racer{u}", Batch: batch, result.Succeeded);
        }).ToList();
        var outcomes = await Task.WhenAll(attempts);

        var holders = (await setup.GetLocksAsync(ids)).ToDictionary(h => h.FileId, h => h.Username);
        var winners = outcomes.Where(o => o.Succeeded).ToList();
        Assert.NotEmpty(winners);
        foreach (var winner in winners)
            Assert.All(winner.Batch, id => Assert.Equal(winner.User, holders[id]));
        // Every held file belongs to exactly one winning batch.
        var claimed = winners.SelectMany(w => w.Batch).ToList();
        Assert.Equal(claimed.Count, claimed.Distinct().Count());
        Assert.Equal(holders.Count, claimed.Count);
    }

    [Fact]
    public async Task Checkout_is_reentrant_on_same_machine_but_not_another()
    {
        var pc1 = vault.Client("carol", "PC1");
        var pc2 = vault.Client("carol", "PC2");
        var ids = await VaultFixture.AddFilesAsync(pc1, VaultFixture.UniqueFolder("reentrant"), $"R-{Guid.NewGuid():N}.dwg");

        Assert.True((await pc1.CheckOutAsync(ids)).Succeeded);
        Assert.True((await pc1.CheckOutAsync(ids)).Succeeded);
        var other = await pc2.CheckOutAsync(ids);

        Assert.False(other.Succeeded);
        Assert.Equal("PC1", Assert.Single(other.Conflicts).Machine);
    }

    [Fact]
    public async Task Undo_checkout_releases_only_own_locks()
    {
        var dave = vault.Client("dave");
        var erin = vault.Client("erin");
        var ids = await VaultFixture.AddFilesAsync(dave, VaultFixture.UniqueFolder("undo"), $"U1-{Guid.NewGuid():N}.ipt", $"U2-{Guid.NewGuid():N}.ipt");
        await dave.CheckOutAsync([ids[0]]);
        await erin.CheckOutAsync([ids[1]]);

        var released = await dave.UndoCheckOutAsync(ids);

        Assert.Equal([ids[0]], released);
        Assert.Equal("erin", Assert.Single(await dave.GetLocksAsync(ids)).Username);
    }

    [Fact]
    public async Task Undo_checkout_only_works_from_the_pc_holding_the_copy()
    {
        var pc1 = vault.Client("gina", "PC1");
        var pc2 = vault.Client("gina", "PC2");
        var ids = await VaultFixture.AddFilesAsync(pc1, VaultFixture.UniqueFolder("undo-machine"), $"M-{Guid.NewGuid():N}.dwg");
        await pc1.CheckOutAsync(ids);

        var fromOtherPc = await pc2.UndoCheckOutAsync(ids);
        var fromHoldingPc = await pc1.UndoCheckOutAsync(ids);

        Assert.Empty(fromOtherPc);
        Assert.Equal(ids, fromHoldingPc);
    }

    [Fact]
    public async Task Backup_server_refuses_changes_but_serves_reads()
    {
        var office = vault.Client("hana");
        var backup = vault.BackupClient("hana");
        var folder = VaultFixture.UniqueFolder("backup");
        var ids = await VaultFixture.AddFilesAsync(office, folder, $"BK-{Guid.NewGuid():N}.dwg");

        var checkout = await Assert.ThrowsAsync<OpenholdApiException>(() => backup.CheckOutAsync(ids));
        var newFolder = await Assert.ThrowsAsync<OpenholdApiException>(() => backup.EnsureFolderAsync(folder + "/More"));
        var listing = await backup.ListFolderAsync(folder);
        var content = new MemoryStream();
        await backup.DownloadAsync(ids[0], null, content);

        Assert.Equal("backup_server", checkout.Code);
        Assert.Contains("AU Offsite is a backup server", checkout.Message);
        Assert.Equal("backup_server", newFolder.Code);
        Assert.Single(listing.Files);
        Assert.True(content.Length > 0);
        Assert.Empty(await office.GetLocksAsync(ids));
    }

    [Fact]
    public async Task Checkout_of_unknown_file_reports_not_found()
    {
        var result = await vault.Client("frank").CheckOutAsync([Guid.NewGuid()]);
        Assert.False(result.Succeeded);
        Assert.Single(result.NotFound);
    }
}
