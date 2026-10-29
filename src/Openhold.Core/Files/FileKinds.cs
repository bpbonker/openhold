namespace Openhold.Core.Files;

/// <summary>Classifies files by extension for vault policy (unique names, versioning, project sets).</summary>
public static class FileKinds
{
    /// <summary>
    /// CAD files resolved by name through project search paths. Duplicate names among these let
    /// Inventor/AutoCAD silently open the wrong file, so their names must be unique across the vault.
    /// </summary>
    private static readonly HashSet<string> UniqueNameExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ipt", ".iam", ".idw", ".dwg", ".ipn", ".ide", ".wdp", ".dwt",
    };

    /// <summary>Scratch, lock and build-output files that tools regenerate; never versioned.</summary>
    private static readonly HashSet<string> IgnoredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // AutoCAD / Inventor / general
        ".bak", ".dwl", ".dwl2", ".lck", ".tmp", ".sv$", ".ac$", ".log",
        // CODESYS: lock file while a project is open, per-user options, and compile/download output
        ".~u", ".opt", ".compileinfo", ".bootinfo", ".bootinfo_guids", ".precompilecache",
    };

    // OldVersions: Inventor backup copies. _V: Autodesk Vault visualization (DWF) files.
    private static readonly string[] IgnoredDirectoryNames = ["OldVersions", "_V"];

    public static bool RequiresUniqueName(string fileName) => UniqueNameExtensions.Contains(Path.GetExtension(fileName));

    public static bool IsIgnored(string path)
    {
        // Paths come from Windows clients, so both separators count wherever this runs.
        var name = path[(path.LastIndexOfAny(['\\', '/']) + 1)..];
        if (name.StartsWith("~$", StringComparison.Ordinal) || IgnoredExtensions.Contains(Path.GetExtension(name)))
            return true;
        var segments = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(s => IgnoredDirectoryNames.Contains(s, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// CAD files whose vault properties are read from the file itself (iProperties, title blocks), so editing them
    /// must write into the file, or the next check-in from Inventor/AutoCAD would put the old values back.
    /// </summary>
    public static bool HasEmbeddedProperties(string fileName) => EmbeddedPropertyExtensions.Contains(Path.GetExtension(fileName));

    private static readonly HashSet<string> EmbeddedPropertyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ipt", ".iam", ".idw", ".ipn", ".ide", ".dwg",
    };

    /// <summary>AutoCAD Electrical project definition; checked out and in as a set with its drawings.</summary>
    public static bool IsAcadeProject(string fileName) =>
        Path.GetExtension(fileName).Equals(".wdp", StringComparison.OrdinalIgnoreCase);
}
