using Openhold.Client.Sdk;
using Openhold.Client.Sdk.Workspace;
using Path = System.IO.Path;

namespace Openhold.Client.Cad;

/// <summary>
/// Writes properties into DWG title blocks without opening AutoCAD: runs the installed Openhold AutoCAD plugin's
/// OPENHOLDWRITEPROPS command in accoreconsole. Properties map to attribute tags through the title-block
/// settings, the same mapping check-in reads them with.
/// </summary>
/// <param name="pluginPath">The plugin DLL to run; by default the one installed for AutoCAD.</param>
public sealed class AutoCadTitleBlockWriter(TitleBlockSettings settings, string? pluginPath = null) : IPropertyWriter
{
    private readonly string? _plugin = AccoreConsole.Plugin(pluginPath);

    public bool CanWrite(string path) => Path.GetExtension(path).Equals(".dwg", StringComparison.OrdinalIgnoreCase) && _plugin is not null;

    public string StoredAs => "Title block";

    /// <summary>Only properties mapped to a title-block attribute go into the drawing.</summary>
    public bool CanHold(string path, string property) => settings.Attributes.Values.Contains(property, StringComparer.OrdinalIgnoreCase);

    public void Write(string path, IReadOnlyDictionary<string, string> values)
    {
        var name = Path.GetFileName(path);
        // A property may be shown in more than one attribute; each one gets the value.
        var tagsOf = settings.Attributes.GroupBy(a => a.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(a => a.Key).ToList(), StringComparer.OrdinalIgnoreCase);
        var unmapped = values.Keys.Where(k => !tagsOf.ContainsKey(k)).ToList();
        if (unmapped.Count > 0)
        {
            throw new IOException(
                $"{name}: {string.Join(", ", unmapped)} {(unmapped.Count == 1 ? "isn't a title-block field" : "aren't title-block fields")}, " +
                "so a drawing can't hold it. Ask your Openhold admin to map it to a title-block attribute.");
        }
        AccoreConsole.RejectIfOpen(path);

        var request = new { settings.BlockNamePattern, Attributes = values.SelectMany(v => tagsOf[v.Key].Select(tag => (tag, v.Value))).ToDictionary(t => t.tag, t => t.Value) };
        var result = AccoreConsole.Run<WriteResult>(path, _plugin!, "OPENHOLDWRITEPROPS", "OPENHOLD_PROPS", request, "writing the title block of");
        if (result.TitleBlocks == 0)
            throw new IOException($"{name} has no title block matching \"{settings.BlockNamePattern}\", so there's nowhere to write the properties.");
        if (result.MissingTags.Count > 0)
            throw new IOException($"{name}'s title block has no {string.Join(", ", result.MissingTags)} attribute, so nothing was changed.");
    }

    private sealed record WriteResult(int Updated, int TitleBlocks, IReadOnlyList<string> MissingTags);
}
