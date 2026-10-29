using Openhold.Client.Sdk.Workspace;

namespace Openhold.Server.Tests;

[Collection(VaultCollection.Name)]
public sealed class WorkspaceTests(VaultFixture vault) : IDisposable
{
    private readonly List<string> _roots = [];
    private readonly string _project = $"Tests/ws-{Guid.NewGuid():N}";

    public void Dispose()
    {
        foreach (var root in _roots.Where(Directory.Exists))
        {
            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
    }

    private OpenholdWorkspace Workspace(string user, string machine = "PC1")
    {
        var root = Path.Combine(Path.GetTempPath(), "openhold-ws", Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return new OpenholdWorkspace(root, vault.Client(user, machine));
    }

    private string PathIn(OpenholdWorkspace ws, string name) => Path.Combine(ws.Root, _project.Replace('/', Path.DirectorySeparatorChar), name);

    private static string Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
            File.SetAttributes(path, FileAttributes.Normal);
        File.WriteAllText(path, content);
        return path;
    }

    private static bool IsReadOnly(string path) => File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly);

    [Fact]
    public async Task New_files_are_checked_in_read_only_and_reach_a_colleague()
    {
        var alice = Workspace("alice");
        var bob = Workspace("bob");
        var name = $"EL-{Guid.NewGuid():N}.dwg";
        var mine = Write(PathIn(alice, name), "panel layout v1");

        var checkIn = await alice.CheckInAsync([mine], "first issue");
        var get = await bob.GetLatestAsync([PathIn(bob, name)]);

        Assert.True(checkIn.Succeeded, checkIn.Summary);
        Assert.True(IsReadOnly(mine));
        Assert.Equal("Updated 1 file to the latest version.", get.Summary);
        Assert.Equal("panel layout v1", File.ReadAllText(PathIn(bob, name)));
        Assert.True(IsReadOnly(PathIn(bob, name)));
    }

    [Fact]
    public async Task The_status_bubble_shows_this_pcs_copy_whoever_has_the_file_checked_out()
    {
        var alice = Workspace("alice");
        var bob = Workspace("bob");
        var name = $"BUB-{Guid.NewGuid():N}.dwg";
        var mine = Write(PathIn(alice, name), "v1");
        var only = Write(PathIn(alice, $"NEW-{Guid.NewGuid():N}.dwg"), "not added yet");
        await alice.CheckInAsync([mine], "first");
        Assert.Equal(LocalCopy.None, (await bob.GetStatusAsync([PathIn(bob, name)]))[0].LocalCopy);

        await bob.GetLatestAsync([PathIn(bob, name)]);
        await alice.CheckOutAsync([mine]);
        Write(mine, "v2");
        await alice.CheckInAsync([mine], "second", new CheckInOptions { KeepCheckedOut = true });

        var bobs = (await bob.GetStatusAsync([PathIn(bob, name)]))[0];
        var alices = await alice.GetStatusAsync([mine, only]);
        Assert.Equal((FileState.CheckedOutToSomeoneElse, LocalCopy.Outdated), (bobs.State, bobs.LocalCopy)); // red bubble, red X
        Assert.Equal((FileState.CheckedOutToMe, LocalCopy.UpToDate), (alices[0].State, alices[0].LocalCopy)); // green bubble, blue X
        Assert.Equal(LocalCopy.OnlyHere, alices[1].LocalCopy);
    }

    [Fact]
    public async Task Releasing_keeps_copies_up_to_date_and_a_released_file_says_how_to_change_it()
    {
        var alice = Workspace("alice");
        var bob = Workspace("bob");
        var name = $"REL-{Guid.NewGuid():N}.dwg";
        var mine = Write(PathIn(alice, name), "general arrangement");
        await alice.CheckInAsync([mine], "first");
        await bob.GetLatestAsync([PathIn(bob, name)]);

        var released = await alice.ChangeStateAsync([mine], "Released", "issued for construction");
        var checkOut = await bob.CheckOutAsync([PathIn(bob, name)]);
        var refusedAgain = await alice.ChangeStateAsync([mine], "Released", null);

        Assert.True(released.Succeeded, released.Summary);
        // Same content, new version number: nobody has anything to download.
        Assert.All(await bob.GetStatusAsync([PathIn(bob, name)]), s => Assert.Equal(FileState.UpToDate, s.State));
        Assert.Equal("Everything is up to date.", (await bob.GetLatestAsync([PathIn(bob, name)])).Summary);
        Assert.False(checkOut.Succeeded);
        Assert.Contains("Reopen for a new revision", checkOut.Summary);
        Assert.False(refusedAgain.Succeeded);
        Assert.Contains("already Released", Assert.Single(refusedAgain.Files).Message);
        Assert.Equal(mine, refusedAgain.Files[0].LocalPath);
    }

    [Fact]
    public async Task Checkout_brings_a_stale_copy_up_to_date_and_names_the_holder_on_conflict()
    {
        var alice = Workspace("alice");
        var bob = Workspace("bob");
        var name = $"EL-{Guid.NewGuid():N}.dwg";
        await alice.CheckInAsync([Write(PathIn(alice, name), "v1")], "v1");
        await bob.GetLatestAsync([PathIn(bob, name)]);

        Assert.True((await alice.CheckOutAsync([PathIn(alice, name)])).Succeeded);
        Write(PathIn(alice, name), "v2 from alice");
        Assert.True((await alice.CheckInAsync([PathIn(alice, name)], "v2")).Succeeded);

        var bobOut = await bob.CheckOutAsync([PathIn(bob, name)]);
        var aliceBlocked = await alice.CheckOutAsync([PathIn(alice, name)]);

        Assert.True(bobOut.Succeeded, bobOut.Summary);
        Assert.Equal("v2 from alice", File.ReadAllText(PathIn(bob, name)));
        Assert.False(IsReadOnly(PathIn(bob, name)));
        Assert.False(aliceBlocked.Succeeded);
        Assert.Contains("checked out to bob on PC1 (AU)", Assert.Single(aliceBlocked.Files).Message);
    }

