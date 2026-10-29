using System.Net;
using Openhold.Client.Sdk;
using Openhold.Core.Configuration;
using Openhold.Core.Contracts;

namespace Openhold.Server.Tests;

[Collection(VaultCollection.Name)]
public sealed class LifecycleTests(VaultFixture vault)
{
    private static string Unique(string name) => $"{Path.GetFileNameWithoutExtension(name)}-{Guid.NewGuid():N}{Path.GetExtension(name)}";

    private static Task<StateChangeResult> ChangeState(OpenholdClient client, string toState, string? comment, params Guid[] ids) =>
        client.ChangeStateAsync(new ChangeStateRequest(Guid.NewGuid(), ids, toState, comment));

    /// <summary>Adds a part, then an assembly that uses it. Returns (assembly, part).</summary>
    private static async Task<(Guid Assembly, Guid Part)> AddAssemblyAsync(OpenholdClient client, string folderName)
    {
        var folder = await client.EnsureFolderAsync(VaultFixture.UniqueFolder(folderName));
        var part = (await VaultFixture.AddFilesAsync(client, folder.Path, Unique("part.ipt")))[0];
        var manifest = await client.UploadContentAsync(new MemoryStream(VaultFixture.Content(3000, 99)));
        var added = await client.CheckInAsync(new CheckInRequest(Guid.NewGuid(), "add assembly",
            [new CheckInFile(null, folder.Id, Unique("assembly.iam"), manifest, null, [new CheckInReference(part, null, null, "part.ipt")])]));
        return (added.Versions.Single().FileId, part);
    }

    [Fact]
    public async Task New_files_get_a_category_from_their_extension_and_start_in_its_first_state()
    {
        var client = vault.Client("lena");
        var folder = VaultFixture.UniqueFolder("categories");
        var ids = await VaultFixture.AddFilesAsync(client, folder, Unique("bracket.ipt"), Unique("notes.txt"));

        var part = await client.GetFileAsync(ids[0]);
        var notes = await client.GetFileAsync(ids[1]);

        Assert.Equal(("engineering", "Engineering", DefaultConfiguration.WorkInProgress), (part.Category, part.CategoryName, part.State));
        Assert.Equal(("document", DefaultConfiguration.WorkInProgress), (notes.Category, notes.State));
        Assert.False(part.StateLocked);
    }

    [Fact]
    public async Task A_released_file_cant_be_checked_out_until_it_is_reopened()
    {
        var client = vault.Client("lena");
        var id = (await VaultFixture.AddFilesAsync(client, VaultFixture.UniqueFolder("release"), Unique("plate.ipt")))[0];

        var released = await ChangeState(client, DefaultConfiguration.Released, "first issue", id);

        Assert.True(released.Applied);
        Assert.Equal(2, released.Versions.Single().Version);
        var file = await client.GetFileAsync(id);
        Assert.Equal(DefaultConfiguration.Released, file.State);
        Assert.True(file.StateLocked && file.StateReleased);
        var refused = await Assert.ThrowsAsync<OpenholdApiException>(() => client.CheckOutAsync([id]));
        Assert.Equal("state_locked", refused.Code);
        Assert.Contains("Reopen for a new revision", refused.Message);

        await ChangeState(client, DefaultConfiguration.WorkInProgress, null, id);
        Assert.True((await client.CheckOutAsync([id])).Succeeded);

        // History records each state change as a version with the same content.
        var history = await client.GetHistoryAsync(id);
        Assert.Equal([DefaultConfiguration.WorkInProgress, DefaultConfiguration.Released, DefaultConfiguration.WorkInProgress], history.Select(h => h.State).Reverse());
        Assert.Single(history.Select(h => h.ContentHash).Distinct());
        Assert.Equal("Release: first issue", history[1].Comment);
    }

    [Fact]
    public async Task An_assembly_is_released_only_with_its_parts()
    {
        var client = vault.Client("lena");
        var (assembly, part) = await AddAssemblyAsync(client, "children");

        var check = await client.ChangeStateAsync(new ChangeStateRequest(Guid.NewGuid(), [assembly], DefaultConfiguration.Released, CheckOnly: true));
        var refused = await Assert.ThrowsAsync<OpenholdApiException>(() => ChangeState(client, DefaultConfiguration.Released, null, assembly));

        Assert.Contains(check.Problems, p => p.FileId == assembly && p.Message.Contains("which is Work in Progress"));
        Assert.Equal("state_change_refused", refused.Code);
        Assert.Equal(1, (await client.GetFileAsync(assembly)).LatestVersion); // nothing changed

        var together = await ChangeState(client, DefaultConfiguration.Released, null, assembly, part);

        Assert.Equal(2, together.Versions.Count);
        var uses = await client.GetUsesAsync(assembly);
        Assert.Equal(2, uses.Single().Version); // the released assembly records the released part
    }

