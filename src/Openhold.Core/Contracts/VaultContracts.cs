namespace Openhold.Core.Contracts;

// Wire contracts shared by the server, desktop app, job worker and CAD add-ins.

public sealed record LockedFile(Guid FileId, int LatestVersion);

public sealed record LockHolder(Guid FileId, string Username, string SiteId, string Machine, string State, DateTime AcquiredAt);

public sealed record CheckOutResult(
    bool Succeeded,
    IReadOnlyList<LockedFile> Files,
    IReadOnlyList<LockHolder> Conflicts,
    IReadOnlyList<Guid> NotFound);

/// <summary>A reference from a file to a child. The child is either an existing vault file or another file in the same changeset.</summary>
public sealed record CheckInReference(Guid? ChildFileId, int? ChildIndex, int? ChildVersion, string? RefPath);

/// <summary>
/// One file in a check-in: an existing file (<see cref="FileId"/>) or a new one (<see cref="FolderId"/> + <see cref="Name"/>).
/// Null <see cref="Properties"/> or <see cref="References"/> keep the previous version's; an empty list means "none".
/// <see cref="NewName"/> / <see cref="NewFolderId"/> rename or move an existing file as part of the same atomic changeset,
/// so a file and the parents whose references were repaired always change together.
/// <see cref="VaultProperties"/> are properties kept in the vault only, because the file can't hold them (e.g. a DWG
/// property with no title-block attribute); null keeps the previous version's, so a CAD check-in never drops them.
/// </summary>
public sealed record CheckInFile(
    Guid? FileId,
    Guid? FolderId,
    string? Name,
    byte[] Manifest,
    IReadOnlyDictionary<string, string>? Properties,
    IReadOnlyList<CheckInReference>? References,
    bool KeepCheckedOut = false,
    byte[]? Thumbnail = null,
    string? NewName = null,
    Guid? NewFolderId = null,
    IReadOnlyDictionary<string, string>? VaultProperties = null);

public sealed record CheckInRequest(Guid ChangesetId, string? Comment, IReadOnlyList<CheckInFile> Files);

public sealed record CommittedVersion(Guid FileId, Guid FolderId, string Name, int Version);

public sealed record CheckInResult(Guid ChangesetId, bool AlreadyCommitted, IReadOnlyList<CommittedVersion> Versions);

/// <summary>
/// A numbering scheme: a template (see <c>NumberTemplate</c>) and the fields people fill in. The counter counts
/// separately for each different text before it, so <c>{field:Area}-{seq:000}</c> numbers each area from 1.
/// <see cref="InUse"/> once it has issued numbers: its template and fields can then only gain new choices.
/// </summary>
public sealed record NumberingScheme(string Id, string Name, string Template, bool AllowReuse, TimeSpan ReservationTtl, bool Enabled)
{
    /// <summary>The fields the template uses ({field:Name}): free text, or one of a list of codes.</summary>
    public IReadOnlyList<NumberingField> Fields { get; init; } = [];

    /// <summary>The first number each counter issues, e.g. 100000 for six-digit numbers that never start with a zero.</summary>
    public long StartAt { get; init; } = 1;

    public string? Description { get; init; }

    /// <summary>Set by the server: the scheme has issued numbers, so its structure is fixed.</summary>
    public bool InUse { get; init; }
}

/// <summary>
/// A value people choose or type when asking for a number, e.g. Area. With <see cref="Choices"/>, only those codes
/// are allowed. <see cref="From"/> fills it in from where the file is saved (see <see cref="NumberingSources"/>);
/// <see cref="Default"/> is used when nothing else gives a value. People can always change what was filled in.
/// A typed value (no choices) can be held to <see cref="Format"/> (see <see cref="NumberingFormats"/>) and a length.
/// </summary>
public sealed record NumberingField(string Name, string? Label = null, IReadOnlyList<NumberingChoice>? Choices = null, string? Hint = null,
    string? From = null, string? Default = null, string? Format = null, int? MinLength = null, int? MaxLength = null)
{
    /// <summary>Why a value doesn't fit this field (not one of its codes, wrong characters, wrong length), or null when it fits.</summary>
    public string? Problem(string value)
    {
        var label = Label ?? Name;
        if (Choices is { Count: > 0 } choices)
        {
            return choices.Any(c => string.Equals(c.Code, value, StringComparison.OrdinalIgnoreCase))
                ? null
                : $"{label} must be one of: {string.Join(", ", choices.Select(c => $"{c.Code} ({c.Meaning})"))}.";
        }
        if (!NumberingFormats.Fits(Format, value))
            return $"{label} must be {NumberingFormats.Describe(Format)}.";
        if (MinLength is { } min && MaxLength is { } max && (value.Length < min || value.Length > max))
            return min == max ? $"{label} must be {min} characters long." : $"{label} must be {min} to {max} characters long.";
        if (MinLength is { } atLeast && value.Length < atLeast)
            return $"{label} must be at least {atLeast} characters long.";
        if (MaxLength is { } atMost && value.Length > atMost)
            return $"{label} can be at most {atMost} characters long.";
        return null;
    }
}

