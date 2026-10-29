using System.Globalization;
using Inventor;
using Openhold.Client.Sdk;
using Openhold.Core.Properties;
using Path = System.IO.Path;

namespace Openhold.Client.Cad;

/// <summary>A file a CAD file uses: the path as stored in the file, and where that resolves to.</summary>
public sealed record LegacyReference(string StoredPath, string FullPath);

/// <summary>What importing needs to know about one file: what it uses, its properties, and anything that went wrong.</summary>
public sealed record LegacyFileFacts(IReadOnlyList<LegacyReference> References, IReadOnlyDictionary<string, string> Properties, IReadOnlyList<string> Problems)
{
    public static LegacyFileFacts None { get; } = new([], new Dictionary<string, string>(), []);
}

/// <summary>
/// Reads references and properties from files being brought into Openhold, without changing them: Inventor files
/// with Apprentice, AutoCAD drawings with AutoCAD's headless console and the Openhold plugin (title blocks are mapped
/// to properties with the PC's title block settings). Files of other kinds have no references.
/// </summary>
public sealed class LegacyReader(TitleBlockSettings titleBlock, string? pluginPath = null)
{
    private static readonly HashSet<string> InventorExtensions = new(StringComparer.OrdinalIgnoreCase) { ".ipt", ".iam", ".idw", ".ipn" };

    public bool CanReadInventor => Apprentice.Installed;

    public bool CanReadDrawings => AccoreConsole.Plugin(pluginPath) is not null;

    public LegacyFileFacts Read(string path)
    {
        var extension = Path.GetExtension(path);
        try
        {
            if (InventorExtensions.Contains(extension))
                return CanReadInventor ? ReadInventor(path) : LegacyFileFacts.None with { Problems = ["Inventor (Apprentice) isn't installed, so its references weren't read."] };
            if (string.Equals(extension, ".dwg", StringComparison.OrdinalIgnoreCase))
                return CanReadDrawings ? ReadDrawing(path) : LegacyFileFacts.None with { Problems = ["AutoCAD isn't installed, so its xrefs weren't read."] };
            return LegacyFileFacts.None;
        }
        catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return LegacyFileFacts.None with { Problems = [$"Couldn't read it: {ex.Message}"] };
        }
    }

    private static LegacyFileFacts ReadInventor(string path) =>
        Apprentice.Run($"read {Path.GetFileName(path)}", apprentice =>
        {
            var document = apprentice.Open(path);
            try
            {
                var references = new List<LegacyReference>();
                foreach (FileDescriptor descriptor in document.File.ReferencedFileDescriptors)
                {
                    if (!string.IsNullOrWhiteSpace(descriptor.FullFileName))
                        references.Add(new LegacyReference(descriptor.FullFileName, Path.GetFullPath(descriptor.FullFileName)));
                }

                var properties = InventorPropertyReader.Read(document.PropertySets);

                var problems = new List<string>();
                if (document.NeedsMigrating)
                    problems.Add("Saved by an older Inventor; it will be migrated when next opened and saved.");
                return new LegacyFileFacts(references, properties, problems);
            }
            finally
            {
                document.Close();
            }
        });

    private LegacyFileFacts ReadDrawing(string path)
    {
        var index = AccoreConsole.Run<DrawingIndex>(path, AccoreConsole.Plugin(pluginPath)!, "OPENHOLDINDEX", "OPENHOLD_INDEX", new { }, "reading");
        var folder = Path.GetDirectoryName(path)!;
        var references = index.References
            .Where(r => !string.IsNullOrWhiteSpace(r.StoredPath))
            .Select(r => new LegacyReference(r.StoredPath, Path.GetFullPath(Path.IsPathRooted(r.StoredPath) ? r.StoredPath : Path.Combine(folder, r.StoredPath))))
            .ToList();
        var reading = new TitleBlockMapping(titleBlock.BlockNamePattern, titleBlock.Attributes, titleBlock.SheetTitleAttribute)
            .Read(index.AttributedBlocks.Select(b => new BlockAttributes(b.BlockName, b.Layout, b.Attributes)));
        var properties = new Dictionary<string, string>(reading.Properties, StringComparer.OrdinalIgnoreCase);
        if (reading.Sheets.Count > 0)
            properties["Sheet Count"] = reading.Sheets.Count.ToString(CultureInfo.InvariantCulture);
        return new LegacyFileFacts(references, properties, reading.Mismatches.Select(m => m.Describe()).ToList());
    }

    // The JSON the plugin's OPENHOLDINDEX command writes.
    private sealed record DrawingIndex(string File, IReadOnlyList<DrawingReferenceJson> References, IReadOnlyList<AttributedBlockJson> AttributedBlocks);

    private sealed record DrawingReferenceJson(string Kind, string Name, string StoredPath, bool Unloaded);

    private sealed record AttributedBlockJson(string BlockName, string Layout, Dictionary<string, string> Attributes);
}