    [Fact]
    public async Task State_changes_are_refused_while_a_file_is_checked_out_and_obsolete_needs_a_reason()
    {
        var client = vault.Client("lena");
        var id = (await VaultFixture.AddFilesAsync(client, VaultFixture.UniqueFolder("checkedout"), Unique("shaft.ipt")))[0];
        await client.CheckOutAsync([id]);

        var checkedOut = await Assert.ThrowsAsync<OpenholdApiException>(() => ChangeState(client, DefaultConfiguration.Released, null, id));
        Assert.Contains("checked out to lena", checkedOut.Message);

        await client.UndoCheckOutAsync([id]);
        await ChangeState(client, DefaultConfiguration.Released, null, id);
        var noReason = await Assert.ThrowsAsync<OpenholdApiException>(() => ChangeState(client, DefaultConfiguration.Obsolete, " ", id));
        Assert.Contains("comment is required", noReason.Message);
        await ChangeState(client, DefaultConfiguration.Obsolete, "superseded", id);
        var again = await Assert.ThrowsAsync<OpenholdApiException>(() => ChangeState(client, DefaultConfiguration.Obsolete, "again", id));
        Assert.Contains("already Obsolete", again.Message);
    }

    [Fact]
    public async Task A_retried_state_change_happens_once()
    {
        var client = vault.Client("lena");
        var id = (await VaultFixture.AddFilesAsync(client, VaultFixture.UniqueFolder("retry"), Unique("pin.ipt")))[0];
        var request = new ChangeStateRequest(Guid.NewGuid(), [id], DefaultConfiguration.Released);

        await client.ChangeStateAsync(request);
        var retry = await client.ChangeStateAsync(request);

        Assert.True(retry.Applied);
        Assert.Equal(2, retry.Versions.Single().Version);
        Assert.Equal(2, (await client.GetFileAsync(id)).LatestVersion);
    }

    [Fact]
    public async Task Restoring_a_released_version_keeps_the_files_current_state()
    {
        var client = vault.Client("lena");
        var id = (await VaultFixture.AddFilesAsync(client, VaultFixture.UniqueFolder("restore"), Unique("cover.ipt")))[0];
        await ChangeState(client, DefaultConfiguration.Released, null, id);          // v2 released
        await ChangeState(client, DefaultConfiguration.WorkInProgress, null, id);    // v3
        await client.CheckOutAsync([id]);

        await client.RestoreVersionAsync(id, new RestoreRequest(Guid.NewGuid(), 2));

        var file = await client.GetFileAsync(id);
        Assert.Equal(4, file.LatestVersion);
        Assert.Equal(DefaultConfiguration.WorkInProgress, file.State);
    }

    [Fact]
    public async Task Only_administrators_can_delete_a_released_file()
    {
        var client = vault.Client("lena");
        var id = (await VaultFixture.AddFilesAsync(client, VaultFixture.UniqueFolder("delete"), Unique("lid.ipt")))[0];
        await ChangeState(client, DefaultConfiguration.Released, null, id);

        var refused = await Assert.ThrowsAsync<OpenholdApiException>(() => client.DeleteFilesAsync([id]));
        Assert.Equal("state_locked", refused.Code);

        await vault.Client(VaultFixture.Administrator).DeleteFilesAsync([id]);
    }

