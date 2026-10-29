using System.Text.RegularExpressions;

namespace Openhold.Core.Properties;

/// <summary>An attributed block instance read from a drawing.</summary>
public sealed record BlockAttributes(string BlockName, string Layout, IReadOnlyDictionary<string, string> Attributes);

public sealed record DrawingSheet(int Index, string Layout, string? Title)
{
    public string Label => Title is { Length: > 0 } ? $"sheet {Index} ({Title})" : $"sheet {Index}";
}

/// <summary>One value of a disagreeing attribute and the sheets that show it.</summary>
public sealed record MismatchedValue(string Value, IReadOnlyList<DrawingSheet> Sheets);

/// <summary>A title-block attribute that disagrees between sheets of the same drawing. Values are ordered most common first.</summary>
public sealed record AttributeMismatch(string Property, string Attribute, IReadOnlyList<MismatchedValue> Occurrences)
{
    public IReadOnlyList<string> Values => Occurrences.Select(o => o.Value).ToList();

    /// <summary>Plain-language description, e.g. "Revision: 32 sheets show 1B, but sheet 10 (WIRING SPECIFICATION) shows 0."</summary>
    public string Describe()
    {
        var majority = Occurrences[0];
        var others = Occurrences.Skip(1).Select(o =>
            o.Sheets.Count <= 3
                ? $"{string.Join(", ", o.Sheets.Select(s => s.Label))} {(o.Sheets.Count == 1 ? "shows" : "show")} {o.Value}"
                : $"{o.Sheets.Count} sheets show {o.Value}");
        var majorityText = majority.Sheets.Count == 1 ? $"1 sheet shows {majority.Value}" : $"{majority.Sheets.Count} sheets show {majority.Value}";
        return $"{Property}: {majorityText}, but {string.Join("; ", others)}.";
    }
}

public sealed record TitleBlockReading(
    IReadOnlyDictionary<string, string> Properties,
    IReadOnlyList<DrawingSheet> Sheets,
    IReadOnlyList<AttributeMismatch> Mismatches);

/// <summary>
/// Maps title-block attributes to vault properties for DWGs. A drawing may hold many sheets, each with
/// its own title block (in model space or on layouts). Drawing-level properties such as revision take the
/// value shared by every sheet. Disagreement between sheets is reported rather than silently resolved,
/// because a sheet left on an old revision is a real error worth catching before release.
/// </summary>
public sealed class TitleBlockMapping
{
    private readonly Regex _blockName;

    /// <param name="blockNamePattern">Regex matching the company title block name, e.g. <c>^Title Blocks TB-A3$</c>.</param>
    /// <param name="attributeToProperty">Title-block attribute tag → vault property name. Tags match case-insensitively.</param>
    /// <param name="sheetTitleAttribute">Attribute holding each sheet's title, e.g. <c>TITLE</c>.</param>
    public TitleBlockMapping(string blockNamePattern, IReadOnlyDictionary<string, string> attributeToProperty, string? sheetTitleAttribute = null)
    {
        _blockName = new Regex(blockNamePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        AttributeToProperty = new Dictionary<string, string>(attributeToProperty, StringComparer.OrdinalIgnoreCase);
        SheetTitleAttribute = sheetTitleAttribute;
    }

    public IReadOnlyDictionary<string, string> AttributeToProperty { get; }

    public string? SheetTitleAttribute { get; }

    public TitleBlockReading Read(IEnumerable<BlockAttributes> blocks)
    {
        var titleBlocks = blocks.Where(b => _blockName.IsMatch(b.BlockName)).ToList();
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var mismatches = new List<AttributeMismatch>();

        var sheets = titleBlocks
            .Select((b, i) => new DrawingSheet(i + 1, b.Layout,
                SheetTitleAttribute is not null && b.Attributes.TryGetValue(SheetTitleAttribute, out var title) ? title.Trim() : null))
            .ToList();

        foreach (var (attribute, property) in AttributeToProperty)
        {
            var occurrences = titleBlocks
                .Select((b, i) => (Sheet: sheets[i], Value: b.Attributes.TryGetValue(attribute, out var v) ? v.Trim() : ""))
                .Where(x => x.Value.Length > 0)
                .GroupBy(x => x.Value, StringComparer.Ordinal)
                .Select(g => new MismatchedValue(g.Key, g.Select(x => x.Sheet).ToList()))
                .OrderByDescending(o => o.Sheets.Count)
                .ToList();

            if (occurrences.Count == 1)
                properties[property] = occurrences[0].Value;
            else if (occurrences.Count > 1)
                mismatches.Add(new AttributeMismatch(property, attribute, occurrences));
        }

        return new TitleBlockReading(properties, sheets, mismatches);
    }
}
