using System.Text.RegularExpressions;
using Openhold.Core.Configuration;
using Openhold.Core.Contracts;

namespace Openhold.Core.Numbering;

/// <summary>
/// Works out how a new file should be named from where it is saved: the category its folder and type give it, that
/// category's numbering scheme, and the scheme's fields filled in from the folders above it and the assembly it is
/// made in. Shared by the CAD add-ins, Openhold Explorer and the server, so all three agree.
/// </summary>
public static class NamingRules
{
    /// <summary>The code a folder stands for: the text before " - " ("02 - Frame" gives 02), or null when it has none.</summary>
    public static string? FolderCode(string folderName)
    {
        var dash = folderName.IndexOf(" - ", StringComparison.Ordinal);
        return dash > 0 ? folderName[..dash].Trim() : null;
    }

    /// <summary>The number a file's name carries: without its extension and any " - description" ("P-100004-4 - IFS240.stp" gives P-100004-4).</summary>
    public static string NumberOf(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var dash = stem.IndexOf(" - ", StringComparison.Ordinal);
        return (dash > 0 ? stem[..dash] : stem).Trim();
    }

    /// <summary>True when the file's name (without " - description") is a number the scheme could have issued.</summary>
    public static bool Follows(NumberingScheme scheme, string fileName) =>
        NumberTemplate.Parse(scheme.Template).NamePattern(scheme.Fields).IsMatch(NumberOf(fileName));

    /// <summary>How a new file saved in this folder, under this parent assembly (if any), would be named.</summary>
    public static NamingSuggestion Suggest(VaultConfiguration configuration, IReadOnlyList<NumberingScheme> schemes,
        string folderPath, string fileName, string? parentFileName = null, string? schemeId = null)
    {
        var category = configuration.CategoryForNewFile(folderPath, fileName);
        var ruleScheme = !string.IsNullOrWhiteSpace(parentFileName) && !string.IsNullOrWhiteSpace(category?.ChildNumberingSchemeId)
            ? category.ChildNumberingSchemeId
            : category?.NumberingSchemeId;
        var scheme = schemes.FirstOrDefault(s => string.Equals(s.Id, schemeId ?? ruleScheme, StringComparison.OrdinalIgnoreCase));
        var fields = scheme is null ? [] : Fill(scheme, folderPath, parentFileName);
        return new NamingSuggestion(category?.Id, category?.Name, scheme, fields,
            category?.EnforceNaming == true && scheme is not null && SchemesOf(category).Contains(scheme.Id, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The schemes a category's new files may be named by: its own and, if set, the one for files made inside an assembly.</summary>
    public static IReadOnlyList<string> SchemesOf(CategoryDefinition category) =>
        new[] { category.NumberingSchemeId, category.ChildNumberingSchemeId }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList();

    /// <summary>
    /// The scheme's fields, in template order, filled in where possible: folder fields from the folders on the path
    /// (top-down, each run of side-by-side fields from the first folder below the previous match whose code fits),
    /// parent fields from the parent's number, then defaults.
    /// </summary>
    public static IReadOnlyList<SuggestedField> Fill(NumberingScheme scheme, string folderPath, string? parentFileName)
    {
        var template = NumberTemplate.Parse(scheme.Template);
        NumberingField Definition(string name) =>
            scheme.Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) ?? new NumberingField(name);
        var values = new Dictionary<string, (string Value, string Source)>(StringComparer.OrdinalIgnoreCase);

        // Folder fields. Runs are matched in template order against the folders from the top, so the hierarchy of
        // the folders (machine, then area) lines up with the order of the number (machine code, then area code).
        var codes = folderPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(f => f != "$").Select(FolderCode).ToList();
        var next = 0;
        foreach (var run in template.FieldRuns())
        {
            var folderFields = run.Where(name => Definition(name).From == NumberingSources.Folder).ToList();
            if (folderFields.Count == 0)
                continue;
            var pattern = new Regex("^" + string.Concat(folderFields.Select(name => $"({NumberTemplate.FieldPattern(Definition(name))})")) + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            for (var i = next; i < codes.Count; i++)
            {
                if (codes[i] is not { } code || pattern.Match(code) is not { Success: true } match)
                    continue;
                for (var g = 0; g < folderFields.Count; g++)
                    values[folderFields[g]] = (AsWritten(Definition(folderFields[g]), match.Groups[g + 1].Value), NumberingSources.Folder);
                next = i + 1;
                break;
            }
        }

        foreach (var name in template.FieldNames)
        {
            var field = Definition(name);
            if (values.ContainsKey(name))
                continue;
            if (field.From == NumberingSources.Parent && !string.IsNullOrWhiteSpace(parentFileName))
                values[name] = (NumberOf(parentFileName), NumberingSources.Parent);
            else if (!string.IsNullOrWhiteSpace(field.Default))
                values[name] = (field.Default.Trim(), "default");
        }

        return template.FieldNames.Select(name => values.TryGetValue(name, out var v)
            ? new SuggestedField(Definition(name), v.Value, v.Source)
            : new SuggestedField(Definition(name), null, null)).ToList();
    }

    /// <summary>In a few words, where a field's value comes from, e.g. "from the folders" or "picked, else 00".</summary>
    public static string Describe(NumberingField field) => field.From switch
    {
        NumberingSources.Folder => "from the folders",
        NumberingSources.Parent => "from the parent assembly",
        _ => field.Choices is { Count: > 0 } ? "picked" : "typed",
    } + (string.IsNullOrWhiteSpace(field.Default) ? "" : $", else {field.Default}");

    private static string AsWritten(NumberingField field, string value) =>
        field.Choices?.FirstOrDefault(c => string.Equals(c.Code, value, StringComparison.OrdinalIgnoreCase))?.Code ?? value;
}
