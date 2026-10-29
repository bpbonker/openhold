using System.IO;
using Autodesk.AutoCAD.DatabaseServices;

namespace Openhold.AutoCAD.Plugin;

/// <summary>
/// Repoints a drawing's external references (xrefs, raster images, PDF/DWF/DGN underlays) from a file's old
/// location to its new one, after Openhold renamed or moved it. Each path keeps its style: an absolute path stays
/// absolute, a relative one stays relative, and a bare file name stays bare when the file is still beside the drawing.
/// </summary>
public static class ReferenceRepair
{
    private static readonly string[] UnderlayDictionaries = ["ACAD_PDFDEFINITIONS", "ACAD_DWFDEFINITIONS", "ACAD_DGNDEFINITIONS"];

    /// <summary>Returns how many references were repointed; the caller saves the drawing if any were.</summary>
    public static int Repoint(Database database, string drawingPath, string oldPath, string newPath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(drawingPath))!;
        var changed = 0;
        string? Repointed(string stored) =>
            string.Equals(Resolve(folder, stored), Path.GetFullPath(oldPath), StringComparison.OrdinalIgnoreCase)
                ? InSameStyle(folder, stored, Path.GetFullPath(newPath))
                : null;

        using var transaction = database.TransactionManager.StartTransaction();
        var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        foreach (ObjectId id in blockTable)
        {
            var record = (BlockTableRecord)transaction.GetObject(id, OpenMode.ForRead);
            if ((record.IsFromExternalReference || record.IsFromOverlayReference) && Repointed(record.PathName) is { } path)
            {
                record.UpgradeOpen();
                record.PathName = path;
                changed++;
            }
        }

        var imageDictionaryId = RasterImageDef.GetImageDictionary(database);
        if (!imageDictionaryId.IsNull)
        {
            foreach (DBDictionaryEntry entry in (DBDictionary)transaction.GetObject(imageDictionaryId, OpenMode.ForRead))
            {
                if (transaction.GetObject(entry.Value, OpenMode.ForRead) is RasterImageDef image && Repointed(image.SourceFileName) is { } path)
                {
                    image.UpgradeOpen();
                    image.SourceFileName = path;
                    changed++;
                }
            }
        }

        var namedObjects = (DBDictionary)transaction.GetObject(database.NamedObjectsDictionaryId, OpenMode.ForRead);
        foreach (var dictionaryName in UnderlayDictionaries.Where(namedObjects.Contains))
        {
            foreach (DBDictionaryEntry entry in (DBDictionary)transaction.GetObject(namedObjects.GetAt(dictionaryName), OpenMode.ForRead))
            {
                if (transaction.GetObject(entry.Value, OpenMode.ForRead) is UnderlayDefinition underlay && Repointed(underlay.SourceFileName) is { } path)
                {
                    underlay.UpgradeOpen();
                    underlay.SourceFileName = path;
                    changed++;
                }
            }
        }

        transaction.Commit();
        return changed;
    }

    /// <summary>Where a stored reference path points, read the way AutoCAD finds it first: relative to the drawing.</summary>
    internal static string Resolve(string drawingFolder, string stored) =>
        Path.GetFullPath(Path.IsPathRooted(stored) ? stored : Path.Combine(drawingFolder, stored));

    internal static string InSameStyle(string drawingFolder, string stored, string newPath)
    {
        if (Path.IsPathRooted(stored))
            return newPath;
        var relative = Path.GetRelativePath(drawingFolder, newPath);
        var storedIsBare = Path.GetFileName(stored) == stored;
        var newIsBeside = Path.GetFileName(relative) == relative;
        if (storedIsBare && newIsBeside)
            return relative;
        // AutoCAD writes relative paths as .\folder\file.dwg or ..\file.dwg.
        return relative.StartsWith('.') ? relative : ".\\" + relative;
    }
}
