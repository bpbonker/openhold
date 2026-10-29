using Openhold.Client.Sdk;
using Openhold.Core.Content;
using Openhold.Core.Contracts;

namespace Openhold.Server.Tests;

[Collection(VaultCollection.Name)]
public sealed class CheckInTests(VaultFixture vault)
{
    [Fact]
    public async Task Content_round_trips_and_history_records_versions()
    {
        var client = vault.Client("grace");
        var folder = await client.EnsureFolderAsync(VaultFixture.UniqueFolder("roundtrip"));
        var v1 = VaultFixture.Content(3 * 1024 * 1024 + 5, seed: 1);
        var name = $"W-{Guid.NewGuid():N}.ipt";

        var added = await client.CheckInAsync(new CheckInRequest(Guid.NewGuid(), "first",
            [new CheckInFile(null, folder.Id, name, await client.UploadContentAsync(new MemoryStream(v1)), new Dictionary<string, string> { ["Description"] = "Bracket" }, null)]));
        var fileId = added.Versions.Single().FileId;

        var v2 = VaultFixture.Content(3 * 1024 * 1024 + 9, seed: 2);
        Assert.True((await client.CheckOutAsync([fileId])).Succeeded);
        await client.CheckInAsync(new CheckInRequest(Guid.NewGuid(), "second",
            [new CheckInFile(fileId, null, null, await client.UploadContentAsync(new MemoryStream(v2)), null, null)]));

        var latest = new MemoryStream();
        await client.DownloadAsync(fileId, null, latest);
        var original = new MemoryStream();
        await client.DownloadAsync(fileId, 1, original);

        Assert.Equal(v2, latest.ToArray());
        Assert.Equal(v1, original.ToArray());
        var history = await client.GetHistoryAsync(fileId);
        Assert.Equal([2, 1], history.Select(h => h.Version));
        Assert.Equal("second", history[0].Comment);
        Assert.Equal("Bracket", history[1].Properties["Description"]);
        // Version 2 was checked in without properties: they carry forward instead of being erased.
        Assert.Equal("Bracket", history[0].Properties["Description"]);
        Assert.Equal("Bracket", (await client.ListFolderAsync(folder.Path)).Files.Single().Properties["Description"]);
        Assert.Empty(await client.GetLocksAsync([fileId]));
    }

