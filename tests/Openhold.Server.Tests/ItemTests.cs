using System.Net;
using Openhold.Client.Sdk;
using Openhold.Core.Configuration;
using Openhold.Core.Contracts;
using Openhold.Core.Numbering;

namespace Openhold.Server.Tests;

[Collection(VaultCollection.Name)]
public sealed class ItemTests(VaultFixture vault)
{
    private OpenholdClient Admin => vault.Client(VaultFixture.Administrator);

    private static string NewUsername() => $"user{Guid.NewGuid():N}"[..14];

    /// <summary>A manufacturer part number nobody else's test uses.</summary>
    private static string NewMpn() => $"LC1-D{Random.Shared.Next(100000, 999999)} M7";

    private static SaveItemRequest Breaker(string mpn, string manufacturer = "Schneider Electric") => new(
        "Circuit breaker 16 A 3 pole C curve", ItemKinds.Bought, ClassId: "breakers",
        Attributes: new Dictionary<string, string> { ["Rated current"] = "16", ["Poles"] = "3", ["Curve"] = "c" },
        ManufacturerParts: [new ManufacturerPart(manufacturer, mpn)]);

    [Fact]
    public async Task A_new_item_gets_the_next_universal_number_with_a_valid_check_digit()
    {
        var first = await vault.Client(NewUsername()).CreateItemAsync(Breaker(NewMpn()));
        var second = await vault.Client(NewUsername()).CreateItemAsync(new SaveItemRequest("Mounting plate", ItemKinds.Made, ClassId: "sheet-metal"));

        Assert.Matches(@"^P-\d{6}-\d$", first.Number);
        Assert.True(Damm.IsValid(first.Number.Replace("P-", "").Replace("-", "")));
        Assert.True(long.Parse(second.Number[2..8]) > long.Parse(first.Number[2..8]));
        Assert.Equal(ItemStates.Draft, first.State);
        Assert.Equal("Electrical / Circuit breakers", first.ClassName);
        Assert.Equal("C", first.Attributes["Curve"]); // matched to the allowed choice's spelling
        Assert.True(first.ManufacturerParts.Single().Preferred);
    }

    [Fact]
    public async Task The_same_manufacturer_part_can_never_get_a_second_number()
    {
        var mpn = NewMpn();
        var original = await Admin.CreateItemAsync(Breaker(mpn));

        var duplicate = await Assert.ThrowsAsync<OpenholdApiException>(() =>
            vault.Client(NewUsername()).CreateItemAsync(Breaker(mpn.ToLowerInvariant().Replace("-", " "), "SCHNEIDER ELECTRIC")));
        var candidates = await Admin.FindDuplicateItemsAsync(new DuplicateCheckRequest([new ManufacturerPart("Another maker", mpn.Replace(" ", ""))]));

        Assert.Equal((HttpStatusCode.Conflict, "duplicate_manufacturer_part"), (duplicate.StatusCode, duplicate.Code));
        Assert.Contains(original.Number, duplicate.Message);
        Assert.Equal(original.Id, candidates.Single().Item.Id);
    }

    [Fact]
    public async Task Only_a_part_librarian_releases_and_only_when_the_item_is_complete()
    {
        var engineer = vault.Client(NewUsername());
        var librarianName = NewUsername();
        var draft = await engineer.CreateItemAsync(new SaveItemRequest("Cable 4 core", ItemKinds.Bought, ClassId: "cable",
            Attributes: new Dictionary<string, string> { ["Cores"] = "4" }));

        var refused = await Assert.ThrowsAsync<OpenholdApiException>(() => engineer.ChangeItemStateAsync(draft.Id, new ItemStateRequest(ItemStates.Released, draft.RowVersion)));
        var current = await Admin.GetConfigurationAsync();
        await Admin.SaveConfigurationAsync(new SaveConfigurationRequest(current.Version, current.Configuration with
        {
            Groups = [.. current.Configuration.Groups.Select(g => g.Id == "part-librarians" ? g with { Members = [.. g.Members, librarianName] } : g)],
        }));
        var librarian = vault.Client(librarianName);
        var incomplete = await Assert.ThrowsAsync<OpenholdApiException>(() => librarian.ChangeItemStateAsync(draft.Id, new ItemStateRequest(ItemStates.Released, draft.RowVersion)));
        var completed = await engineer.UpdateItemAsync(draft.Id, new SaveItemRequest("Cable 4 core 2.5 mm²", ItemKinds.Bought, ClassId: "cable",
            Attributes: new Dictionary<string, string> { ["Cores"] = "4", ["Conductor size"] = "2.5" },
            ManufacturerParts: [new ManufacturerPart("Olex", NewMpn())], RowVersion: draft.RowVersion));
        var released = await librarian.ChangeItemStateAsync(draft.Id, new ItemStateRequest(ItemStates.Released, completed.RowVersion));
        var lockedForEngineer = await Assert.ThrowsAsync<OpenholdApiException>(() =>
            engineer.UpdateItemAsync(draft.Id, new SaveItemRequest("Renamed", ItemKinds.Bought, ClassId: "cable", RowVersion: released.RowVersion)));
        var needsReason = await Assert.ThrowsAsync<OpenholdApiException>(() => librarian.ChangeItemStateAsync(draft.Id, new ItemStateRequest(ItemStates.Obsolete, released.RowVersion)));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("not_ready_to_release", incomplete.Code);
        Assert.Contains("Conductor size", incomplete.Message);
        Assert.Contains("manufacturer", incomplete.Message);
        Assert.Equal((ItemStates.Released, librarianName), (released.State, released.ReleasedBy));
        Assert.Equal(HttpStatusCode.Forbidden, lockedForEngineer.StatusCode);
        Assert.Equal("comment_required", needsReason.Code);
    }

