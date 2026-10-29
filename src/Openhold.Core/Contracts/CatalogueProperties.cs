namespace Openhold.Core.Contracts;

/// <summary>
/// The file properties a catalogue item fills in (iProperties in Inventor files, mapped title-block fields in drawings),
/// so a part's model or drawing carries the same number, description and supplier details as the catalogue.
/// </summary>
public static class CatalogueProperties
{
    public static IReadOnlyDictionary<string, string> For(ItemInfo item)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Part Number"] = item.Number,
            ["Description"] = item.Description,
        };
        // Inventor's Vendor and Stock Number iProperties: who makes the part, and their part number.
        if ((item.ManufacturerParts.FirstOrDefault(p => p.Preferred) ?? item.ManufacturerParts.FirstOrDefault()) is { } preferred)
        {
            values["Vendor"] = preferred.Manufacturer;
            values["Stock Number"] = preferred.PartNumber;
        }
        return values;
    }

    /// <summary>The values that differ from what the file has now; empty when it already matches.</summary>
    public static IReadOnlyDictionary<string, string> Changes(ItemInfo item, IReadOnlyDictionary<string, string> current) =>
        For(item).Where(v => !string.Equals(current.GetValueOrDefault(v.Key)?.Trim(), v.Value, StringComparison.Ordinal))
            .ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
}
