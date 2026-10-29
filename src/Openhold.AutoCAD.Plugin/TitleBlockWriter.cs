using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;

namespace Openhold.AutoCAD.Plugin;

/// <summary>What a title-block write changed: attributes updated, and requested tags no title block has.</summary>
public sealed record TitleBlockWriteResult(int Updated, int TitleBlocks, IReadOnlyList<string> MissingTags);

/// <summary>
/// Sets title-block attribute values on every layout, so a property edited in Openhold appears on every sheet.
/// Works on any database, including in accoreconsole where Openhold Explorer runs it headlessly. Nothing changes
/// unless a title block was found and every requested tag is on one, so a typo can't half-update a drawing.
/// </summary>
public static class TitleBlockWriter
{
    public static TitleBlockWriteResult Write(Database database, string blockNamePattern, IReadOnlyDictionary<string, string> valuesByTag)
    {
        var blockName = new Regex(blockNamePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var updated = 0;
        var titleBlocks = 0;

        using var transaction = database.TransactionManager.StartTransaction();
        var layouts = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
        foreach (DBDictionaryEntry entry in layouts)
        {
            var layout = (Layout)transaction.GetObject(entry.Value, OpenMode.ForRead);
            var space = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
            foreach (ObjectId entityId in space)
            {
                if (transaction.GetObject(entityId, OpenMode.ForRead) is not BlockReference reference || reference.AttributeCollection.Count == 0)
                    continue;
                var definition = (BlockTableRecord)transaction.GetObject(
                    reference.IsDynamicBlock ? reference.DynamicBlockTableRecord : reference.BlockTableRecord, OpenMode.ForRead);
                if (!blockName.IsMatch(definition.Name))
                    continue;

                titleBlocks++;
                foreach (ObjectId attributeId in reference.AttributeCollection)
                {
                    if (transaction.GetObject(attributeId, OpenMode.ForRead) is not AttributeReference attribute
                        || !valuesByTag.TryGetValue(attribute.Tag, out var value))
                    {
                        continue;
                    }
                    found.Add(attribute.Tag);
                    if (attribute.TextString == value)
                        continue;
                    attribute.UpgradeOpen();
                    attribute.TextString = value;
                    if (attribute.IsMTextAttribute)
                        attribute.UpdateMTextAttribute();
                    else
                        attribute.AdjustAlignment(database);
                    updated++;
                }
            }
        }
        var missing = valuesByTag.Keys.Where(t => !found.Contains(t)).ToList();
        if (titleBlocks > 0 && missing.Count == 0)
            transaction.Commit();
        return new TitleBlockWriteResult(titleBlocks > 0 && missing.Count == 0 ? updated : 0, titleBlocks, missing);
    }
}