    [Fact]
    public async Task A_missing_preview_can_be_filled_in_but_never_replaced()
    {
        var client = vault.Client("grace");
        var id = (await VaultFixture.AddFilesAsync(client, VaultFixture.UniqueFolder("preview"), $"P-{Guid.NewGuid():N}.ipt"))[0];
        byte[] first = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1];
        byte[] second = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 2];

        await client.AddMissingThumbnailAsync(id, 1, first);
        await client.AddMissingThumbnailAsync(id, 1, second);
        var notPng = await Assert.ThrowsAsync<OpenholdApiException>(() => client.AddMissingThumbnailAsync(id, 1, [1, 2, 3]));

        Assert.Equal(first, await client.GetThumbnailAsync(id));
        Assert.Equal("not_png", notPng.Code);
    }

    [Fact]
    public async Task Retried_changeset_commits_once()
    {
        var client = vault.Client("heidi");
        var ids = await VaultFixture.AddFilesAsync(client, VaultFixture.UniqueFolder("idempotent"), $"I-{Guid.NewGuid():N}.ipt");
        await client.CheckOutAsync(ids);
        var request = new CheckInRequest(Guid.NewGuid(), "retry me",
            [new CheckInFile(ids[0], null, null, await client.UploadContentAsync(new MemoryStream(VaultFixture.Content(5000, 7))), null, null)]);

        var first = await client.CheckInAsync(request);
        var retry = await client.CheckInAsync(request);

        Assert.False(first.AlreadyCommitted);
        Assert.True(retry.AlreadyCommitted);
        Assert.Equal(first.Versions.Single().Version, retry.Versions.Single().Version);
        Assert.Equal(2, (await client.GetFileAsync(ids[0])).LatestVersion);
    }

    [Fact]
    public async Task Checkin_requires_lock_held_on_this_machine()
    {
        var ivan = vault.Client("ivan", "PC1");
        var ids = await VaultFixture.AddFilesAsync(ivan, VaultFixture.UniqueFolder("nolock"), $"N-{Guid.NewGuid():N}.ipt");
        var manifest = await ivan.UploadContentAsync(new MemoryStream(VaultFixture.Content(100, 3)));

        var notLocked = await Assert.ThrowsAsync<OpenholdApiException>(() =>
            ivan.CheckInAsync(new CheckInRequest(Guid.NewGuid(), null, [new CheckInFile(ids[0], null, null, manifest, null, null)])));
        Assert.Equal("lock_not_held", notLocked.Code);

        await ivan.CheckOutAsync(ids);
        var otherMachine = await Assert.ThrowsAsync<OpenholdApiException>(() =>
            vault.Client("ivan", "PC2").CheckInAsync(new CheckInRequest(Guid.NewGuid(), null, [new CheckInFile(ids[0], null, null, manifest, null, null)])));
        Assert.Equal("lock_not_held", otherMachine.Code);
    }

    [Fact]
    public async Task Checkin_is_rejected_until_all_chunks_are_at_the_site()
    {
        var client = vault.Client("judy");
        var folder = await client.EnsureFolderAsync(VaultFixture.UniqueFolder("chunks"));
        var data = VaultFixture.Content(1000, 99);
        var manifest = ManifestCodec.Encode(new ChunkManifest(ContentHash.Compute(data), data.Length, [new ChunkRef(ContentHash.Compute(data), data.Length)]));

        var error = await Assert.ThrowsAsync<OpenholdApiException>(() =>
            client.CheckInAsync(new CheckInRequest(Guid.NewGuid(), null, [new CheckInFile(null, folder.Id, $"M-{Guid.NewGuid():N}.pdf", manifest, null, null)])));

        Assert.Equal("missing_chunks", error.Code);
    }

    [Fact]
    public async Task Cad_names_are_unique_across_vault_but_documents_only_per_folder()
    {
        var client = vault.Client("ken");
        var cadName = $"EL-{Guid.NewGuid():N}.dwg";
        await VaultFixture.AddFilesAsync(client, VaultFixture.UniqueFolder("unique-a"), cadName, "Datasheet.pdf");

        var error = await Assert.ThrowsAsync<OpenholdApiException>(() =>
            VaultFixture.AddFilesAsync(client, VaultFixture.UniqueFolder("unique-b"), cadName));
        Assert.Equal("name_conflict", error.Code);

        // A PDF with the same name in a different folder is fine.
        await VaultFixture.AddFilesAsync(client, VaultFixture.UniqueFolder("unique-c"), "Datasheet.pdf");
    }

    [Fact]
    public async Task References_within_a_changeset_build_uses_and_where_used()
    {
        var client = vault.Client("leo");
        var folder = await client.EnsureFolderAsync(VaultFixture.UniqueFolder("refs"));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        async Task<byte[]> Upload(int seed) => await client.UploadContentAsync(new MemoryStream(VaultFixture.Content(4000, seed)));

        var result = await client.CheckInAsync(new CheckInRequest(Guid.NewGuid(), "harness", [
            new CheckInFile(null, folder.Id, $"HARNESS-{suffix}.iam", await Upload(1), null,
                [new CheckInReference(null, 1, null, $"HARNESS-{suffix}/CONN-{suffix}.ipt"), new CheckInReference(null, 2, null, $"CABLE-{suffix}.ipt")]),
            new CheckInFile(null, folder.Id, $"CONN-{suffix}.ipt", await Upload(2), null, null),
            new CheckInFile(null, folder.Id, $"CABLE-{suffix}.ipt", await Upload(3), null, null),
        ]));
        var assembly = result.Versions.Single(v => v.Name.StartsWith("HARNESS")).FileId;
        var connector = result.Versions.Single(v => v.Name.StartsWith("CONN")).FileId;

        var uses = await client.GetUsesAsync(assembly);
        var whereUsed = await client.GetWhereUsedAsync(connector);

        Assert.Equal(2, uses.Count);
        Assert.Equal(assembly, Assert.Single(whereUsed).FileId);
    }

    [Fact]
    public async Task Rename_and_parent_check_in_commit_together_and_keep_history()
    {
        var client = vault.Client("nora");
        var folder = await client.EnsureFolderAsync(VaultFixture.UniqueFolder("rename"));
        var other = await client.EnsureFolderAsync(VaultFixture.UniqueFolder("rename-target"));
        var tag = Guid.NewGuid().ToString("N")[..8];
        async Task<byte[]> Upload(string text) => await client.UploadContentAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)));

        var added = await client.CheckInAsync(new CheckInRequest(Guid.NewGuid(), "add", [
            new CheckInFile(null, folder.Id, $"ASM-{tag}.iam", await Upload("asm"), null, [new CheckInReference(null, 1, null, $"OLD-{tag}.ipt")]),
            new CheckInFile(null, folder.Id, $"OLD-{tag}.ipt", await Upload("part"), null, null),
        ]));
        var assembly = added.Versions.Single(v => v.Name.StartsWith("ASM")).FileId;
        var part = added.Versions.Single(v => v.Name.StartsWith("OLD")).FileId;
        await client.CheckOutAsync([assembly, part]);

        // The part is renamed and moved; the assembly (references repaired by the client) is checked in alongside it.
        var renamed = await client.CheckInAsync(new CheckInRequest(Guid.NewGuid(), "rename", [
            new CheckInFile(part, null, null, await Upload("part"), null, null, NewName: $"NEW-{tag}.ipt", NewFolderId: other.Id),
            new CheckInFile(assembly, null, null, await Upload("asm repaired"), null, [new CheckInReference(part, null, null, $"NEW-{tag}.ipt")]),
        ]));

        var info = await client.GetFileAsync(part);
        Assert.Equal($"NEW-{tag}.ipt", info.Name);
        Assert.Equal(other.Id, info.FolderId);
        Assert.Equal(2, info.LatestVersion);
        Assert.Equal([2, 1], (await client.GetHistoryAsync(part)).Select(h => h.Version));
        Assert.Equal($"NEW-{tag}.ipt", Assert.Single(await client.GetUsesAsync(assembly)).Name);
        Assert.Equal(assembly, Assert.Single(await client.GetWhereUsedAsync(part)).FileId);
        Assert.Contains(renamed.Versions, v => v.FileId == part && v.Name == $"NEW-{tag}.ipt");
    }

    [Fact]
    public async Task Check_in_without_references_keeps_the_previous_ones()
    {
        var client = vault.Client("olly");
        var folder = await client.EnsureFolderAsync(VaultFixture.UniqueFolder("keep-refs"));
        var tag = Guid.NewGuid().ToString("N")[..8];
        async Task<byte[]> Upload(string text) => await client.UploadContentAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)));
        var added = await client.CheckInAsync(new CheckInRequest(Guid.NewGuid(), "add", [
            new CheckInFile(null, folder.Id, $"A-{tag}.iam", await Upload("a"), null, [new CheckInReference(null, 1, null, null)]),
            new CheckInFile(null, folder.Id, $"P-{tag}.ipt", await Upload("p"), null, null),
        ]));
        var assembly = added.Versions.Single(v => v.Name.StartsWith("A-")).FileId;
        await client.CheckOutAsync([assembly]);

        await client.CheckInAsync(new CheckInRequest(Guid.NewGuid(), "edited in Explorer", [new CheckInFile(assembly, null, null, await Upload("a2"), null, null)]));

        Assert.Equal($"P-{tag}.ipt", Assert.Single(await client.GetUsesAsync(assembly)).Name);
    }

    [Fact]
    public async Task Rename_to_a_name_already_in_use_is_refused()
    {
        var client = vault.Client("pia");
        var tag = Guid.NewGuid().ToString("N")[..8];
        var ids = await VaultFixture.AddFilesAsync(client, VaultFixture.UniqueFolder("rename-clash"), $"X-{tag}.dwg", $"Y-{tag}.dwg");
        await client.CheckOutAsync([ids[0]]);

        var error = await Assert.ThrowsAsync<OpenholdApiException>(async () => await client.CheckInAsync(new CheckInRequest(Guid.NewGuid(), "clash",
            [new CheckInFile(ids[0], null, null, await client.UploadContentAsync(new MemoryStream([1])), null, null, NewName: $"Y-{tag}.dwg")])));

        Assert.Equal("name_conflict", error.Code);
        Assert.Equal($"X-{tag}.dwg", (await client.GetFileAsync(ids[0])).Name);
    }

    [Fact]
    public async Task Ignored_scratch_files_cannot_be_added()
    {
        var error = await Assert.ThrowsAsync<OpenholdApiException>(() =>
            VaultFixture.AddFilesAsync(vault.Client("mia"), VaultFixture.UniqueFolder("ignored"), "drawing.dwl"));
        Assert.Equal("invalid_name", error.Code);
    }
}
