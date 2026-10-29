namespace Openhold.Client.Sdk.Workspace;

/// <summary>
/// Writes property values into a file (Inventor iProperties through Apprentice, AutoCAD title blocks), so a
/// property edited in Openhold is also in the file when someone opens it. Implementations exist per file type.
/// </summary>
public interface IPropertyWriter
{
    bool CanWrite(string path);

    /// <summary>Where the values end up, in words for the person editing, e.g. "iProperty" or "Title block".</summary>
    string StoredAs => "In the file";

    /// <summary>
    /// Whether the file can hold this property at all. A DWG holds only properties mapped to title-block attributes;
    /// others are kept in the vault only, as Autodesk Vault does for properties with no mapping.
    /// </summary>
    bool CanHold(string path, string property) => true;

    /// <summary>
    /// Writes the values into the file; an empty value clears that property. Throws <see cref="IOException"/> with a
    /// message for the user when the file can't be written (e.g. it needs migrating to this Inventor version first).
    /// </summary>
    void Write(string path, IReadOnlyDictionary<string, string> values);
}
