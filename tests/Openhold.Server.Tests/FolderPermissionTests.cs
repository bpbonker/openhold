using System.Net;
using System.Net.Http.Json;
using Openhold.Client.Sdk;
using Openhold.Core.Configuration;
using Openhold.Core.Content;
using Openhold.Core.Contracts;
using Openhold.Server.Identity;

namespace Openhold.Server.Tests;

[Collection(VaultCollection.Name)]
public sealed class FolderPermissionTests(VaultFixture vault)
{
    private OpenholdClient Admin => vault.Client(VaultFixture.Administrator);

    private static string NewUsername() => $"user{Guid.NewGuid():N}"[..14];

    /// <summary>Adds a group and folder rules to the vault configuration; returns the group id.</summary>
    private async Task<string> RestrictAsync(IReadOnlyList<string> members, params (string Path, string Group, FolderAccess Access)[] rules)
    {
        var current = await Admin.GetConfigurationAsync();
        var groupId = $"team-{Guid.NewGuid():N}";
        await Admin.SaveConfigurationAsync(new SaveConfigurationRequest(current.Version, current.Configuration with
        {
            Groups = [.. current.Configuration.Groups, new GroupDefinition(groupId, "Project team", members)],
            FolderRules = [.. current.Configuration.FolderRules ?? [], .. rules.Select(r => new FolderRule(r.Path, r.Group == "team" ? groupId : r.Group, r.Access))],
        }));
        return groupId;
    }

    [Fact]
    public async Task A_hidden_folder_is_invisible_to_outsiders_everywhere_they_might_look()
    {
        var parent = VaultFixture.UniqueFolder("hidden");
        var secret = $"{parent}/Secret";
        var insider = NewUsername();
        var outsider = NewUsername();
        var ids = await VaultFixture.AddFilesAsync(Admin, secret, "SECRET-PLAN.dwg");
        await RestrictAsync([insider], (secret, "everyone", FolderAccess.None), (secret, "team", FolderAccess.Change));
        var other = vault.Client(outsider);

        var parentListing = await other.ListFolderAsync(parent);
        var listing = await Assert.ThrowsAsync<OpenholdApiException>(() => other.ListFolderAsync(secret));
        var file = await Assert.ThrowsAsync<OpenholdApiException>(() => other.GetFileAsync(ids[0]));
        var checkOut = await Assert.ThrowsAsync<OpenholdApiException>(() => other.CheckOutAsync(ids));
        var search = await other.SearchAsync("SECRET-PLAN");
        var resolved = await other.ResolvePathsAsync([$"{secret}/SECRET-PLAN.dwg"]);

        Assert.DoesNotContain(parentListing.Folders, f => f.Name == "Secret");
        Assert.Equal(HttpStatusCode.NotFound, listing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, file.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, checkOut.StatusCode);
        Assert.Empty(search.Hits);
        Assert.Null(resolved.Single().File);

        var team = vault.Client(insider);
        Assert.Single((await team.ListFolderAsync(secret)).Files);
        Assert.True((await team.CheckOutAsync(ids)).Succeeded);
        await team.UndoCheckOutAsync(ids);
    }

    [Fact]
    public async Task Read_access_gets_files_but_refuses_every_change()
    {
        var library = VaultFixture.UniqueFolder("library");
        var reader = NewUsername();
        var ids = await VaultFixture.AddFilesAsync(Admin, library, "M8-BOLT.ipt");
        await RestrictAsync([], (library, "everyone", FolderAccess.Read));
        var client = vault.Client(reader);

        using var copy = new MemoryStream();
        await client.DownloadAsync(ids[0], null, copy);
        var checkOut = await Assert.ThrowsAsync<OpenholdApiException>(() => client.CheckOutAsync(ids));
        var delete = await Assert.ThrowsAsync<OpenholdApiException>(() => client.DeleteFilesAsync(ids));
        var folder = await client.ListFolderAsync(library);
        var manifest = await client.UploadContentAsync(new MemoryStream(VaultFixture.Content(500, 7)));
        var add = await Assert.ThrowsAsync<OpenholdApiException>(() =>
            client.CheckInAsync(new CheckInRequest(Guid.NewGuid(), "add", [new CheckInFile(null, folder.Folder.Id, "NEW.ipt", manifest, null, null)])));
        var explained = await client.ExplainAccessAsync(library);

        Assert.True(copy.Length > 0);
        Assert.Equal((HttpStatusCode.Forbidden, "read_only_folder"), (checkOut.StatusCode, checkOut.Code));
        Assert.Equal("read_only_folder", delete.Code);
        Assert.Equal("read_only_folder", add.Code);
        Assert.Equal(FolderAccess.Read, explained.Access);
        Assert.Contains(library, explained.Reason);
    }