    [Fact]
    public async Task Undo_checkout_restores_the_vault_copy_and_keeps_changes_in_a_backup()
    {
        var alice = Workspace("alice");
        var name = $"EL-{Guid.NewGuid():N}.dwg";
        var path = Write(PathIn(alice, name), "released");
        await alice.CheckInAsync([path], "v1");
        await alice.CheckOutAsync([path]);
        Write(path, "experiment I want to abandon");

        var undo = await alice.UndoCheckOutAsync([path]);

        Assert.True(undo.Succeeded);
        Assert.Equal("released", File.ReadAllText(path));
        Assert.True(IsReadOnly(path));
        var backup = Directory.GetFiles(Path.Combine(alice.Root, ".openhold", "backup"), name, SearchOption.AllDirectories).Single();
        Assert.Equal("experiment I want to abandon", File.ReadAllText(backup));
        Assert.Contains(backup, Assert.Single(undo.Files).Message);
    }

    [Fact]
    public async Task Checking_out_one_drawing_checks_out_its_whole_acade_project()
    {
        var alice = Workspace("alice");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var wdp = Write(PathIn(alice, $"PUMP-{suffix}.wdp"), $"*[1]Pump station\n===Cover\nPS-{suffix}-01.dwg\n===Motors\nPS-{suffix}-02.dwg\n");
        var titles = Write(PathIn(alice, $"PUMP-{suffix}.wdt"), "title mapping");
        var sheet1 = Write(PathIn(alice, $"PS-{suffix}-01.dwg"), "sheet 1");
        var sheet2 = Write(PathIn(alice, $"PS-{suffix}-02.dwg"), "sheet 2");
        var added = await alice.CheckInAsync([wdp], "new project");
        Assert.True(added.Succeeded, added.Summary);
        Assert.Equal(4, added.Files.Count);

        var checkout = await alice.CheckOutAsync([sheet2]);
        var statuses = await alice.GetStatusAsync([wdp, titles, sheet1, sheet2]);

        Assert.True(checkout.Succeeded, checkout.Summary);
        Assert.Equal("Checked out 4 files.", checkout.Summary);
        Assert.All(statuses, s => Assert.Equal(FileState.CheckedOutToMe, s.State));
    }

    [Fact]
    public async Task Checking_in_one_drawing_keeps_the_references_of_the_project_s_other_drawings()
    {
        var alice = Workspace("alice");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var wdp = Write(PathIn(alice, $"PANEL-{suffix}.wdp"), $"*[1]Panel\nPN-{suffix}-01.dwg\nPN-{suffix}-02.dwg\n");
        var sheet1 = Write(PathIn(alice, $"PN-{suffix}-01.dwg"), "sheet 1");
        var sheet2 = Write(PathIn(alice, $"PN-{suffix}-02.dwg"), "sheet 2");
        var border = Write(PathIn(alice, $"BORDER-{suffix}.dwg"), "border xref");
        var added = await alice.CheckInAsync([wdp, border], "new project", new CheckInOptions { References = p => p == sheet1 || p == sheet2 ? [border] : [] });
        Assert.True(added.Succeeded, added.Summary);
        await alice.CheckOutAsync([sheet2]);
        Write(sheet1, "sheet 1, wire numbers updated");
        Write(sheet2, "sheet 2, edited");

        // As OPENHOLDCHECKIN does: only the open drawing's references were read.
        var checkIn = await alice.CheckInAsync([sheet2], "edit", new CheckInOptions { References = p => p == sheet2 ? [] : null });

        Assert.True(checkIn.Succeeded, checkIn.Summary);
        var client = vault.Client("alice");
        var ids = (await client.ResolvePathsAsync([$"$/{_project}/PN-{suffix}-01.dwg", $"$/{_project}/PN-{suffix}-02.dwg"])).Select(r => r.File!.Id).ToList();
        Assert.Equal(2, (await client.GetFileAsync(ids[0])).LatestVersion);
        Assert.Equal($"BORDER-{suffix}.dwg", Assert.Single(await client.GetUsesAsync(ids[0])).Name);
        Assert.Empty(await client.GetUsesAsync(ids[1]));
    }

    [Fact]
    public async Task An_import_can_check_in_a_project_file_without_its_drawings()
    {
        var alice = Workspace("alice");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var wdp = Write(PathIn(alice, $"IMPORT-{suffix}.wdp"), $"*[1]Import\nIM-{suffix}-01.dwg\n");
        var sheet = Write(PathIn(alice, $"IM-{suffix}-01.dwg"), "sheet");

        var first = await alice.CheckInAsync([wdp], "import", new CheckInOptions { WholeProjectSets = false });
        var second = await alice.CheckInAsync([sheet], "import", new CheckInOptions { WholeProjectSets = false });

        Assert.True(first.Succeeded, first.Summary);
        Assert.Single(first.Files);
        Assert.True(second.Succeeded, second.Summary);
    }

    [Fact]
    public async Task Check_in_without_checkout_is_refused_with_a_clear_next_step()
    {
        var alice = Workspace("alice");
        var name = $"EL-{Guid.NewGuid():N}.dwg";
        var path = Write(PathIn(alice, name), "v1");
        await alice.CheckInAsync([path], "v1");
        Write(path, "edited without checking out");

        var result = await alice.CheckInAsync([path], "sneaky");

        Assert.False(result.Succeeded);
        Assert.Equal("Nothing was checked in. Check these files out first, then check them in.", result.Summary);
        Assert.Contains("isn't checked out to you", Assert.Single(result.Files).Message);
    }