    [Fact]
    public async Task A_file_can_be_moved_to_the_buyout_category_and_made_active()
    {
        var client = vault.Client("lena");
        var id = (await VaultFixture.AddFilesAsync(client, VaultFixture.UniqueFolder("buyout"), Unique("motor.stp")))[0];

        // The category decides how a file is released, so only an administrator can change it.
        var refused = await Assert.ThrowsAsync<OpenholdApiException>(() => client.ChangeCategoryAsync(new ChangeCategoryRequest(Guid.NewGuid(), [id], "buyout")));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        await vault.Client(VaultFixture.Administrator).ChangeCategoryAsync(new ChangeCategoryRequest(Guid.NewGuid(), [id], "buyout"));
        await ChangeState(client, DefaultConfiguration.Active, null, id);

        var file = await client.GetFileAsync(id);
        Assert.Equal(("buyout", DefaultConfiguration.Active, true), (file.Category, file.State, file.StateReleased));
        Assert.Contains(await client.ReserveNumbersAsync(VaultFixture.BuyoutScheme), n => n.Formatted.StartsWith("BO-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_configuration_save_is_kept_with_who_saved_it()
    {
        var admin = vault.Client(VaultFixture.Administrator);
        var before = await admin.GetConfigurationAsync();
        var comment = $"renamed checkers {Guid.NewGuid():N}";
        var renamed = before.Configuration with
        {
            Groups = before.Configuration.Groups.Select(g => g.Id == "checkers" ? g with { Name = "Drawing checkers" } : g).ToList(),
        };

        var saved = await admin.SaveConfigurationAsync(new SaveConfigurationRequest(before.Version, renamed, comment));
        var history = await admin.GetConfigurationHistoryAsync();
        var old = await admin.GetConfigurationVersionAsync(before.Version);

        var entry = Assert.Single(history, h => h.Version == saved.Version);
        Assert.Equal((VaultFixture.Administrator, comment), (entry.SavedBy, entry.Comment));
        Assert.Equal("Drawing checkers", saved.Configuration.Group("checkers")!.Name);
        Assert.Equal(before.Configuration.Group("checkers")!.Name, old.Configuration.Group("checkers")!.Name);
        Assert.Contains(await admin.ListUsersAsync(), u => u.Username == VaultFixture.Administrator);
    }

    [Fact]
    public async Task Only_administrators_save_configuration_and_never_from_a_stale_copy()
    {
        var current = await vault.Client("lena").GetConfigurationAsync();

        var notAdmin = await Assert.ThrowsAsync<OpenholdApiException>(() =>
            vault.Client("lena").SaveConfigurationAsync(new SaveConfigurationRequest(current.Version, current.Configuration)));
        Assert.Equal(HttpStatusCode.Forbidden, notAdmin.StatusCode);

        var admin = vault.Client(VaultFixture.Administrator);
        var stale = await Assert.ThrowsAsync<OpenholdApiException>(() =>
            admin.SaveConfigurationAsync(new SaveConfigurationRequest(current.Version - 1, current.Configuration)));
        Assert.Equal("configuration_changed", stale.Code);

        var withoutEngineering = current.Configuration with { Categories = current.Configuration.Categories.Where(c => c.Id != "engineering").ToList() };
        var inUse = await Assert.ThrowsAsync<OpenholdApiException>(() =>
            admin.SaveConfigurationAsync(new SaveConfigurationRequest(current.Version, withoutEngineering)));
        Assert.Equal("invalid_configuration", inUse.Code);
        Assert.Contains("can't be removed", inUse.Message);
    }

    [Fact]
    public async Task A_transition_that_needs_approval_waits_for_a_checker_who_isnt_the_requester()
    {
        var folder = VaultFixture.UniqueFolder("reviewed");
        var groupId = $"checkers-{Guid.NewGuid():N}";
        await ConfigureAsync(c => c with
        {
            Groups = [.. c.Groups, new GroupDefinition(groupId, "Checkers for test", ["carol", "alice"])],
            Lifecycles = [.. c.Lifecycles, ReviewedLifecycle(groupId)],
            Categories = [new CategoryDefinition(groupId, "Reviewed", LifecycleId: groupId, Folders: [folder + "/*"]), .. c.Categories],
        });
        var alice = vault.Client("alice");
        var id = (await VaultFixture.AddFilesAsync(alice, folder, Unique("frame.ipt")))[0];
        await ChangeState(alice, DefaultConfiguration.InReview, null, id);

        var submitted = await ChangeState(alice, DefaultConfiguration.Released, "ready", id);

        Assert.False(submitted.Applied);
        Assert.True(submitted.NeedsApproval);
        var again = await Assert.ThrowsAsync<OpenholdApiException>(() => ChangeState(alice, DefaultConfiguration.Released, null, id));
        Assert.Contains("already waiting for approval", again.Message);
        var own = await Assert.ThrowsAsync<OpenholdApiException>(() => alice.ApproveAsync(submitted.Id));
        Assert.Equal("own_request", own.Code);
        var outsider = await Assert.ThrowsAsync<OpenholdApiException>(() => vault.Client("bob").ApproveAsync(submitted.Id));
        Assert.Equal("not_an_approver", outsider.Code);

        var carol = vault.Client("carol");
        var inbox = await carol.ListChangeRequestsAsync();
        var request = Assert.Single(inbox, r => r.Id == submitted.Id);
        Assert.True(request.CanApprove);
        Assert.Equal(id, request.Files.Single().FileId);

        var approved = await carol.ApproveAsync(submitted.Id, "checked");

        Assert.Equal("approved", approved.Status);
        var file = await alice.GetFileAsync(id);
        Assert.Equal(DefaultConfiguration.Released, file.State);
        Assert.Equal("alice", (await alice.GetHistoryAsync(id))[0].Username);
        Assert.Contains("approved by carol", (await alice.GetHistoryAsync(id))[0].Comment);
    }

    [Fact]
    public async Task A_rejected_or_cancelled_request_changes_nothing()
    {
        var folder = VaultFixture.UniqueFolder("rejected");
        var groupId = $"checkers-{Guid.NewGuid():N}";
        await ConfigureAsync(c => c with
        {
            Groups = [.. c.Groups, new GroupDefinition(groupId, "Checkers for test", ["carol"])],
            Lifecycles = [.. c.Lifecycles, ReviewedLifecycle(groupId)],
            Categories = [new CategoryDefinition(groupId, "Reviewed", LifecycleId: groupId, Folders: [folder + "/*"]), .. c.Categories],
        });
        var alice = vault.Client("alice");
        var ids = await VaultFixture.AddFilesAsync(alice, folder, Unique("a.ipt"), Unique("b.ipt"));
        await ChangeState(alice, DefaultConfiguration.InReview, null, ids);

        var first = await ChangeState(alice, DefaultConfiguration.Released, null, ids[0]);
        var noReason = await Assert.ThrowsAsync<OpenholdApiException>(() => vault.Client("carol").RejectAsync(first.Id, " "));
        Assert.Equal("comment_required", noReason.Code);
        var rejected = await vault.Client("carol").RejectAsync(first.Id, "dimension missing");

        var second = await ChangeState(alice, DefaultConfiguration.Released, null, ids[1]);
        var cancelled = await alice.CancelChangeRequestAsync(second.Id);

        Assert.Equal(("rejected", "cancelled"), (rejected.Status, cancelled.Status));
        foreach (var id in ids)
            Assert.Equal(DefaultConfiguration.InReview, (await alice.GetFileAsync(id)).State);
        Assert.DoesNotContain(await vault.Client("carol").ListChangeRequestsAsync(), r => r.Id == first.Id || r.Id == second.Id);
    }

    [Fact]
    public async Task Each_approver_counts_once_so_two_rules_need_two_people()
    {
        var folder = VaultFixture.UniqueFolder("two-rules");
        var engineering = $"eng-{Guid.NewGuid():N}";
        var quality = $"qa-{Guid.NewGuid():N}";
        await ConfigureAsync(c => c with
        {
            Groups = [.. c.Groups, new GroupDefinition(engineering, "Engineering", ["carol", "dave"]), new GroupDefinition(quality, "Quality", ["carol"])],
            Lifecycles = [.. c.Lifecycles, ReviewedLifecycle(engineering, new ApprovalRule(engineering), new ApprovalRule(quality))],
            Categories = [new CategoryDefinition(engineering, "Two rules", LifecycleId: engineering, Folders: [folder + "/*"]), .. c.Categories],
        });
        var alice = vault.Client("alice");
        var id = (await VaultFixture.AddFilesAsync(alice, folder, Unique("beam.ipt")))[0];
        await ChangeState(alice, DefaultConfiguration.InReview, null, id);
        var request = await ChangeState(alice, DefaultConfiguration.Released, null, id);

        var afterCarol = await vault.Client("carol").ApproveAsync(request.Id); // in both groups, but counts for one
        var afterDave = await vault.Client("dave").ApproveAsync(request.Id);

        Assert.Equal("pending", afterCarol.Status);
        Assert.Equal("approved", afterDave.Status);
        Assert.Equal(DefaultConfiguration.Released, (await alice.GetFileAsync(id)).State);
    }

    [Fact]
    public async Task An_approved_change_that_cant_be_applied_is_blocked_and_frees_its_files()
    {
        var folder = VaultFixture.UniqueFolder("blocked");
        var groupId = $"checkers-{Guid.NewGuid():N}";
        await ConfigureAsync(c => c with
        {
            Groups = [.. c.Groups, new GroupDefinition(groupId, "Checkers for test", ["carol"])],
            Lifecycles = [.. c.Lifecycles, ReviewedLifecycle(groupId)],
            Categories = [new CategoryDefinition(groupId, "Reviewed", LifecycleId: groupId, Folders: [folder + "/*"]), .. c.Categories],
        });
        var alice = vault.Client("alice");
        var ids = await VaultFixture.AddFilesAsync(alice, folder, Unique("a.ipt"), Unique("b.ipt"));
        await ChangeState(alice, DefaultConfiguration.InReview, null, ids);
        var request = await ChangeState(alice, DefaultConfiguration.Released, null, ids);
        await vault.Client(VaultFixture.Administrator).DeleteFilesAsync([ids[1]]);

        var result = await vault.Client("carol").ApproveAsync(request.Id);

        Assert.Equal("blocked", result.Status);
        Assert.Contains(result.Problems, p => p.Contains("deleted"));
        Assert.Equal(DefaultConfiguration.InReview, (await alice.GetFileAsync(ids[0])).State);
        var resubmitted = await ChangeState(alice, DefaultConfiguration.Released, "again", ids[0]); // free again
        Assert.True(resubmitted.NeedsApproval);
    }

    [Fact]
    public async Task Configuration_saves_cant_lock_out_their_author_or_quietly_unrelease_files()
    {
        var admin = vault.Client(VaultFixture.Administrator);
        var lena = vault.Client("lena");
        var folder = VaultFixture.UniqueFolder("unrelease");
        var categoryId = $"cat-{Guid.NewGuid():N}";
        await ConfigureAsync(c => c with { Categories = [new CategoryDefinition(categoryId, "Temporary", LifecycleId: "release", Folders: [folder + "/*"]), .. c.Categories] });
        var id = (await VaultFixture.AddFilesAsync(lena, folder, Unique("cover.ipt")))[0];
        await ChangeState(lena, DefaultConfiguration.Released, null, id);
        var current = await admin.GetConfigurationAsync();

        var withoutLifecycle = current.Configuration with
        {
            Categories = current.Configuration.Categories.Select(c => c.Id == categoryId ? c with { LifecycleId = null } : c).ToList(),
        };
        var unrelease = await Assert.ThrowsAsync<OpenholdApiException>(() => admin.SaveConfigurationAsync(new SaveConfigurationRequest(current.Version, withoutLifecycle)));
        Assert.Contains("can't lose its lifecycle", unrelease.Message);

        // An administrator by the vault's configuration (not a bootstrap one) can't remove themselves.
        var withLena = current.Configuration with
        {
            Groups = current.Configuration.Groups.Select(g => g.Id == VaultConfiguration.AdministratorsGroup ? g with { Members = [.. g.Members, "lena"] } : g).ToList(),
        };
        var saved = await admin.SaveConfigurationAsync(new SaveConfigurationRequest(current.Version, withLena));
        var withoutLena = saved.Configuration with
        {
            Groups = saved.Configuration.Groups.Select(g => g.Id == VaultConfiguration.AdministratorsGroup ? g with { Members = g.Members.Where(m => m != "lena").ToList() } : g).ToList(),
        };
        var lockout = await Assert.ThrowsAsync<OpenholdApiException>(() => lena.SaveConfigurationAsync(new SaveConfigurationRequest(saved.Version, withoutLena)));
        Assert.Contains("no longer be allowed to change the configuration", lockout.Message);
        await admin.SaveConfigurationAsync(new SaveConfigurationRequest(saved.Version, withoutLena));
    }

    private static LifecycleDefinition ReviewedLifecycle(string id, params ApprovalRule[] rules) => new(id, "Reviewed for test",
        [
            new LifecycleState(DefaultConfiguration.WorkInProgress),
            new LifecycleState(DefaultConfiguration.InReview, Locked: true),
            new LifecycleState(DefaultConfiguration.Released, Locked: true, Released: true),
        ],
        [
            new LifecycleTransition(DefaultConfiguration.WorkInProgress, DefaultConfiguration.InReview),
            new LifecycleTransition(DefaultConfiguration.InReview, DefaultConfiguration.Released, "Release", Approvals: rules.Length > 0 ? rules : [new ApprovalRule(id)]),
        ]);

    private async Task ConfigureAsync(Func<VaultConfiguration, VaultConfiguration> change)
    {
        var admin = vault.Client(VaultFixture.Administrator);
        var current = await admin.GetConfigurationAsync();
        await admin.SaveConfigurationAsync(new SaveConfigurationRequest(current.Version, change(current.Configuration), "test"));
    }
}
