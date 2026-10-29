using Openhold.Core.Contracts;

namespace Openhold.Core.Tests;

public sealed class CataloguePropertiesTests
{
    private static ItemInfo Item(params ManufacturerPart[] parts) => new(
        Guid.NewGuid(), "P-100001-5", "Contactor 9 A", "each", ItemKinds.Bought, null, null, ItemStates.Released,
        new Dictionary<string, string>(), null, parts, [], [], "admin", DateTime.UtcNow, DateTime.UtcNow, null, null, 1);

    [Fact]
    public void A_bought_part_fills_vendor_and_stock_number_from_its_preferred_manufacturer()
    {
        var values = CatalogueProperties.For(Item(new("ABB", "AF09-30-10"), new("Schneider Electric", "LC1D09BD", Preferred: true)));

        Assert.Equal("P-100001-5", values["Part Number"]);
        Assert.Equal("Contactor 9 A", values["Description"]);
        Assert.Equal(("Schneider Electric", "LC1D09BD"), (values["Vendor"], values["Stock Number"]));
    }

    [Fact]
    public void Only_values_that_differ_are_written_and_a_made_part_has_no_vendor()
    {
        var changes = CatalogueProperties.Changes(Item(), new Dictionary<string, string> { ["Part Number"] = "P-100001-5", ["Description"] = "old text" });

        Assert.Equal(["Description"], changes.Keys);
        Assert.DoesNotContain("Vendor", CatalogueProperties.For(Item()).Keys);
    }
}