    [Fact]
    public async Task Status_reports_outdated_and_locally_modified_files()
    {
        var alice = Workspace("alice");
        var bob = Workspace("bob");
        var outdated = $"EL-{Guid.NewGuid():N}.dwg";
        var modified = $"EL-{Guid.NewGuid():N}.dwg";
        await alice.CheckInAsync([Write(PathIn(alice, outdated), "a1"), Write(PathIn(alice, modified), "b1")], "v1");
        await bob.GetLatestAsync([PathIn(bob, outdated), PathIn(bob, modified)]);
        await alice.CheckOutAsync([PathIn(alice, outdated)]);
        await alice.CheckInAsync([Write(PathIn(alice, outdated), "a2")], "v2");
        Write(PathIn(bob, modified), "bob bypassed read-only");

        var statuses = (await bob.GetStatusAsync([PathIn(bob, outdated), PathIn(bob, modified)])).ToDictionary(s => Path.GetFileName(s.LocalPath));

        Assert.Equal(FileState.Outdated, statuses[outdated].State);
        Assert.Equal(FileState.ModifiedLocally, statuses[modified].State);
    }

    [Fact]
    public async Task Checking_in_without_changes_releases_instead_of_adding_a_version()
    {
        var alice = Workspace("alice");
        var name = $"EL-{Guid.NewGuid():N}.dwg";
        var unchanged = Write(PathIn(alice, name), "same content");
        var edited = Write(PathIn(alice, "B-" + name), "before");
        await alice.CheckInAsync([unchanged, edited], "v1");
        await alice.CheckOutAsync([unchanged, edited]);
        Write(edited, "after");

        var mixed = await alice.CheckInAsync([unchanged, edited], "only one changed");
        await alice.CheckOutAsync([unchanged]);
        var nothing = await alice.CheckInAsync([unchanged], "no edits");

        Assert.True(mixed.Succeeded, mixed.Summary);
        Assert.Equal("Checked in 1 file.", mixed.Summary);
        Assert.Contains(mixed.Files, f => f.Message == $"{name} hasn't changed since version 1, so no new version was made.");
        Assert.Equal("Nothing had changed, so no new version was made. It has been released.", nothing.Summary);
        var statuses = await alice.GetStatusAsync([unchanged, edited]);
        Assert.All(statuses, s => Assert.Equal(FileState.UpToDate, s.State));
        Assert.Equal(2, (await vault.Client("alice").GetFileAsync(statuses[1].File!.Id)).LatestVersion);
        Assert.Equal(1, (await vault.Client("alice").GetFileAsync(statuses[0].File!.Id)).LatestVersion);
    }

    [Fact]
    public async Task Checking_out_again_never_replaces_edits_made_since_the_first_check_out()
    {
        var alice = Workspace("alice");
        var path = Write(PathIn(alice, $"TOP-{Guid.NewGuid():N}.iam"), "v1");
        await alice.CheckInAsync([path], "v1");
        await alice.CheckOutAsync([path]);
        File.WriteAllText(path, "work in progress");

        var again = await alice.CheckOutAsync([path]); // e.g. pulled in by a rename of a part it uses

        Assert.True(again.Succeeded, again.Summary);
        Assert.Empty(again.UpdatedFiles);
        Assert.Equal("work in progress", File.ReadAllText(path));
    }

    /// <summary>Lets one check-in reach the server and commit, then loses the reply, as a dropped connection would.</summary>
    private sealed class DropCheckInReply : DelegatingHandler
    {
        public bool Armed { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (Armed && request.RequestUri!.AbsolutePath.EndsWith("/api/checkin", StringComparison.Ordinal))
            {
                Armed = false;
                throw new HttpRequestException("connection dropped");
            }
            return response;
        }
    }

    [Fact]
    public async Task A_check_in_whose_reply_was_lost_completes_once_when_retried()
    {
        var network = new DropCheckInReply();
        var alice = new OpenholdWorkspace(Workspace("alice").Root, vault.Client("alice", network));
        var path = Write(PathIn(alice, $"DUCT-{Guid.NewGuid():N}.pdf"), "v1");
        await alice.CheckInAsync([path], "v1");
        await alice.CheckOutAsync([path]);
        File.WriteAllText(path, "v2");

        network.Armed = true;
        var lost = await alice.CheckInAsync([path], "v2");
        var retried = await alice.CheckInAsync([path], "v2");

        Assert.True(lost.Retryable);
        Assert.True(retried.Succeeded, retried.Summary);
        var file = (await alice.GetStatusAsync([path]))[0].File!;
        Assert.Equal(2, file.LatestVersion); // committed exactly once
    }

    [Fact]
    public async Task Edits_made_after_a_lost_reply_are_never_reported_as_checked_in_when_they_are_not()
    {
        var network = new DropCheckInReply();
        var alice = new OpenholdWorkspace(Workspace("alice").Root, vault.Client("alice", network));
        var path = Write(PathIn(alice, $"DUCT-{Guid.NewGuid():N}.pdf"), "v1");
        await alice.CheckInAsync([path], "v1");
        await alice.CheckOutAsync([path]);
        File.WriteAllText(path, "first edit");
        network.Armed = true;
        await alice.CheckInAsync([path], "first"); // committed, but the reply was lost

        File.WriteAllText(path, "second edit");
        var result = await alice.CheckInAsync([path], "second");

        var file = (await alice.GetStatusAsync([path]))[0].File!;
        using var vaultCopy = new MemoryStream();
        await vault.Client("alice").DownloadAsync(file.Id, file.LatestVersion, vaultCopy);
        if (result.Succeeded)
            Assert.Equal("second edit", System.Text.Encoding.UTF8.GetString(vaultCopy.ToArray()));
        else
            Assert.Equal("second edit", File.ReadAllText(path)); // refused, and the edit is still on this PC
    }

