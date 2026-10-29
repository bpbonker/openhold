using Autodesk.AutoCAD.DatabaseServices;

namespace Openhold.AutoCAD.Plugin;

public sealed record DrawingReference(string Kind, string Name, string StoredPath, bool Unloaded);

public sealed record TitleBlockAttributes(string BlockName, string Layout, IReadOnlyDictionary<string, string> Attributes);

public sealed record DrawingIndexResult(
    string File,
    IReadOnlyList<DrawingReference> References,
    IReadOnlyList<TitleBlockAttributes> AttributedBlocks);

/// <summary>
/// Extracts what the vault needs from a drawing: external references (xrefs, raster images,
/// PDF/DWF/DGN underlays) with their stored paths, and attribute values of attributed blocks on
/// layouts (title blocks), which map to vault properties.
/// </summary>
public static class DrawingIndex
{
    private static readonly (string Dictionary, string Kind)[] UnderlayDictionaries =
    [
        ("ACAD_PDFDEFINITIONS", "pdf"),
        ("ACAD_DWFDEFINITIONS", "dwf"),
        ("ACAD_DGNDEFINITIONS", "dgn"),
    ];

    public static DrawingIndexResult Read(Database database)
    {
        var references = new List<DrawingReference>();
        var blocks = new List<TitleBlockAttributes>();

        using var transaction = database.TransactionManager.StartOpenCloseTransaction();

        var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        foreach (ObjectId id in blockTable)
        {
            var record = (BlockTableRecord)transaction.GetObject(id, OpenMode.ForRead);
            if (record.IsFromExternalReference || record.IsFromOverlayReference)
                references.Add(new DrawingReference(record.IsFromOverlayReference ? "overlay" : "xref", record.Name, record.PathName, record.IsUnloaded));
        }

        var namedObjects = (DBDictionary)transaction.GetObject(database.NamedObjectsDictionaryId, OpenMode.ForRead);
        var imageDictionaryId = RasterImageDef.GetImageDictionary(database);
        if (!imageDictionaryId.IsNull)
        {
            var images = (DBDictionary)transaction.GetObject(imageDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in images)
            {
                if (transaction.GetObject(entry.Value, OpenMode.ForRead) is RasterImageDef image)
                    references.Add(new DrawingReference("image", entry.Key, image.SourceFileName, !image.IsLoaded));
            }
        }

        foreach (var (dictionaryName, kind) in UnderlayDictionaries)
        {
            if (!namedObjects.Contains(dictionaryName))
                continue;
            var underlays = (DBDictionary)transaction.GetObject(namedObjects.GetAt(dictionaryName), OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in underlays)
            {
                if (transaction.GetObject(entry.Value, OpenMode.ForRead) is UnderlayDefinition underlay)
                    references.Add(new DrawingReference(kind, entry.Key, underlay.SourceFileName, !underlay.Loaded));
            }
        }

        var layouts = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
        foreach (DBDictionaryEntry entry in layouts)
        {
            var layout = (Layout)transaction.GetObject(entry.Value, OpenMode.ForRead);
            var space = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
            foreach (ObjectId entityId in space)
            {
                if (transaction.GetObject(entityId, OpenMode.ForRead) is not BlockReference reference || reference.AttributeCollection.Count == 0)
                    continue;

                var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (ObjectId attributeId in reference.AttributeCollection)
                {
                    if (transaction.GetObject(attributeId, OpenMode.ForRead) is AttributeReference attribute)
                        attributes[attribute.Tag] = attribute.TextString;
                }
                var definition = (BlockTableRecord)transaction.GetObject(
                    reference.IsDynamicBlock ? reference.DynamicBlockTableRecord : reference.BlockTableRecord, OpenMode.ForRead);
                blocks.Add(new TitleBlockAttributes(definition.Name, layout.LayoutName, attributes));
            }
        }

        transaction.Commit();
        return new DrawingIndexResult(database.Filename, references, blocks);
    }
}