    [Fact]
    public async Task Files_cannot_be_moved_into_a_folder_the_person_may_not_change()
    {
        var work = VaultFixture.UniqueFolder("move-from");
        var released = VaultFixture.UniqueFolder("move-read-only");
        var secret = VaultFixture.UniqueFolder("move-hidden");
        var mover = NewUsername();
        var ids = await VaultFixture.AddFilesAsync(Admin, work, $"BRACKET-{Guid.NewGuid():N}.ipt");
        var readOnly = await Admin.EnsureFolderAsync(released);
        var hidden = await Admin.EnsureFolderAsync(secret);
        await RestrictAsync([], (released, "everyone", FolderAccess.Read), (secret, "everyone", FolderAccess.None));
        var client = vault.Client(mover);
        await client.CheckOutAsync(ids);
        var manifest = await client.UploadContentAsync(new MemoryStream(VaultFixture.Content(500, 8)));
        CheckInRequest MoveTo(Guid folderId) => new(Guid.NewGuid(), "move", [new CheckInFile(ids[0], null, null, manifest, null, null, NewFolderId: folderId)]);

        var intoReadOnly = await Assert.ThrowsAsync<OpenholdApiException>(() => client.CheckInAsync(MoveTo(readOnly.Id)));
        var intoHidden = await Assert.ThrowsAsync<OpenholdApiException>(() => client.CheckInAsync(MoveTo(hidden.Id)));

        Assert.Equal("read_only_folder", intoReadOnly.Code);
        Assert.Equal(HttpStatusCode.NotFound, intoHidden.StatusCode);
        Assert.Equal((await client.ListFolderAsync(work)).Folder.Id, (await client.GetFileAsync(ids[0])).FolderId);
        await client.UndoCheckOutAsync(ids);
    }

    [Fact]
    public async Task A_deeper_rule_opens_a_subfolder_of_a_hidden_folder()
    {
        var parent = VaultFixture.UniqueFolder("customers");
        var shared = $"{parent}/Shared";
        var partner = NewUsername();
        await VaultFixture.AddFilesAsync(Admin, $"{parent}/Internal", "COSTING.xlsx");
        await VaultFixture.AddFilesAsync(Admin, shared, "GA.dwg");
        await RestrictAsync([partner], (parent, "everyone", FolderAccess.None), (shared, "team", FolderAccess.Read));
        var client = vault.Client(partner);

        var top = await client.ListFolderAsync(parent);

        Assert.Equal(["Shared"], top.Folders.Select(f => f.Name));
        Assert.Empty(top.Files);
        Assert.Single((await client.ListFolderAsync(shared)).Files);
        Assert.Equal(FolderAccess.Read, (await Admin.ExplainAccessAsync(shared, partner)).Access);
        await Assert.ThrowsAsync<OpenholdApiException>(() => client.ExplainAccessAsync(shared, VaultFixture.Administrator));
    }

    [Fact]
    public async Task The_trash_only_shows_and_restores_files_from_folders_the_person_may_change()
    {
        var hidden = VaultFixture.UniqueFolder("trash-hidden");
        var readOnly = VaultFixture.UniqueFolder("trash-read");
        var open = VaultFixture.UniqueFolder("trash-open");
        var hiddenIds = await VaultFixture.AddFilesAsync(Admin, hidden, "SECRET.dwg");
        var readIds = await VaultFixture.AddFilesAsync(Admin, readOnly, "LIBRARY.ipt");
        var openIds = await VaultFixture.AddFilesAsync(Admin, open, "MINE.dwg");
        await Admin.DeleteFilesAsync([hiddenIds[0], readIds[0], openIds[0]]);
        await RestrictAsync([], (hidden, "everyone", FolderAccess.None), (readOnly, "everyone", FolderAccess.Read));
        var person = vault.Client(NewUsername());

        var trash = await person.ListTrashAsync();
        var restoreHidden = await Assert.ThrowsAsync<OpenholdApiException>(() => person.RestoreFromTrashAsync(hiddenIds));
        var restoreReadOnly = await Assert.ThrowsAsync<OpenholdApiException>(() => person.RestoreFromTrashAsync(readIds));
        var restored = await person.RestoreFromTrashAsync(openIds);
        var adminTrash = await Admin.ListTrashAsync();

        Assert.DoesNotContain(trash, t => t.FileId == hiddenIds[0] || t.FileId == readIds[0]);
        Assert.Contains(trash, t => t.FileId == openIds[0]);
        Assert.Equal(HttpStatusCode.NotFound, restoreHidden.StatusCode);
        Assert.Equal("read_only_folder", restoreReadOnly.Code);
        Assert.Equal(openIds, restored);
        Assert.Contains(adminTrash, t => t.FileId == hiddenIds[0]);
        Assert.Contains(adminTrash, t => t.FileId == readIds[0]);
    }