    [Fact]
    public async Task Checkout_reports_which_files_were_brought_up_to_date()
    {
        var alice = Workspace("alice");
        var bob = Workspace("bob");
        var name = $"EL-{Guid.NewGuid():N}.dwg";
        await alice.CheckInAsync([Write(PathIn(alice, name), "v1")], "v1");
        await bob.GetLatestAsync([PathIn(bob, name)]);
        await alice.CheckOutAsync([PathIn(alice, name)]);
        await alice.CheckInAsync([Write(PathIn(alice, name), "v2")], "v2");

        var current = await alice.CheckOutAsync([PathIn(alice, name)]);
        await alice.UndoCheckOutAsync([PathIn(alice, name)]);
        var stale = await bob.CheckOutAsync([PathIn(bob, name)]);

        Assert.Empty(current.UpdatedFiles);
        Assert.Equal([PathIn(bob, name)], stale.UpdatedFiles);
    }

    [Fact]
    public async Task Two_programs_on_one_pc_never_lose_each_others_workspace_updates()
    {
        // AutoCAD's plugin and Openhold Explorer each hold their own OpenholdWorkspace on the same folder.
        var root = Path.Combine(Path.GetTempPath(), "openhold-ws", Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        var autocad = new OpenholdWorkspace(root, vault.Client("alice"));
        var explorer = new OpenholdWorkspace(root, vault.Client("alice"));
        var first = Write(PathIn(autocad, $"A-{Guid.NewGuid():N}.dwg"), "from AutoCAD");
        var second = Write(PathIn(explorer, $"B-{Guid.NewGuid():N}.pdf"), "from Explorer");

        await autocad.CheckInAsync([first], "a");
        await explorer.CheckInAsync([second], "b");
        var seenByAutocad = await autocad.GetStatusAsync([first, second]);

        Assert.All(seenByAutocad, s => Assert.Equal(FileState.UpToDate, s.State));
    }

    [Fact]
    public async Task Folder_view_combines_vault_files_with_files_only_on_this_pc()
    {
        var alice = Workspace("alice");
        var inVault = Write(PathIn(alice, $"V-{Guid.NewGuid():N}.dwg"), "in vault");
        await alice.CheckInAsync([inVault], "v1");
        var localOnly = Write(PathIn(alice, $"L-{Guid.NewGuid():N}.pdf"), "only here");
        Write(PathIn(alice, "scratch.dwl"), "lock file");
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(inVault)!, "Sub"));

        var folder = await alice.GetFolderAsync("$/" + _project);

