using Openhold.Core.Properties;

namespace Openhold.Core.Tests;

public class TitleBlockMappingTests
{
    private static readonly TitleBlockMapping Mapping = new(
        "^Title Blocks TB-A3$",
        new Dictionary<string, string>
        {
            ["REVISION_NUMBER"] = "Revision",
            ["Sheet_Size"] = "Sheet Size",
            ["PART_NUMBER"] = "Part Number",
        },
        sheetTitleAttribute: "TITLE");

    private static BlockAttributes Sheet(string title, string revision, string size = "A3", string partNumber = "") =>
        new("Title Blocks TB-A3", "Model", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TITLE"] = title,
            ["REVISION_NUMBER"] = revision,
            ["SHEET_SIZE"] = size,
            ["PART_NUMBER"] = partNumber,
        });

    [Fact]
    public void Shared_values_become_drawing_properties_and_titles_become_sheets()
    {
        var reading = Mapping.Read([
            Sheet("TITLE PAGE", "1B"),
            Sheet("INCOMER AND PLC", "1B"),
            new BlockAttributes("CONTACTOR_COIL", "Model", new Dictionary<string, string> { ["TAG"] = "K1" }),
            Sheet("OPERATOR INTERFACE", "1B"),
        ]);

        Assert.Equal("1B", reading.Properties["Revision"]);
        Assert.Equal("A3", reading.Properties["Sheet Size"]);
        Assert.False(reading.Properties.ContainsKey("Part Number")); // blank on every sheet
        Assert.Equal(["TITLE PAGE", "INCOMER AND PLC", "OPERATOR INTERFACE"], reading.Sheets.Select(s => s.Title));
        Assert.Empty(reading.Mismatches);
    }

    [Fact]
    public void Sheet_left_on_old_revision_is_reported()
    {
        var reading = Mapping.Read([Sheet("TITLE PAGE", "1B"), Sheet("INCOMER AND PLC", "1A"), Sheet("OPERATOR INTERFACE", "1B")]);

        var mismatch = Assert.Single(reading.Mismatches);
        Assert.Equal("Revision", mismatch.Property);
        Assert.Equal(["1B", "1A"], mismatch.Values);
        Assert.Equal("Revision: 2 sheets show 1B, but sheet 2 (INCOMER AND PLC) shows 1A.", mismatch.Describe());
        Assert.False(reading.Properties.ContainsKey("Revision"));
    }
}
