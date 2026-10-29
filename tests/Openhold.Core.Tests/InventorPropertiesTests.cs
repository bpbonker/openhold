using Openhold.Core.Properties;

namespace Openhold.Core.Tests;

public sealed class InventorPropertiesTests
{
    [Fact]
    public void Every_tab_of_inventors_dialog_is_covered_once()
    {
        Assert.Equal(["Summary", "Project", "Status", "Physical"], InventorProperties.All.Select(p => p.Tab).Distinct());
        Assert.Equal(InventorProperties.All.Count, InventorProperties.All.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(InventorProperties.All, p => p.Name == "Estimated Cost" && p.Internal == "Cost" && p.Editable);
        Assert.All(InventorProperties.All.Where(p => p.Tab == "Physical"), p => Assert.False(p.Editable));
    }

    [Fact]
    public void Older_records_under_internal_names_find_their_dialog_name()
    {
        Assert.Equal("Eng. Approved By", InventorProperties.Canonical("Engr Approved By"));
        Assert.Equal("WEB Link", InventorProperties.Canonical("Catalog Web Link"));
        Assert.Equal("Revision", InventorProperties.Canonical("Revision Number"));
        Assert.Equal("My custom", InventorProperties.Canonical("My custom"));
    }

    [Fact]
    public void Values_are_shown_as_inventor_shows_them()
    {
        var state = InventorProperties.Find("Design State")!;
        Assert.Equal("Released", InventorProperties.Format(state, 3));
        Assert.Equal(2, InventorProperties.ChoiceValue(state, "pending"));
        Assert.Null(InventorProperties.ChoiceValue(state, "Approved"));
        // Inventor stores mass in grams, area in cm² and volume in cm³ (volume × density = mass).
        Assert.Equal("1.547 kg", InventorProperties.Format(InventorProperties.Find("Mass")!, 1546.88));
        Assert.Equal("140542.405 mm²", InventorProperties.Format(InventorProperties.Find("Area")!, 1405.42405));
        Assert.Equal("196679.462 mm³", InventorProperties.Format(InventorProperties.Find("Volume")!, 196.679462));
        Assert.Equal("1250.50", InventorProperties.Format(InventorProperties.Find("Estimated Cost")!, 1250.5m));
        Assert.Null(InventorProperties.Format(InventorProperties.Find("Estimated Cost")!, 0m));
    }
}