    [Fact]
    public async Task Only_empty_folders_can_be_removed_and_never_the_top_folder()
    {
        var parent = VaultFixture.UniqueFolder("remove");
        await Admin.EnsureFolderAsync($"{parent}/Empty");
        var ids = await VaultFixture.AddFilesAsync(Admin, $"{parent}/Used", "KEEP.ipt");

        var removed = await Admin.DeleteFolderAsync($"{parent}/Empty");
        var used = await Assert.ThrowsAsync<OpenholdApiException>(() => Admin.DeleteFolderAsync($"{parent}/Used"));
        await Admin.DeleteFilesAsync(ids);
        var trashed = await Assert.ThrowsAsync<OpenholdApiException>(() => Admin.DeleteFolderAsync($"{parent}/Used"));
        var top = await Assert.ThrowsAsync<OpenholdApiException>(() => Admin.DeleteFolderAsync("$"));

        Assert.Equal("Empty", removed.Name);
        Assert.DoesNotContain((await Admin.ListFolderAsync(parent)).Folders, f => f.Name == "Empty");
        Assert.Equal(("folder_not_empty", "folder_not_empty", "root_folder"), (used.Code, trashed.Code, top.Code));
        Assert.Contains("trash", trashed.Message);
    }

    [Fact]
    public async Task Chunks_are_only_served_for_files_the_person_may_read()
    {
        var secret = VaultFixture.UniqueFolder("chunks");
        var outsider = NewUsername();
        var ids = await VaultFixture.AddFilesAsync(Admin, secret, "PRICING.pdf");
        await RestrictAsync([], (secret, "everyone", FolderAccess.None));
        var hashes = await ChunkHashesAsync(VaultFixture.Administrator, ids[0]);

        using var stolen = await FetchChunksAsync(outsider, hashes);
        using var allowed = await FetchChunksAsync(VaultFixture.Administrator, hashes);

        Assert.Equal(HttpStatusCode.Forbidden, stolen.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public void Access_is_decided_by_the_deepest_rule_and_the_most_generous_group_there()
    {
        var configuration = new VaultConfiguration([], [], [],
            [new("administrators", "Administrators", ["admin"]), new("design", "Design", ["ann"]), new("viewers", "Viewers", ["ann", "bob"]),
             new("workers", "Workers", ["job"], [Privileges.RunJobs])],
            [new("$/Projects", "everyone", FolderAccess.Read), new("$/Projects/X", "viewers", FolderAccess.Read),
             new("$/Projects/X", "design", FolderAccess.Change), new("$/HR", "everyone", FolderAccess.None)]);

        Assert.Equal(FolderAccess.Change, configuration.AccessTo("ann", "$/Projects/X/Sub"));
        Assert.Equal(FolderAccess.Read, configuration.AccessTo("bob", "$/Projects/X"));
        Assert.Equal(FolderAccess.Read, configuration.AccessTo("carl", "$/Projects/Y"));
        Assert.Equal(FolderAccess.Change, configuration.AccessTo("carl", "$/Other"));
        Assert.Equal(FolderAccess.None, configuration.AccessTo("carl", "$/HR/Payroll"));
        Assert.Equal(FolderAccess.Read, configuration.AccessTo("job", "$/HR"));
        Assert.Equal(FolderAccess.Change, configuration.AccessTo("admin", "$/HR"));
        Assert.False(configuration.Unrestricted("carl"));
        Assert.Empty(configuration.Validate());
    }

    private async Task<IReadOnlyList<string>> ChunkHashesAsync(string username, Guid fileId)
    {
        using var http = vault.AnonymousHttpClient();
        http.DefaultRequestHeaders.Add(ActorResolver.UserHeader, username);
        var manifest = ManifestCodec.Decode(await http.GetByteArrayAsync($"api/files/{fileId}/manifest"));
        return manifest.DistinctHashes().Select(h => h.ToString()).ToList();
    }

    private async Task<HttpResponseMessage> FetchChunksAsync(string username, IReadOnlyList<string> hashes)
    {
        var http = vault.AnonymousHttpClient();
        http.DefaultRequestHeaders.Add(ActorResolver.UserHeader, username);
        return await http.PostAsJsonAsync("api/chunks/fetch", hashes);
    }
}
