using System.Globalization;

namespace Openhold.Core.Properties;

/// <summary>How an iProperty's value is typed in and shown.</summary>
public enum IPropertyKind
{
    Text,
    Date,
    Currency,
    /// <summary>One of a fixed list (e.g. Design State), stored by Inventor as a number.</summary>
    Choice,
    /// <summary>Worked out by Inventor (mass, area, volume…): shown, never typed in.</summary>
    Calculated,
}

/// <summary>
/// One iProperty as Inventor's iProperties dialog shows it: <see cref="Name"/> is what Openhold calls it (Inventor's own
/// label), <see cref="Tab"/> the dialog tab, and <see cref="Set"/>/<see cref="Internal"/> where Inventor stores it.
/// </summary>
public sealed record IPropertyDefinition(
    string Name, string Tab, string Set, string Internal, IPropertyKind Kind = IPropertyKind.Text, bool Editable = true,
    IReadOnlyList<(string Label, int Value)>? Choices = null, string? Unit = null, double Scale = 1);

/// <summary>
/// Inventor's iProperties, all in one place, tab by tab as in Inventor's iProperties dialog: what the add-in records at
/// check-in, what Openhold's iProperties editor offers, what the importer reads and what the writer writes. Custom
/// iProperties (Inventor's Custom tab) are kept under their own names alongside these.
/// </summary>
public static class InventorProperties
{
    public const string SummarySet = "Inventor Summary Information";
    public const string DocumentSummarySet = "Inventor Document Summary Information";
    public const string DesignTrackingSet = "Design Tracking Properties";
    public const string CustomSet = "Inventor User Defined Properties";

    public static IReadOnlyList<IPropertyDefinition> All { get; } =
    [
        // Summary
        new("Title", "Summary", SummarySet, "Title"),
        new("Subject", "Summary", SummarySet, "Subject"),
        new("Author", "Summary", SummarySet, "Author"),
        new("Manager", "Summary", DocumentSummarySet, "Manager"),
        new("Company", "Summary", DocumentSummarySet, "Company"),
        new("Category", "Summary", DocumentSummarySet, "Category"),
        new("Keywords", "Summary", SummarySet, "Keywords"),
        new("Comments", "Summary", SummarySet, "Comments"),
        // Project ("Revision Number" is "Revision" in Openhold, as for title blocks and revision schemes)
        new("Part Number", "Project", DesignTrackingSet, "Part Number"),
        new("Stock Number", "Project", DesignTrackingSet, "Stock Number"),
        new("Description", "Project", DesignTrackingSet, "Description"),
        new("Revision", "Project", SummarySet, "Revision Number"),
        new("Project", "Project", DesignTrackingSet, "Project"),
        new("Designer", "Project", DesignTrackingSet, "Designer"),
        new("Engineer", "Project", DesignTrackingSet, "Engineer"),
        new("Authority", "Project", DesignTrackingSet, "Authority"),
        new("Cost Center", "Project", DesignTrackingSet, "Cost Center"),
        new("Estimated Cost", "Project", DesignTrackingSet, "Cost", IPropertyKind.Currency),
        new("Creation Date", "Project", DesignTrackingSet, "Creation Time", IPropertyKind.Date),
        new("Vendor", "Project", DesignTrackingSet, "Vendor"),
        new("WEB Link", "Project", DesignTrackingSet, "Catalog Web Link"),
        new("File Subtype", "Project", DesignTrackingSet, "Document SubType Name", IPropertyKind.Calculated, Editable: false),
        // Status
        new("Design State", "Status", DesignTrackingSet, "Design Status", IPropertyKind.Choice,
            Choices: [("Work In Progress", 1), ("Pending", 2), ("Released", 3)]),
        new("Checked By", "Status", DesignTrackingSet, "Checked By"),
        new("Checked Date", "Status", DesignTrackingSet, "Date Checked", IPropertyKind.Date),
        new("Eng. Approved By", "Status", DesignTrackingSet, "Engr Approved By"),
        new("Eng. Approved Date", "Status", DesignTrackingSet, "Engr Date Approved", IPropertyKind.Date),
        new("Mfg. Approved By", "Status", DesignTrackingSet, "Mfg Approved By"),
        new("Mfg. Approved Date", "Status", DesignTrackingSet, "Mfg Date Approved", IPropertyKind.Date),
        // Physical: worked out by Inventor from the geometry and material. Stored as grams, cm², cm³ and g/cm³ (checked
        // against a real part: volume × density = mass); shown as kg, mm², mm³ and g/cm³.
        new("Material", "Physical", DesignTrackingSet, "Material", IPropertyKind.Calculated, Editable: false),
        new("Mass", "Physical", DesignTrackingSet, "Mass", IPropertyKind.Calculated, Editable: false, Unit: "kg", Scale: 0.001),
        new("Area", "Physical", DesignTrackingSet, "SurfaceArea", IPropertyKind.Calculated, Editable: false, Unit: "mm²", Scale: 100),
        new("Volume", "Physical", DesignTrackingSet, "Volume", IPropertyKind.Calculated, Editable: false, Unit: "mm³", Scale: 1000),
        new("Density", "Physical", DesignTrackingSet, "Density", IPropertyKind.Calculated, Editable: false, Unit: "g/cm³"),
    ];

    /// <summary>The definition with this Openhold name, or with this Inventor internal name (as older versions recorded).</summary>
    public static IPropertyDefinition? Find(string name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? All.FirstOrDefault(p => string.Equals(p.Internal, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The definition stored in this set under this internal name, if it's one of the standard iProperties.</summary>
    public static IPropertyDefinition? Stored(string set, string internalName) =>
        All.FirstOrDefault(p => p.Set == set && string.Equals(p.Internal, internalName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The name to show and record a property under: the standard name for a standard iProperty, else as given.</summary>
    public static string Canonical(string name) => Find(name)?.Name ?? name;

    /// <summary>A value read from Inventor, as text: choices by their label, calculated values in their unit.</summary>
    public static string? Format(IPropertyDefinition definition, object? raw)
    {
        if (definition.Kind == IPropertyKind.Choice && raw is IConvertible)
        {
            var number = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
            return definition.Choices?.FirstOrDefault(c => c.Value == number).Label;
        }
        if (definition.Unit is { } unit && raw is IConvertible && raw is not string)
        {
            var value = Convert.ToDouble(raw, CultureInfo.InvariantCulture) * definition.Scale;
            return value == 0 ? null : $"{value.ToString("0.###", CultureInfo.InvariantCulture)} {unit}";
        }
        if (definition.Kind == IPropertyKind.Currency && raw is IConvertible && raw is not string)
        {
            var value = Convert.ToDecimal(raw, CultureInfo.InvariantCulture);
            return value == 0 ? null : value.ToString("0.00", CultureInfo.InvariantCulture);
        }
        return PropertyValues.Format(raw);
    }

    /// <summary>The number Inventor stores for a choice typed or picked as its label (e.g. "Released" → 3).</summary>
    public static int? ChoiceValue(IPropertyDefinition definition, string label) =>
        definition.Choices?.FirstOrDefault(c => string.Equals(c.Label, label.Trim(), StringComparison.OrdinalIgnoreCase)) is { Label: not null } choice
            ? choice.Value
            : null;
}