/// <summary>What a typed numbering field may contain.</summary>
public static class NumberingFormats
{
    public const string Letters = "letters";
    public const string Digits = "digits";
    public const string LettersAndDigits = "letters-digits";

    public static bool IsKnown(string? format) => format is null or Letters or Digits or LettersAndDigits;

    public static bool Fits(string? format, string value) => format switch
    {
        Letters => value.All(char.IsAsciiLetter),
        Digits => value.All(char.IsAsciiDigit),
        LettersAndDigits => value.All(char.IsAsciiLetterOrDigit),
        _ => true,
    };

    public static string Describe(string? format) => format switch
    {
        Letters => "letters only",
        Digits => "digits only",
        LettersAndDigits => "letters and digits only",
        _ => "any text",
    };
}

/// <summary>Where a numbering field's value can come from automatically.</summary>
public static class NumberingSources
{
    /// <summary>
    /// The folder the file is saved in, or one above it: the code of a folder named "CODE - Name" (e.g. 02 from
    /// "02 - Frame"). Fields are matched top-down in template order, so a project folder fills the project field
    /// and an area folder below it fills the area.
    /// </summary>
    public const string Folder = "folder";

    /// <summary>The number of the assembly the file is made in (its name without " - description").</summary>
    public const string Parent = "parent";

    public static bool IsKnown(string? source) => source is null or Folder or Parent;
}

/// <summary>A field as filled in for one save: its value (null when nothing gave one) and where the value came from.</summary>
public sealed record SuggestedField(NumberingField Field, string? Value, string? Source)
{
    public string Label => Field.Label ?? Field.Name;
}

/// <summary>
/// How a new file would be named: the category its folder and type give it, that category's scheme, and the fields
/// filled in from where it is saved. <see cref="Next"/> is the number it would get when all fields have values.
/// </summary>
public sealed record NamingSuggestion(string? CategoryId, string? CategoryName, NumberingScheme? Scheme, IReadOnlyList<SuggestedField> Fields, bool Enforced)
{
    public string? Next { get; init; }

    public bool Complete => Scheme is not null && Fields.All(f => !string.IsNullOrWhiteSpace(f.Value));
}

/// <summary>One allowed code for a field and what it means, e.g. "05" = Hydraulics.</summary>
public sealed record NumberingChoice(string Code, string Meaning);

/// <summary>What the next number would be with these field values, without reserving it.</summary>
public sealed record NumberPreview(string SchemeId, string Next, string Counter);

public sealed record ReservedNumber(string SchemeId, long Value, string Formatted, DateTime ExpiresAt);

public sealed record FolderInfo(Guid Id, Guid? ParentId, string Name, string Path);