    [Fact]
    public async Task Two_people_editing_the_same_item_cannot_overwrite_each_other()
    {
        var item = await Admin.CreateItemAsync(new SaveItemRequest("Bracket", ItemKinds.Made, ClassId: "machined"));
        await Admin.UpdateItemAsync(item.Id, new SaveItemRequest("Bracket, left", ItemKinds.Made, ClassId: "machined", RowVersion: item.RowVersion));

        var stale = await Assert.ThrowsAsync<OpenholdApiException>(() =>
            Admin.UpdateItemAsync(item.Id, new SaveItemRequest("Bracket, right", ItemKinds.Made, ClassId: "machined", RowVersion: item.RowVersion)));

        Assert.Equal("item_changed", stale.Code);
        Assert.Equal("Bracket, left", (await Admin.GetItemAsync(item.Id)).Description);
    }

    [Fact]
    public async Task Items_are_found_by_legacy_number_manufacturer_part_attribute_or_linked_file()
    {
        var folder = VaultFixture.UniqueFolder("items");
        var files = await VaultFixture.AddFilesAsync(Admin, folder, "BRACKET.ipt");
        var legacy = $"PRJ01-{Guid.NewGuid():N}"[..14];
        var mpn = NewMpn();
        var item = await Admin.CreateItemAsync(new SaveItemRequest("Contactor 9 A", ItemKinds.Bought, ClassId: "contactors",
            Attributes: new Dictionary<string, string> { ["Coil voltage"] = "24 V DC" },
            ManufacturerParts: [new ManufacturerPart("Schneider Electric", mpn)],
            CrossReferences: [new CrossReference("Legacy vault", legacy)],
            Files: [new ItemFileLink(files[0], "datasheet")]));

        var byLegacy = await Admin.GetItemByNumberAsync(legacy.ToLowerInvariant());
        var byMpn = await Admin.SearchItemsAsync(new ItemSearchRequest(mpn.Replace("-", "").Replace(" ", "")));
        var byWords = await Admin.SearchItemsAsync(new ItemSearchRequest("contactor 24", ClassId: "electrical"));
        var forFile = await Admin.GetItemsForFileAsync(files[0]);
        var takenLegacy = await Assert.ThrowsAsync<OpenholdApiException>(() =>
            Admin.CreateItemAsync(new SaveItemRequest("Copy", ItemKinds.Made, CrossReferences: [new CrossReference("LEGACY VAULT", legacy)])));

        Assert.Equal(item.Id, byLegacy.Id);
        Assert.Contains(byMpn, i => i.Id == item.Id);
        Assert.Contains(byWords, i => i.Id == item.Id);
        Assert.Equal(item.Id, forFile.Single().Id);
        Assert.Equal(("datasheet", "BRACKET.ipt"), (item.Files.Single().Role, item.Files.Single().Name));
        Assert.Equal("duplicate_cross_reference", takenLegacy.Code);
    }

    [Fact]
    public async Task Attribute_values_must_be_one_of_the_class_choices()
    {
        var invalid = await Assert.ThrowsAsync<OpenholdApiException>(() => Admin.CreateItemAsync(new SaveItemRequest("Breaker", ItemKinds.Bought, ClassId: "breakers",
            Attributes: new Dictionary<string, string> { ["Poles"] = "5" })));
        var unknownClass = await Assert.ThrowsAsync<OpenholdApiException>(() => Admin.CreateItemAsync(new SaveItemRequest("Thing", ItemKinds.Made, ClassId: "no-such-class")));

        Assert.Equal("invalid_attribute", invalid.Code);
        Assert.Contains("1, 2, 3, 4", invalid.Message);
        Assert.Equal("unknown_class", unknownClass.Code);
    }

    [Fact]
    public async Task A_class_that_items_use_cannot_be_removed()
    {
        var classId = $"cls-{Guid.NewGuid():N}"[..12];
        var current = await Admin.GetConfigurationAsync();
        var added = await Admin.SaveConfigurationAsync(new SaveConfigurationRequest(current.Version, current.Configuration with
        {
            ItemClasses = [.. current.Configuration.ItemClasses ?? [], new ItemClass(classId, "Hose fittings")],
        }));
        var item = await Admin.CreateItemAsync(new SaveItemRequest("Fitting", ItemKinds.Bought, ClassId: classId));

        var refused = await Assert.ThrowsAsync<OpenholdApiException>(() => Admin.SaveConfigurationAsync(new SaveConfigurationRequest(added.Version, added.Configuration with
        {
            ItemClasses = [.. added.Configuration.ItemClasses!.Where(c => c.Id != classId)],
        })));

        Assert.Equal("invalid_configuration", refused.Code);
        Assert.Contains("Hose fittings", refused.Message);
        Assert.Contains(item.Number, refused.Message);
    }

    [Fact]
    public void Item_classes_inherit_attributes_and_cycles_are_reported()
    {
        var configuration = DefaultConfiguration.Create();
        var cyclic = configuration with { ItemClasses = [new ItemClass("a", "A", "b"), new ItemClass("b", "B", "a")] };

        Assert.Contains(configuration.ItemAttributesFor("sheet-metal"), a => a.Name == "Material");
        Assert.Equal("Mechanical / Fasteners", configuration.ItemClassPath("fasteners"));
        Assert.Empty(configuration.Validate());
        Assert.Contains(cyclic.Validate(), p => p.Contains("circle"));
    }
}