        Assert.Equal(2, folder.Files.Count);
        Assert.Equal(FileState.UpToDate, folder.Files.Single(f => f.LocalPath == inVault).State);
        Assert.Equal(FileState.NotInVault, folder.Files.Single(f => f.LocalPath == localOnly).State);
        Assert.Equal(["Sub"], folder.Subfolders);
    }

    [Fact]
    public async Task Search_results_carry_local_status()
    {
        var alice = Workspace("alice");
        var bob = Workspace("bob");
        var tag = Guid.NewGuid().ToString("N")[..10];
        await alice.CheckInAsync([Write(PathIn(alice, $"FIND-{tag}.dwg"), "x")], "v1");

        var forAlice = await alice.SearchAsync(tag);
        var forBob = await bob.SearchAsync(tag);

        var aliceHit = Assert.Single(forAlice.Hits);
        Assert.Equal(FileState.UpToDate, aliceHit.Status.State);
        Assert.Equal("$/" + _project, aliceHit.FolderPath);
        Assert.Equal(PathIn(alice, $"FIND-{tag}.dwg"), aliceHit.Status.LocalPath);
        Assert.Equal(FileState.NotDownloaded, Assert.Single(forBob.Hits).Status.State);
    }

    [Fact]
    public async Task Restoring_an_earlier_version_adds_it_as_the_newest_and_keeps_history()
    {
        var alice = Workspace("alice");
        var path = Write(PathIn(alice, $"R-{Guid.NewGuid():N}.dwg"), "first design");
        await alice.CheckInAsync([path], "v1");
        await alice.CheckOutAsync([path]);
        await alice.CheckInAsync([Write(path, "second design")], "v2");

        var result = await alice.RestoreVersionAsync(path, 1, "customer preferred the first");

        Assert.True(result.Succeeded, result.Summary);
        Assert.Equal("first design", File.ReadAllText(path));
        Assert.True(IsReadOnly(path));
        var status = Assert.Single(await alice.GetStatusAsync([path]));
        Assert.Equal(FileState.UpToDate, status.State);
        var history = await vault.Client("alice").GetHistoryAsync(status.File!.Id);
        Assert.Equal([3, 2, 1], history.Select(h => h.Version));
        Assert.Equal("Restored version 1: customer preferred the first", history[0].Comment);
    }

    [Fact]
    public async Task Thumbnails_are_stored_per_version_and_follow_a_restore()
    {
        var alice = Workspace("alice");
        var client = vault.Client("alice");
        var path = Write(PathIn(alice, $"T-{Guid.NewGuid():N}.dwg"), "v1");
        byte[] firstPicture = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
        await alice.CheckInAsync([path], "v1", new CheckInOptions { Thumbnail = _ => firstPicture });
        await alice.CheckOutAsync([path]);
        await alice.CheckInAsync([Write(path, "v2")], "v2"); // no thumbnail this time
        var fileId = (await alice.GetStatusAsync([path]))[0].File!.Id;

        var latestBefore = await client.GetThumbnailAsync(fileId);
        var original = await client.GetThumbnailAsync(fileId, 1);
        await alice.RestoreVersionAsync(path, 1);
        var latestAfter = await client.GetThumbnailAsync(fileId);

        Assert.Null(latestBefore);
        Assert.Equal(firstPicture, original);
        Assert.Equal(firstPicture, latestAfter);
    }

    [Fact]
    public async Task An_assembly_brings_every_file_it_uses_at_any_depth()
    {
        var alice = Workspace("alice");
        var bob = Workspace("bob");
        var tag = Guid.NewGuid().ToString("N")[..8];
        var harness = Write(PathIn(alice, $"HARNESS-{tag}.iam"), "harness");
        var connector = Write(PathIn(alice, $"CONN-{tag}.iam"), "connector");
        var pin = Write(PathIn(alice, $"PIN-{tag}.ipt"), "pin");
        var cable = Write(PathIn(alice, $"CABLE-{tag}.ipt"), "cable");
        var uses = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [harness] = [connector, cable],
            [connector] = [pin],
        };
        await alice.CheckInAsync([harness, connector, pin, cable], "harness", new CheckInOptions { References = p => uses.GetValueOrDefault(p) ?? [] });

        var bobsHarness = PathIn(bob, $"HARNESS-{tag}.iam");
        var everything = await bob.WithDependenciesAsync([bobsHarness]);
        var get = await bob.GetLatestAsync(everything);

        Assert.Equal(4, everything.Count);
        Assert.Contains(PathIn(bob, $"PIN-{tag}.ipt"), everything); // two levels down
        Assert.Equal("Updated 4 files to the latest version.", get.Summary);
        Assert.Equal("pin", File.ReadAllText(PathIn(bob, $"PIN-{tag}.ipt")));
    }

    [Fact]
    public async Task Delete_goes_to_the_trash_and_restore_brings_back_the_history()
    {
        var alice = Workspace("alice");
        var name = $"D-{Guid.NewGuid():N}.pdf";
        var path = Write(PathIn(alice, name), "spec v1");
        await alice.CheckInAsync([path], "v1");
        await alice.CheckOutAsync([path]);
        await alice.CheckInAsync([Write(path, "spec v2")], "v2");

        var deleted = await alice.DeleteAsync([path]);
        var trash = await vault.Client("alice").ListTrashAsync();
        var item = trash.Single(t => t.Name == name);
        var restored = await alice.RestoreFromTrashAsync([item]);

        Assert.True(deleted.Succeeded, deleted.Summary);
        Assert.Equal("alice", item.DeletedBy);
        Assert.True(restored.Succeeded, restored.Summary);
        Assert.Equal("spec v2", File.ReadAllText(path));
        Assert.Equal(2, (await alice.GetStatusAsync([path]))[0].File!.LatestVersion);
    }

    [Fact]
    public async Task A_part_still_used_by_an_assembly_cannot_be_deleted()
    {
        var alice = Workspace("alice");
        var tag = Guid.NewGuid().ToString("N")[..8];
        var assembly = Write(PathIn(alice, $"ASM-{tag}.iam"), "asm");
        var part = Write(PathIn(alice, $"PART-{tag}.ipt"), "part");
        await alice.CheckInAsync([assembly, part], "add", new CheckInOptions { References = p => p == assembly ? [part] : [] });

        var partOnly = await alice.DeleteAsync([part]);
        var partKept = File.Exists(part);
        var both = await alice.DeleteAsync([assembly, part]);

        Assert.False(partOnly.Succeeded);
        Assert.Contains($"PART-{tag}.ipt is used by ASM-{tag}.iam", partOnly.Summary);
        Assert.True(partKept);
        Assert.True(both.Succeeded, both.Summary);
        Assert.False(File.Exists(part));
    }

    /// <summary>Stands in for Apprentice: a "parent" whose text lists child paths, one per line.</summary>
    private sealed class TextListRepairer : IReferenceRepairer
    {
        public bool CanRepair(string parentPath) => parentPath.EndsWith(".iam", StringComparison.OrdinalIgnoreCase);

        public bool Repair(string parentPath, string oldChildPath, string newChildPath)
        {
            var text = File.ReadAllText(parentPath);
            if (!text.Contains(oldChildPath, StringComparison.OrdinalIgnoreCase))
                return false;
            File.WriteAllText(parentPath, text.Replace(oldChildPath, newChildPath, StringComparison.OrdinalIgnoreCase));
            return true;
        }
    }

    [Fact]
    public async Task Renaming_a_part_repairs_and_checks_in_the_assembly_that_uses_it()
    {
        var alice = Workspace("alice");
        var bob = Workspace("bob");
        var tag = Guid.NewGuid().ToString("N")[..8];
        var part = Write(PathIn(alice, $"BRACKET-{tag}.ipt"), "bracket");
        var assembly = Write(PathIn(alice, $"FRAME-{tag}.iam"), part);
        await alice.CheckInAsync([assembly, part], "add", new CheckInOptions { References = p => p == assembly ? [part] : [], Thumbnail = p => p == part ? [1, 2, 3] : null });

        var result = await alice.RenameAsync(part, $"BRACKET-LH-{tag}.ipt", [new TextListRepairer()]);

        var renamed = PathIn(alice, $"BRACKET-LH-{tag}.ipt");
        Assert.True(result.Succeeded, result.Summary);
        Assert.Contains("1 file that uses it was updated", result.Summary);
        Assert.False(File.Exists(part));
        Assert.Equal("bracket", File.ReadAllText(renamed));
        Assert.Equal(renamed, File.ReadAllText(assembly)); // the parent now points at the new name
        var statuses = await alice.GetStatusAsync([renamed, assembly]);
        Assert.All(statuses, s => Assert.Equal(FileState.UpToDate, s.State));
        var client = vault.Client("alice");
        Assert.Equal([1, 2, 3], await client.GetThumbnailAsync(statuses[0].File!.Id)); // same content keeps its picture
        Assert.Equal($"BRACKET-LH-{tag}.ipt", Assert.Single(await client.GetUsesAsync(statuses[1].File!.Id)).Name);

        // A colleague sees the new name and the repaired assembly.
        await bob.GetLatestAsync([PathIn(bob, $"BRACKET-LH-{tag}.ipt"), PathIn(bob, $"FRAME-{tag}.iam")]);
        Assert.Equal("bracket", File.ReadAllText(PathIn(bob, $"BRACKET-LH-{tag}.ipt")));
    }

    [Fact]
    public async Task Renaming_is_refused_while_a_parent_has_work_in_progress()
    {
        var alice = Workspace("alice");
        var tag = Guid.NewGuid().ToString("N")[..8];
        var part = Write(PathIn(alice, $"PIN-{tag}.ipt"), "pin");
        var assembly = Write(PathIn(alice, $"HINGE-{tag}.iam"), part);
        await alice.CheckInAsync([assembly, part], "add", new CheckInOptions { References = p => p == assembly ? [part] : [] });
        await alice.CheckOutAsync([assembly]);
        File.WriteAllText(assembly, part + "\nhalf-finished edit");

        var result = await alice.RenameAsync(part, $"PIN-LONG-{tag}.ipt", [new TextListRepairer()]);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Files, f => f.LocalPath == assembly);
        Assert.True(File.Exists(part));
        Assert.Equal(part + "\nhalf-finished edit", File.ReadAllText(assembly)); // the work in progress is untouched
        Assert.Equal(FileState.UpToDate, (await alice.GetStatusAsync([part]))[0].State); // and nothing was left checked out
    }

    [Fact]
    public async Task Copy_design_puts_nothing_in_the_vault_if_a_copy_cant_be_pointed_at_the_copies()
    {
        var alice = Workspace("alice");
        var tag = Guid.NewGuid().ToString("N")[..8];
        var part = Write(PathIn(alice, $"LEG-{tag}.ipt"), "leg");
        var assembly = Write(PathIn(alice, $"TABLE-{tag}.iam"), "refers to nothing the repairer can find");
        await alice.CheckInAsync([assembly, part], "add", new CheckInOptions { References = p => p == assembly ? [part] : [] });
        var copies = new Dictionary<string, string>
        {
            [assembly] = PathIn(alice, $"TABLE-V2-{tag}.iam"),
            [part] = PathIn(alice, $"LEG-V2-{tag}.ipt"),
        };

        var result = await alice.CopyDesignAsync(copies, "copy", [new TextListRepairer()]);

        Assert.False(result.Succeeded);
        Assert.All(copies.Values, c => Assert.False(File.Exists(c)));
        Assert.All(await alice.GetStatusAsync([.. copies.Values]), s => Assert.Null(s.File));
    }

    [Fact]
    public async Task A_record_for_a_different_file_at_the_same_path_never_makes_a_file_look_up_to_date()
    {
        // e.g. the same workspace used with another vault: same path and version number, different file.
        var alice = Workspace("alice");
        var bob = Workspace("bob");
        var name = $"SPEC-{Guid.NewGuid():N}.pdf";
        await alice.CheckInAsync([Write(PathIn(alice, name), "alice's file")], "v1");
        await bob.GetLatestAsync([PathIn(bob, name)]);
        await alice.DeleteAsync([PathIn(alice, name)]);
        Write(PathIn(alice, name), "a different file, same name");
        await alice.CheckInAsync([PathIn(alice, name)], "new file at the old name");

        var status = (await bob.GetStatusAsync([PathIn(bob, name)]))[0]; // bob's record is for the deleted file, also v1
        await bob.GetLatestAsync([PathIn(bob, name)]);

        Assert.NotEqual(FileState.UpToDate, status.State);
        Assert.Equal("a different file, same name", File.ReadAllText(PathIn(bob, name)));
    }

    [Fact]
    public async Task A_failed_rename_puts_everything_back()
    {
        var alice = Workspace("alice");
        var tag = Guid.NewGuid().ToString("N")[..8];
        var part = Write(PathIn(alice, $"P1-{tag}.ipt"), "one");
        var assembly = Write(PathIn(alice, $"A1-{tag}.iam"), part);
        var other = Write(PathIn(Workspace("carl"), $"TAKEN-{tag}.ipt"), "taken");
        await alice.CheckInAsync([assembly, part], "add", new CheckInOptions { References = p => p == assembly ? [part] : [] });
        // Another user's file already has the target name elsewhere in the vault (CAD names are vault-unique).
        await vault.Client("carl").CheckInAsync(new Openhold.Core.Contracts.CheckInRequest(Guid.NewGuid(), "x", [new Openhold.Core.Contracts.CheckInFile(null,
            (await vault.Client("carl").EnsureFolderAsync(VaultFixture.UniqueFolder("taken"))).Id, $"TAKEN-{tag}.ipt",
            await vault.Client("carl").UploadContentAsync(new MemoryStream([9])), null, null)]));

        var result = await alice.RenameAsync(part, $"TAKEN-{tag}.ipt", [new TextListRepairer()]);

        Assert.False(result.Succeeded);
        Assert.Contains("put back as it was", result.Summary);
        Assert.True(File.Exists(part));
        Assert.Equal(part, File.ReadAllText(assembly));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public async Task Rename_is_refused_when_a_parent_cannot_be_repaired()
    {
        var alice = Workspace("alice");
        var tag = Guid.NewGuid().ToString("N")[..8];
        var part = Write(PathIn(alice, $"X-{tag}.ipt"), "x");
        var drawing = Write(PathIn(alice, $"X-{tag}.dwg"), "drawing");
        await alice.CheckInAsync([drawing, part], "add", new CheckInOptions { References = p => p == drawing ? [part] : [] });

        var result = await alice.RenameAsync(part, $"Y-{tag}.ipt", [new TextListRepairer()]);

        Assert.False(result.Succeeded);
        Assert.Contains("can't update the references", result.Summary);
        Assert.True(File.Exists(part));
        Assert.Empty(await vault.Client("alice").GetLocksAsync([(await alice.GetStatusAsync([part]))[0].File!.Id]));
    }

    [Fact]
    public async Task Pack_and_go_zips_the_vault_versions_of_a_file_and_everything_it_uses()
    {
        var alice = Workspace("alice");
        var tag = Guid.NewGuid().ToString("N")[..8];
        var assembly = Write(PathIn(alice, $"SKID-{tag}.iam"), "skid");
        var pump = Write(PathIn(alice, $"PUMP-{tag}.ipt"), "pump");
        await alice.CheckInAsync([assembly, pump], "add", new CheckInOptions { References = p => p == assembly ? [pump] : [] });
        await alice.CheckOutAsync([pump]);
        Write(pump, "half-finished local edit"); // must not be sent
        var zipPath = Path.Combine(alice.Root, $"pack-{tag}.zip");

        var count = await alice.PackAndGoAsync([assembly], zipPath);

        using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
        Assert.Equal(2, count);
        var names = zip.Entries.Select(e => e.FullName).Order().ToList();
        Assert.Equal([$"{_project}/PUMP-{tag}.ipt", $"{_project}/SKID-{tag}.iam"], names);
        using var reader = new StreamReader(zip.GetEntry($"{_project}/PUMP-{tag}.ipt")!.Open());
        Assert.Equal("pump", reader.ReadToEnd());
    }

    /// <summary>Stands in for Apprentice: appends "name=value" lines to an Inventor file.</summary>
    private sealed class TextPropertyWriter : IPropertyWriter
    {
        public bool CanWrite(string path) => path.EndsWith(".ipt", StringComparison.OrdinalIgnoreCase);

        public void Write(string path, IReadOnlyDictionary<string, string> values) =>
            File.AppendAllLines(path, values.Select(v => $"{v.Key}={v.Value}"));
    }

    /// <summary>Like a DWG title block: holds only the properties it has attributes for.</summary>
    private sealed class TitleBlockLikeWriter(params string[] fields) : IPropertyWriter
    {
        public bool CanWrite(string path) => path.EndsWith(".ipt", StringComparison.OrdinalIgnoreCase);

        public bool CanHold(string path, string property) => fields.Contains(property);

        public void Write(string path, IReadOnlyDictionary<string, string> values)
        {
            Assert.All(values.Keys, key => Assert.Contains(key, fields)); // never asked to hold what it can't
            File.AppendAllLines(path, values.Select(v => $"{v.Key}={v.Value}"));
        }
    }

    [Fact]
    public async Task Properties_a_file_cant_hold_are_kept_in_the_vault_and_survive_the_next_cad_check_in()
    {
        var alice = Workspace("alice");
        var drawing = Write(PathIn(alice, $"GA-{Guid.NewGuid():N}.ipt"), "drawing");
        await alice.CheckInAsync([drawing], "first", new CheckInOptions { Properties = _ => new Dictionary<string, string> { ["Description"] = "Frame" } });

        var edit = await alice.EditPropertiesAsync(
            new Dictionary<string, IReadOnlyDictionary<string, string>> { [drawing] = new Dictionary<string, string> { ["Description"] = "Main frame", ["Vendor"] = "Acme" } },
            "add vendor", [new TitleBlockLikeWriter("Description")]);
        // The CAD program checks in again, sending only what the title block holds.
        await alice.CheckOutAsync([drawing]);
        Write(drawing, "drawing v3");
        await alice.CheckInAsync([drawing], "moved a dimension", new CheckInOptions { Properties = _ => new Dictionary<string, string> { ["Description"] = "Main frame" } });

        Assert.True(edit.Succeeded, edit.Summary);
        var file = (await alice.GetStatusAsync([drawing]))[0].File!;
        Assert.Equal(3, file.LatestVersion);
        Assert.Equal("Main frame", file.Properties["Description"]);
        Assert.Equal("Acme", file.Properties["Vendor"]);
        Assert.Equal(["Vendor"], file.VaultProperties.Keys);
        var found = await vault.Client("alice").SearchAsync($"Vendor:Acme {Path.GetFileNameWithoutExtension(drawing)}");
        Assert.Single(found.Hits);
    }

    [Fact]
    public async Task Editing_properties_writes_them_into_the_files_and_checks_in_one_new_version_each()
    {
        var alice = Workspace("alice");
        var tag = Guid.NewGuid().ToString("N")[..8];
        var part = Write(PathIn(alice, $"PLATE-{tag}.ipt"), "plate");
        var datasheet = Write(PathIn(alice, $"PLATE-{tag}.pdf"), "pdf");
        await alice.CheckInAsync([part, datasheet], "first", new CheckInOptions
        {
            Properties = _ => new Dictionary<string, string> { ["Description"] = "Plate", ["Revision"] = "A", ["Vendor"] = "Acme" },
            Thumbnail = p => p == part ? [1, 2, 3] : null,
        });

        var change = new Dictionary<string, string> { ["Description"] = "Base plate", ["Vendor"] = "" };
        var result = await alice.EditPropertiesAsync(
            new Dictionary<string, IReadOnlyDictionary<string, string>> { [part] = change, [datasheet] = change },
            "Description fixed", [new TextPropertyWriter()]);

        Assert.True(result.Succeeded, result.Summary);
        Assert.EndsWith($"Description=Base plate{Environment.NewLine}Vendor={Environment.NewLine}", File.ReadAllText(part)); // written into the CAD file
        Assert.Equal("pdf", File.ReadAllText(datasheet)); // the PDF's contents are untouched: vault-only properties
        var statuses = await alice.GetStatusAsync([part, datasheet]);
        foreach (var status in statuses)
        {
            Assert.Equal(FileState.UpToDate, status.State); // checked in and released
            Assert.Equal(2, status.File!.LatestVersion);
            Assert.Equal("Base plate", status.File.Properties["Description"]);
            Assert.Equal("A", status.File.Properties["Revision"]); // never changed unless edited
            Assert.False(status.File.Properties.ContainsKey("Vendor")); // empty value clears it
        }
        Assert.Equal([1, 2, 3], await vault.Client("alice").GetThumbnailAsync(statuses[0].File!.Id)); // preview kept
    }

    [Fact]
    public async Task Editing_properties_changes_nothing_if_any_file_cant_be_written()
    {
        var alice = Workspace("alice");
        var tag = Guid.NewGuid().ToString("N")[..8];
        var part = Write(PathIn(alice, $"SHAFT-{tag}.ipt"), "shaft");
        var drawing = Write(PathIn(alice, $"SHAFT-{tag}.dwg"), "dwg");
        await alice.CheckInAsync([part, drawing], "first");

        var change = new Dictionary<string, string> { ["Description"] = "Shaft" };
        var result = await alice.EditPropertiesAsync(
            new Dictionary<string, IReadOnlyDictionary<string, string>> { [part] = change, [drawing] = change },
            "", [new TextPropertyWriter()]);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Files, f => f.LocalPath == drawing); // no DWG writer here, so it would drift from its title block
        Assert.Equal("shaft", File.ReadAllText(part));
        Assert.All(await alice.GetStatusAsync([part, drawing]), s => Assert.Equal(1, s.File!.LatestVersion));
        Assert.All(await alice.GetStatusAsync([part, drawing]), s => Assert.Equal(FileState.UpToDate, s.State));
    }

    [Fact]
    public async Task Copy_design_copies_the_tree_points_copies_at_each_other_and_keeps_library_parts_shared()
    {
        var alice = Workspace("alice");
        var tag = Guid.NewGuid().ToString("N")[..8];
        var assembly = Write(PathIn(alice, $"CART-{tag}.iam"), "");
        var frame = Write(PathIn(alice, $"FRAME-{tag}.ipt"), "frame");
        var bolt = Write(PathIn(alice, $"M10-BOLT-{tag}.ipt"), "library bolt");
        File.WriteAllText(assembly, $"{frame}\n{bolt}");
        await alice.CheckInAsync([assembly, frame, bolt], "original", new CheckInOptions
        {
            References = p => p == assembly ? [frame, bolt] : [],
            Properties = p => p == frame ? new Dictionary<string, string> { ["Description"] = "Cart frame" } : null,
            Thumbnail = p => p == frame ? [7, 7, 7] : null,
        });

        var copies = new Dictionary<string, string>
        {
            [assembly] = PathIn(alice, $"CART-V2-{tag}.iam"),
            [frame] = PathIn(alice, $"FRAME-V2-{tag}.ipt"),
            // the library bolt is not copied: the new cart keeps using it
        };
        var result = await alice.CopyDesignAsync(copies, "Cart V2 from Cart", [new TextListRepairer()]);

        Assert.True(result.Succeeded, result.Summary);
        var newAssembly = copies[assembly];
        Assert.Equal($"{copies[frame]}\n{bolt}", File.ReadAllText(newAssembly)); // copy uses copied frame, original bolt
        Assert.Equal($"{frame}\n{bolt}", File.ReadAllText(assembly)); // original untouched
        var client = vault.Client("alice");
        var statuses = await alice.GetStatusAsync([newAssembly, copies[frame]]);
        var uses = await client.GetUsesAsync(statuses[0].File!.Id);
        Assert.Equal([$"FRAME-V2-{tag}.ipt", $"M10-BOLT-{tag}.ipt"], uses.Select(u => u.Name).Order());
        Assert.Equal("Cart frame", statuses[1].File!.Properties["Description"]);
        Assert.Equal([7, 7, 7], await client.GetThumbnailAsync(statuses[1].File!.Id));
        Assert.Equal(2, (await client.GetWhereUsedAsync((await alice.GetStatusAsync([bolt]))[0].File!.Id)).Count); // bolt now used by both carts
    }

    [Fact]
    public async Task An_earlier_version_opens_as_a_separate_read_only_copy()
    {
        var alice = Workspace("alice");
        var name = $"O-{Guid.NewGuid():N}.dwg";
        var path = Write(PathIn(alice, name), "version one");
        var added = await alice.CheckInAsync([path], "v1");
        await alice.CheckOutAsync([path]);
        await alice.CheckInAsync([Write(path, "version two")], "v2");
        var fileId = (await alice.GetStatusAsync([path]))[0].File!.Id;

        var copy = await alice.GetVersionCopyAsync(fileId, name, 1);

        Assert.True(added.Succeeded);
        Assert.Equal("version one", File.ReadAllText(copy));
        Assert.EndsWith(" (v1).dwg", copy);
        Assert.False(alice.Contains(copy));
        Assert.True(IsReadOnly(copy));
        Assert.Equal("version two", File.ReadAllText(path));
    }

    [Fact]
    public async Task A_drawing_held_open_by_cad_can_still_be_checked_in()
    {
        var alice = Workspace("alice");
        var path = Write(PathIn(alice, $"EL-{Guid.NewGuid():N}.dwg"), "open in AutoCAD");

        // AutoCAD keeps an open drawing locked for writing and lets others read it.
        await using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            var result = await alice.CheckInAsync([path], "checked in while open", new CheckInOptions { KeepCheckedOut = true });
            Assert.True(result.Succeeded, result.Summary);
        }
    }

    [Fact]
    public async Task Files_outside_the_workspace_are_rejected_with_an_explanation()
    {
        var alice = Workspace("alice");
        var outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.dwg");

        var result = await alice.CheckInAsync([outside], "nope");

        Assert.False(result.Succeeded);
        Assert.Contains("outside your Openhold workspace", result.Summary);
    }
}
