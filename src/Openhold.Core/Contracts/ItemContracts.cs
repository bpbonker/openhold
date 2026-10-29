namespace Openhold.Core.Contracts;

/// <summary>The states of a catalogue item. Numbers are issued at once; a part librarian releases items.</summary>
public static class ItemStates
{
    public const string Draft = "Draft";
    public const string Released = "Released";
    public const string Obsolete = "Obsolete";
}

/// <summary>Whether a part is made (designed in-house) or bought (a buyout part from a supplier).</summary>
public static class ItemKinds
{
    public const string Made = "made";
    public const string Bought = "bought";
}

/// <summary>An approved manufacturer part: who makes it and their part number.</summary>
public sealed record ManufacturerPart(string Manufacturer, string PartNumber, bool Preferred = false);

/// <summary>Another number the part is known by, e.g. its legacy vault number or an ERP's old code.</summary>
public sealed record CrossReference(string System, string Number);

/// <summary>A file that defines or describes an item. <see cref="Role"/> is model, drawing, datasheet or other.</summary>
public sealed record ItemFileLink(Guid FileId, string Role = "model")
{
    public string? Name { get; init; }

    public string? FolderPath { get; init; }
}

/// <summary>A catalogue item in full.</summary>
public sealed record ItemInfo(
    Guid Id, string Number, string Description, string Unit, string Kind, string? ClassId, string? ClassName, string State,
    IReadOnlyDictionary<string, string> Attributes, string? Notes,
    IReadOnlyList<ManufacturerPart> ManufacturerParts, IReadOnlyList<CrossReference> CrossReferences, IReadOnlyList<ItemFileLink> Files,
    string CreatedBy, DateTime CreatedAt, DateTime UpdatedAt, string? ReleasedBy, DateTime? ReleasedAt, int RowVersion);

/// <summary>An item as listed in search results.</summary>
public sealed record ItemSummary(
    Guid Id, string Number, string Description, string Kind, string? ClassName, string State, string? Manufacturer, string? ManufacturerPartNumber);

/// <summary>
/// A new item, or the edited item (<see cref="RowVersion"/> is the version that was edited, so two people's
/// changes can't silently overwrite each other).
/// </summary>
public sealed record SaveItemRequest(
    string Description, string Kind, string Unit = "each", string? ClassId = null,
    IReadOnlyDictionary<string, string>? Attributes = null, string? Notes = null,
    IReadOnlyList<ManufacturerPart>? ManufacturerParts = null, IReadOnlyList<CrossReference>? CrossReferences = null,
    IReadOnlyList<ItemFileLink>? Files = null, int? RowVersion = null,
    IReadOnlyDictionary<string, string>? NumberFields = null);

public sealed record ItemStateRequest(string State, int RowVersion, string? Comment = null);

/// <summary>Search the catalogue by text (number, description, manufacturer part, cross-reference, attributes) and filters.</summary>
public sealed record ItemSearchRequest(string? Text = null, string? ClassId = null, string? State = null, string? Kind = null, int Limit = 200);

public sealed record DuplicateCheckRequest(IReadOnlyList<ManufacturerPart> ManufacturerParts, Guid? ExcludeItemId = null);

/// <summary>A field a kind of ERP connection needs, e.g. simPRO's BaseUrl. Secrets are never sent back once saved.</summary>
public sealed record ErpSettingInfo(string Name, string Label, bool Secret = false, bool Required = true, string? Hint = null);

/// <summary>A kind of ERP connection Openhold can make (a connector), and the fields it needs.</summary>
public sealed record ErpKindInfo(string Kind, string Name, string Description, IReadOnlyList<ErpSettingInfo> Settings);

/// <summary>
/// An ERP connection as administrators see it: its settings, and which secrets are saved (never their values).
/// <see cref="FromServerSettings"/> connections come from the server's settings file and are changed there.
/// </summary>
public sealed record ErpConnectionDetails(
    string Id, string Name, string Kind, bool Enabled, bool FromServerSettings,
    IReadOnlyDictionary<string, string> Settings, IReadOnlyList<string> SecretsSaved);

/// <summary>
/// Adds or changes an ERP connection. <see cref="Secrets"/> holds only the secrets being entered or changed (an empty
/// value removes one); secrets left out keep their saved values.
/// </summary>
public sealed record SaveErpConnectionRequest(
    string Name, string Kind, bool Enabled, IReadOnlyDictionary<string, string> Settings, IReadOnlyDictionary<string, string>? Secrets = null);

/// <summary>An ERP that receives released items, with how many items are waiting, failed or sent.</summary>
public sealed record ErpConnectionInfo(string Id, string Name, string Kind, int Pending, int Failed, int Sent);

/// <summary>Where an item stands in one ERP: its id there, and whether the latest change has been sent.</summary>
public sealed record ItemErpStatus(string ConnectionId, string ConnectionName, string? ExternalId, string Status, int Attempts, string? LastError, DateTime UpdatedAt);

/// <summary>An existing item that may be the same part, and why it looks like it.</summary>
public sealed record DuplicateCandidate(ItemSummary Item, string Reason);