public sealed record VaultFileInfo(
    Guid Id, Guid FolderId, string Name, int LatestVersion, long? Length, string? ContentHash, DateTime? ModifiedAt,
    string? LockedBy, string? LockSite, string? LockMachine, string? LockState)
{
    /// <summary>Properties recorded with the latest version (e.g. Part Number, Description, Revision).</summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();

    /// <summary>The subset of <see cref="Properties"/> kept in the vault only, because the file itself can't hold them.</summary>
    public IReadOnlyDictionary<string, string> VaultProperties { get; init; } = new Dictionary<string, string>();

    /// <summary>The file's category id (e.g. "engineering"), or null if it has none.</summary>
    public string? Category { get; init; }

    /// <summary>The category's display name.</summary>
    public string? CategoryName { get; init; }

    /// <summary>The lifecycle state of the latest version, or null if the file's category has no lifecycle.</summary>
    public string? State { get; init; }

    /// <summary>The state doesn't allow check-out (e.g. Released): it must move back to an editable state first.</summary>
    public bool StateLocked { get; init; }

    /// <summary>The state counts as released.</summary>
    public bool StateReleased { get; init; }

    public bool StateObsolete { get; init; }

    /// <summary>
    /// The latest version was checked in at another site and its content hasn't fully arrived at this site's server
    /// yet. Getting it still works (the server fetches it first) but crosses the link between sites.
    /// </summary>
    public bool Syncing { get; init; }
}

/// <summary>A site's server and how many file contents it still lacks (zero when it's up to date).</summary>
public sealed record SiteStatus(string Id, string Name, string? Url, bool IsAuthority, bool IsThisSite, DateTime? LastSeen, int Missing);

public sealed record FolderListing(FolderInfo Folder, IReadOnlyList<FolderInfo> Folders, IReadOnlyList<VaultFileInfo> Files);

public sealed record VersionInfo(int Version, Guid ChangesetId, long Length, string ContentHash, string OriginSite, string Username, string Comment, DateTime CreatedAt)
{
    /// <summary>Properties recorded with this version (e.g. read from title blocks or iProperties).</summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();

    /// <summary>The lifecycle state this version was in, or null if the file has no lifecycle.</summary>
    public string? State { get; init; }
}

public sealed record ReferenceInfo(Guid FileId, string Name, string Path, int Version, string? RefPath);

public sealed record FileIdsRequest(IReadOnlyList<Guid> FileIds);

public sealed record CreateFolderRequest(string Path);

public sealed record ReserveNumbersRequest(int Count = 1, IReadOnlyDictionary<string, string>? Fields = null);

public sealed record ReleaseNumbersRequest(IReadOnlyList<string> Numbers);

/// <summary>A vault path and the file at it, or null if nothing is there yet.</summary>
public sealed record ResolvedPath(string Path, VaultFileInfo? File);

public sealed record ResolvePathsRequest(IReadOnlyList<string> Paths);

/// <summary>
/// A search. <see cref="Text"/> holds terms that must all match; a plain term matches the name, folder,
/// any property value or the latest check-in comment; <c>Property:value</c> matches one property;
/// <c>*</c> is a wildcard. <see cref="Folder"/> limits the search to a folder and everything below it.
/// </summary>
public sealed record SearchRequest(string Text, string? Folder = null, int Limit = 200);

/// <summary>A file found by a search, where it is, and what matched (e.g. "Description: AXLE ASSEMBLY").</summary>
public sealed record SearchHit(VaultFileInfo File, string FolderPath, string? Match);

public sealed record SearchResults(IReadOnlyList<SearchHit> Hits, bool Truncated);

/// <summary>What a server says about itself before sign-in: used to test a connection.</summary>
public sealed record ServerHealth(string Status, string Site, string? SiteName);

/// <summary>Make <see cref="Version"/> the newest version again. The changeset id makes retries safe.</summary>
public sealed record RestoreRequest(Guid ChangesetId, int Version, string? Comment = null);

/// <summary>What <see cref="Username"/> may do in the folder at <see cref="Path"/>, and why in words.</summary>
public sealed record FolderAccessInfo(string Path, string Username, Openhold.Core.Configuration.FolderAccess Access, string Reason);

/// <summary>A file in the trash bin: deleted, restorable with its full history.</summary>
public sealed record TrashItem(Guid FileId, string Name, string FolderPath, int LatestVersion, DateTime DeletedAt, string DeletedBy);

/// <summary>The vault configuration in force and its version number (each save makes a new version).</summary>
public sealed record VersionedConfiguration(int Version, Openhold.Core.Configuration.VaultConfiguration Configuration);

/// <summary>Saves a configuration made from version <see cref="BasedOnVersion"/>; refused if someone saved in the meantime.</summary>
public sealed record SaveConfigurationRequest(int BasedOnVersion, Openhold.Core.Configuration.VaultConfiguration Configuration, string? Comment = null);

