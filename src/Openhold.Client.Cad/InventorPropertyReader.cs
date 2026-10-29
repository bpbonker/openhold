using System.Runtime.InteropServices;
using Inventor;
using Openhold.Core.Properties;

namespace Openhold.Client.Cad;

/// <summary>
/// Reads a document's iProperties as Openhold records them: every standard one in <see cref="InventorProperties"/> under
/// its dialog name (calculated mass properties only when Inventor says they're up to date), and every custom one.
/// </summary>
internal static class InventorPropertyReader
{
    public static Dictionary<string, string> Read(PropertySets sets)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var massValid = Value(sets, InventorProperties.DesignTrackingSet, "Valid MassProps") is not (0 or null);
        foreach (var definition in InventorProperties.All)
        {
            if (definition.Unit is not null && !massValid)
                continue; // mass, area, volume and density aren't current: better none than wrong
            if (InventorProperties.Format(definition, Value(sets, definition.Set, definition.Internal)) is { } value)
                properties[definition.Name] = value;
        }
        try
        {
            foreach (Property property in sets[InventorProperties.CustomSet])
            {
                if (PropertyValues.Format(Read(property)) is { } value)
                    properties.TryAdd(property.Name, value);
            }
        }
        catch (COMException)
        {
            // no custom set in this kind of document
        }
        return properties;
    }

    private static object? Value(PropertySets sets, string set, string name)
    {
        try
        {
            return Read(sets[set][name]);
        }
        catch (COMException)
        {
            return null; // not in this kind of document
        }
    }

    private static object? Read(Property property)
    {
        try
        {
            return property.Value;
        }
        catch (COMException)
        {
            return null; // some values (e.g. expression-driven) can't be read outside a model update
        }
    }
}
