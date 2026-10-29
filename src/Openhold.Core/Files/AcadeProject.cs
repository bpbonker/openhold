namespace Openhold.Core.Files;

public sealed record AcadeDrawing(string Path, string? Description, string? Subfolder);

/// <summary>
/// An AutoCAD Electrical project (<c>.wdp</c>) and the files that make up its project set.
/// Project-wide operations (wire numbering, tag updates, title block updates) touch many drawings at
/// once, so the whole set is checked out, checked in and fetched as one batch.
/// </summary>
/// <remarks>
/// Format: <c>*[n]</c> description lines, <c>+[n]</c> settings, <c>===text</c> describes the next
/// drawing, <c>=====SUB=name</c> sets the next drawing's subfolder, and every other non-blank line is a
/// drawing path (absolute, or relative to the <c>.wdp</c>'s folder).
/// </remarks>
public sealed class AcadeProject
{
    /// <summary>Per-project files that sit beside the .wdp with the same stem.</summary>
    private static readonly string[] CompanionSuffixes = [".wdt", ".wdl", "_wdtitle.wdl", ".aepx", "_wdpinlist.wdl", ".wdi"];

    private AcadeProject(string projectPath, IReadOnlyList<string> description, IReadOnlyList<AcadeDrawing> drawings)
    {
        ProjectPath = projectPath;
        Description = description;
        Drawings = drawings;
    }

    public string ProjectPath { get; }

    public IReadOnlyList<string> Description { get; }

    /// <summary>Drawings in project (sheet) order, with absolute paths.</summary>
    public IReadOnlyList<AcadeDrawing> Drawings { get; }

    public static AcadeProject Load(string wdpPath)
    {
        var fullPath = System.IO.Path.GetFullPath(wdpPath);
        return Parse(fullPath, File.ReadAllLines(fullPath));
    }

    public static AcadeProject Parse(string wdpPath, IEnumerable<string> lines)
    {
        var projectDirectory = System.IO.Path.GetDirectoryName(wdpPath)!;
        var description = new List<string>();
        var drawings = new List<AcadeDrawing>();
        string? pendingDescription = null;
        string? pendingSubfolder = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith("+[", StringComparison.Ordinal))
                continue;

            if (line.StartsWith("*[", StringComparison.Ordinal))
            {
                var close = line.IndexOf(']');
                description.Add(close >= 0 ? line[(close + 1)..] : line);
            }
            else if (line.StartsWith("=====SUB=", StringComparison.Ordinal))
            {
                pendingSubfolder = line["=====SUB=".Length..];
            }
            else if (line.StartsWith("===", StringComparison.Ordinal))
            {
                pendingDescription = line[3..];
            }
            else
            {
                var path = line.Replace('\\', System.IO.Path.DirectorySeparatorChar).Replace('/', System.IO.Path.DirectorySeparatorChar);
                var absolute = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(projectDirectory, path));
                drawings.Add(new AcadeDrawing(absolute, pendingDescription, pendingSubfolder));
                pendingDescription = null;
                pendingSubfolder = null;
            }
        }

        return new AcadeProject(wdpPath, description, drawings);
    }

    /// <summary>
    /// Every file in the project set that exists on disk: the .wdp, its companion files and its drawings.
    /// Drawings listed in the project but missing on disk are reported by <see cref="MissingDrawings"/>.
    /// </summary>
    public IReadOnlyList<string> ProjectSetFiles()
    {
        var directory = System.IO.Path.GetDirectoryName(ProjectPath)!;
        var stem = System.IO.Path.GetFileNameWithoutExtension(ProjectPath);
        var files = new List<string> { ProjectPath };
        files.AddRange(CompanionSuffixes
            .Select(suffix => System.IO.Path.Combine(directory, stem + suffix))
            .Where(File.Exists));
        files.AddRange(Drawings.Select(d => d.Path).Where(File.Exists));
        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<string> MissingDrawings() => Drawings.Select(d => d.Path).Where(p => !File.Exists(p)).ToList();
}
