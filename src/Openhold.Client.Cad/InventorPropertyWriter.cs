using System.Globalization;
using System.Runtime.InteropServices;
using Inventor;
using Openhold.Client.Sdk.Workspace;
using Openhold.Core.Properties;
using Path = System.IO.Path;

namespace Openhold.Client.Cad;

/// <summary>
/// Writes iProperties into Inventor files with Apprentice, so properties edited in Openhold Explorer are in the file
/// when it's next opened. Standard iProperties are found through <see cref="InventorProperties"/> (by the name in
/// Inventor's iProperties dialog, e.g. "Estimated Cost", or the internal name); calculated ones (mass, material) are
/// never written. Any other name is a custom iProperty.
/// </summary>
public sealed class InventorPropertyWriter : IPropertyWriter
{
    public string StoredAs => "iProperty";

    private const string CustomSet = InventorProperties.CustomSet;

    // Placeholder Inventor uses for "no date"; Openhold shows it as empty.
    private static readonly DateTime NoDate = new(1601, 1, 1);

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".ipt", ".iam", ".idw", ".ipn", ".ide" };

    public bool CanWrite(string path) => Extensions.Contains(Path.GetExtension(path)) && Apprentice.Installed;

    public void Write(string path, IReadOnlyDictionary<string, string> values) =>
        Apprentice.Run($"write the iProperties of {Path.GetFileName(path)}", apprentice =>
        {
            var document = apprentice.Open(path);
            if (document.NeedsMigrating)
            {
                throw new IOException(
                    $"{Path.GetFileName(path)} was saved by an older Inventor. Open and save it in Inventor first, then edit its properties.");
            }
            foreach (var (name, value) in values)
            {
                if (InventorProperties.Find(name) is { } standard)
                    SetStandard(document, standard, value);
                else
                    SetProperty(document, name, value);
            }
            document.PropertySets.FlushToFile();
            return true;
        });

    /// <summary>A standard iProperty, in its own set; a choice (e.g. Design State) is stored as Inventor's number for it.</summary>
    private static void SetStandard(ApprenticeServerDocument document, IPropertyDefinition definition, string value)
    {
        if (!definition.Editable)
            return; // worked out by Inventor; any value here is just what was read
        Property property;
        try
        {
            property = document.PropertySets[definition.Set][definition.Internal];
        }
        catch (COMException)
        {
            return; // this kind of document doesn't have it (e.g. no Design Tracking set in a presentation)
        }
        try
        {
            if (definition.Kind == IPropertyKind.Choice)
            {
                property.Value = value.Length == 0
                    ? definition.Choices![0].Value
                    : InventorProperties.ChoiceValue(definition, value)
                      ?? throw new IOException($"{definition.Name} must be one of: {string.Join(", ", definition.Choices!.Select(c => c.Label))}.");
                return;
            }
            property.Value = ConvertLike(property.Value, value, definition.Name);
        }
        catch (COMException ex)
        {
            throw new IOException($"Inventor wouldn't accept \"{value}\" for {definition.Name}: {ex.Message}", ex);
        }
    }

    private static void SetProperty(ApprenticeServerDocument document, string name, string value)
    {
        if (Find(document, name) is not { } property)
        {
            if (value.Length > 0)
                document.PropertySets[CustomSet].Add(value, name);
            return;
        }
        if (value.Length == 0 && property.Parent.Name == CustomSet)
        {
            property.Delete();
            return;
        }
        try
        {
            property.Value = ConvertLike(property.Value, value, name);
        }
        catch (COMException ex)
        {
            throw new IOException($"Inventor wouldn't accept \"{value}\" for {name}: {ex.Message}", ex);
        }
    }

    /// <summary>Converts the text to the property's existing type, so dates stay dates and numbers stay numbers.</summary>
    private static object ConvertLike(object? current, string value, string name)
    {
        switch (current)
        {
            case DateTime:
                if (value.Length == 0)
                    return NoDate;
                return DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out var date)
                    || DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
                    ? date
                    : throw new IOException($"{name} needs a date, e.g. {DateTime.Today:yyyy-MM-dd}.");
            case bool:
                return value.Trim().ToLowerInvariant() switch
                {
                    "yes" or "true" or "1" => true,
                    "no" or "false" or "0" or "" => false,
                    _ => throw new IOException($"{name} needs Yes or No."),
                };
            case double or float or decimal or int or long when value.Length == 0:
                return Convert.ChangeType(0, current.GetType(), CultureInfo.InvariantCulture); // cleared: Inventor's "no value"
            case double or float or decimal or int or long:
                // Costs may be typed with a currency sign or thousands separators, e.g. $1,250.00.
                return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    || double.TryParse(value, NumberStyles.Currency, CultureInfo.CurrentCulture, out number)
                    || double.TryParse(value.TrimStart('$', '€', '£', '₹').Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                    ? Convert.ChangeType(number, current.GetType(), CultureInfo.InvariantCulture)
                    : throw new IOException($"{name} needs a number.");
            default:
                return value;
        }
    }

    private static Property? Find(ApprenticeServerDocument document, string name)
    {
        foreach (PropertySet set in document.PropertySets)
        {
            foreach (Property property in set)
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    return property;
            }
        }
        return null;
    }
}