/// <summary>
/// Moves files to <see cref="ToState"/> together, or none of them. With <see cref="CheckOnly"/> nothing changes:
/// the result says whether it would work and whether it would need approval. The changeset id makes retries safe.
/// </summary>
public sealed record ChangeStateRequest(Guid ChangesetId, IReadOnlyList<Guid> FileIds, string ToState, string? Comment = null, bool CheckOnly = false);

/// <summary>Why one file can't make a state change, e.g. "uses BRACKET.ipt, which isn't released".</summary>
public sealed record StateChangeProblem(Guid FileId, string FileName, string Message);

/// <summary>
/// The outcome of a state change: <see cref="Applied"/> with the new versions, or waiting for approval in change
/// request <see cref="Id"/>. A check lists any <see cref="Problems"/> and whether approval would be needed.
/// </summary>
public sealed record StateChangeResult(Guid Id, bool Applied, bool NeedsApproval, IReadOnlyList<CommittedVersion> Versions, IReadOnlyList<StateChangeProblem> Problems);

public sealed record ChangeRequestFile(Guid FileId, string Name, string FolderPath, int Version, string FromState);

/// <summary>How far one approval rule has got: <see cref="Approved"/> of <see cref="Required"/> people from the group.</summary>
public sealed record ApprovalProgress(string Group, string GroupName, int Required, int Approved);

public sealed record ApprovalDecision(string Username, string Decision, string Comment, DateTime DecidedAt);

/// <summary>A state change waiting for (or resolved by) approval.</summary>
public sealed record ChangeRequestInfo(
    Guid Id, string ToState, string Comment, string RequestedBy, DateTime RequestedAt, string Status,
    IReadOnlyList<ChangeRequestFile> Files, IReadOnlyList<ApprovalProgress> Approvals, IReadOnlyList<ApprovalDecision> Decisions,
    bool CanApprove, bool IsMine)
{
    /// <summary>Why an approved change couldn't be applied (status "blocked"), e.g. a file changed since it was submitted.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];
}

/// <summary>An approver's sign-off or rejection, with an optional comment (required to reject).</summary>
public sealed record ApprovalRequest(string? Comment = null);

/// <summary>Puts files in another category; their state becomes the new lifecycle's first state.</summary>
public sealed record ChangeCategoryRequest(Guid ChangesetId, IReadOnlyList<Guid> FileIds, string CategoryId, string? Comment = null);

/// <summary>
/// A job for a job worker, e.g. publishing the stamped PDF of a released drawing. <see cref="VerifyUrl"/> is the
/// page the PDF's QR code opens to show whether that printout is still the current release.
/// </summary>
public sealed record JobInfo(
    long Id, string Kind, Guid FileId, int Version, string FileName, string FolderPath,
    string? Revision, string? State, DateTime ChangedAt, string VerifyUrl, int Attempts);

public sealed record ClaimJobRequest(IReadOnlyList<string> Kinds);

/// <summary>
/// The job's output: the manifest of content already uploaded to the chunk store. <see cref="Attempt"/> is the
/// claim being finished (<see cref="JobInfo.Attempts"/>), so a worker whose job was handed on can't finish it.
/// </summary>
public sealed record CompleteJobRequest(byte[] Manifest, int Attempt);

/// <summary>Why a job failed, in words an administrator can act on. <see cref="Retry"/> false for failures retrying won't fix.</summary>
public sealed record FailJobRequest(string Error, int Attempt, bool Retry = true);

public sealed record JobStatus(long Id, string Kind, Guid FileId, string FileName, int Version, string Status, int Attempts,
    string? LastError, string? Worker, DateTime CreatedAt, DateTime? FinishedAt);

/// <summary>The published PDF of a file: the newest version that has one, and the manifest to download it with.</summary>
public sealed record PublishedPdf(Guid FileId, int Version, long Length, byte[] Manifest);

/// <summary>One saved version of the vault's configuration.</summary>
public sealed record ConfigurationVersion(int Version, DateTime SavedAt, string SavedBy, string Comment);

/// <summary>Someone who has used the vault.</summary>
public sealed record UserInfo(string Username, string DisplayName, string? Site);
