using System.Text;
using Openhold.Core.Files;

namespace Openhold.Client.Sdk.Workspace;

/// <summary>
/// Rewrites a parent file's reference from a child's old location to its new one, so renaming or moving a
/// file never breaks the files that use it. Implementations exist per file type (Inventor through Apprentice,
/// AutoCAD Electrical projects as text).
/// </summary>
public interface IReferenceRepairer
{
    bool CanRepair(string parentPath);

    /// <summary>Repoints references in <paramref name="parentPath"/>. Returns true if the parent was changed.</summary>
    bool Repair(string parentPath, string oldChildPath, string newChildPath);
}

/// <summary>
/// Repairs an AutoCAD Electrical project (<c>.wdp</c>) when one of its drawings is renamed or moved: the drawing's
/// line is rewritten relative to the project, keeping the file's encoding and every other line unchanged.
/// </summary>
public sealed class AcadeProjectRepairer : IReferenceRepairer
{
    public bool CanRepair(string parentPath) => FileKinds.IsAcadeProject(parentPath);

    public bool Repair(string parentPath, string oldChildPath, string newChildPath)
    {
        var bytes = File.ReadAllBytes(parentPath);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var body = hasBom ? bytes[3..] : bytes;
        // Older projects are often saved in the PC's ANSI code page (°, ², Ø in descriptions). Anything that isn't
        // valid UTF-8 is read as Latin-1, which gives every byte back unchanged when the file is written again.
        Encoding encoding = new UTF8Encoding(hasBom, throwOnInvalidBytes: true);
        string text;
        try
        {
            text = encoding.GetString(body);
        }
        catch (DecoderFallbackException)
        {
            encoding = Encoding.Latin1;
            text = encoding.GetString(body);
        }
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split(newline);

        // Drawing lines are the ones that aren't description (*), settings (+) or headings (=); see AcadeProject.
        var folder = Path.GetDirectoryName(Path.GetFullPath(parentPath))!;
        var changed = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith('*') || line.StartsWith('+') || line.StartsWith('='))
                continue;
            var resolved = Path.GetFullPath(Path.IsPathRooted(line) ? line : Path.Combine(folder, line.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
            if (!string.Equals(resolved, Path.GetFullPath(oldChildPath), StringComparison.OrdinalIgnoreCase))
                continue;

            var relative = Path.GetRelativePath(folder, newChildPath);
            // Keep the separator the project file already uses; AutoCAD Electrical writes backslashes.
            lines[i] = line.Contains('/') ? relative.Replace('\\', '/') : relative.Replace('/', '\\');
            changed = true;
        }
        if (!changed)
            return false;

        var output = encoding.GetBytes(string.Join(newline, lines));
        File.WriteAllBytes(parentPath, hasBom ? [.. encoding.GetPreamble(), .. output] : output);
        return true;
    }
}
