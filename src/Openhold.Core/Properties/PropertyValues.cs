using System.Globalization;

namespace Openhold.Core.Properties;

/// <summary>Turns CAD property values (iProperties, attributes) into the text Openhold stores, searches and shows.</summary>
public static class PropertyValues
{
    /// <summary>
    /// Text for a property value, or null when the value is empty or a placeholder, e.g. Inventor's unset
    /// dates (1 Jan 1601), so blank fields never appear as noise. Dates use ISO format (yyyy-MM-dd) so they
    /// read and sort the same everywhere; booleans become Yes/No; numbers use invariant formatting.
    /// </summary>
    public static string? Format(object? value) => value switch
    {
        null => null,
        string text => string.IsNullOrWhiteSpace(text) ? null : text.Trim(),
        DateTime { Year: <= 1601 } => null,
        DateTime date => date.TimeOfDay == TimeSpan.Zero
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        bool flag => flag ? "Yes" : "No",
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => null, // pictures and other binary values are not properties a person reads
    };
}
