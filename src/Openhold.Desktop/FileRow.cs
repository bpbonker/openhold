using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Openhold.Client.Sdk.Workspace;

namespace Openhold.Desktop;

/// <summary>One file in the file list, with its status in words, a colour and an icon.</summary>
/// <param name="folderPath">Shown for search results, which span folders.</param>
/// <param name="match">Why a search found this file, when its name doesn't say.</param>
public sealed class FileRow(FileStatus status, string? folderPath = null, string? match = null)
{
    public FileStatus Status { get; } = status;

    public string? FolderPath { get; } = folderPath?.TrimStart('$', '/').Replace("/", " › ");

    public string? Match { get; } = match;

    /// <summary>Property values for the configurable columns; a property the file doesn't have reads as empty.</summary>
    public PropertyLookup Prop { get; } = new(status.File?.Properties);

    public string Name => Path.GetFileName(Status.LocalPath);

    /// <summary>What screen readers and UI automation announce for the row.</summary>
    public override string ToString() => Name;

    public string LocalPath => Status.LocalPath;

    public string Version => Status.File is { LatestVersion: > 0 } file ? $"v{file.LatestVersion}" : "";

    /// <summary>The lifecycle state, e.g. Work in Progress or Released; empty for files without a lifecycle.</summary>
    public string State => Status.File?.State ?? "";

    public string Category => Status.File?.CategoryName ?? "";

    public string? StateTip => Status.File switch
    {
        { StateLocked: true, State: { } state } => $"{state}: can't be checked out until it's moved back to an editable state.",
        { State: { } state } => state,
        _ => null,
    };

    public Brush StateForeground => Status.File switch
    {
        { StateObsolete: true } => Brushes.DimGray,
        { StateReleased: true } => Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x6B, 0x30))),
        { StateLocked: true } => Freeze(new SolidColorBrush(Color.FromRgb(0x8A, 0x55, 0x00))),
        _ => Blue,
    };

    public Visibility StateVisibility => State.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string CheckedOutBy => Status switch
    {
        { State: FileState.CheckedOutToMe } => "You",
        { File: { LockedBy: { } user, LockSite: { Length: > 0 } site } } => $"{user} ({site})",
        { File.LockedBy: { } user } => user,
        _ => "",
    };

    public string Modified => Status.File?.ModifiedAt?.ToLocalTime().ToString("d MMM yyyy h:mm tt", CultureInfo.CurrentCulture)
        ?? (File.Exists(LocalPath) ? File.GetLastWriteTime(LocalPath).ToString("d MMM yyyy h:mm tt", CultureInfo.CurrentCulture) : "");

    public string Size
    {
        get
        {
            var bytes = Status.File?.Length ?? (File.Exists(LocalPath) ? new FileInfo(LocalPath).Length : 0);
            return bytes switch
            {
                0 => "",
                < 1024 => $"{bytes} B",
                < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
                _ => $"{bytes / (1024.0 * 1024):0.0} MB",
            };
        }
    }

    /// <summary>Someone at another site checked in a newer version that's still on its way to this site's server.</summary>
    public bool Syncing => Status.File?.Syncing == true;

    public string StatusText => Syncing ? $"{BaseStatusText} (arriving from another site)" : BaseStatusText;

    private string BaseStatusText => Status.State switch
    {
        FileState.UpToDate => "Up to date",
        FileState.CheckedOutToMe => "Checked out to you",
        FileState.CheckedOutToSomeoneElse => $"Checked out to {Status.File?.LockedBy}",
        FileState.Outdated => "Newer version in vault",
        FileState.NotDownloaded => "Not on this PC",
        FileState.ModifiedLocally => "Changed without check-out",
        FileState.NotInVault => "Not in vault yet",
        _ => "",
    };

    // ---- Status bubble and check-out mark, as Autodesk Vault shows them ----

    private static readonly Brush Green = Freeze(new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x44)));
    private static readonly Brush Red = Freeze(new SolidColorBrush(Color.FromRgb(0xD9, 0x30, 0x25)));
    private static readonly Brush Amber = Freeze(new SolidColorBrush(Color.FromRgb(0xB8, 0x6E, 0x00)));
    private static readonly Brush Blue = Freeze(new SolidColorBrush(Color.FromRgb(0x1F, 0x5F, 0x8B)));
    private static readonly Brush Hollow = Freeze(new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)));

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    /// <summary>The bubble's fill: green up to date, red out of date, amber changed here; white when not on this PC.</summary>
    public Brush BubbleFill => Status.LocalCopy switch
    {
        LocalCopy.UpToDate => Green,
        LocalCopy.Outdated => Red,
        LocalCopy.ChangedHere => Amber,
        _ => Brushes.White,
    };

    public Brush BubbleStroke => Status.LocalCopy switch
    {
        LocalCopy.UpToDate => Green,
        LocalCopy.Outdated => Red,
        LocalCopy.ChangedHere => Amber,
        _ => Hollow,
    };

    /// <summary>A mark inside the bubble as well as its colour, so out of date and changed read without colour.</summary>
    public string BubbleMark => Syncing ? "↻" : Status.LocalCopy switch // ↻ still arriving at this site
    {
        LocalCopy.Outdated => "\u2193", // ↓ get the newer version
        LocalCopy.ChangedHere => "!",
        _ => "",
    };

    /// <summary>White on a filled bubble; blue on a hollow one (not on this PC), so the mark still shows.</summary>
    public Brush BubbleMarkBrush => Status.LocalCopy is LocalCopy.UpToDate or LocalCopy.Outdated or LocalCopy.ChangedHere ? Brushes.White : Blue;

    /// <summary>Dashed outline for a file that's only on this PC (not in the vault yet).</summary>
    public DoubleCollection? BubbleDash => Status.LocalCopy == LocalCopy.OnlyHere ? [2, 1.5] : null;

    public string BubbleTip => Syncing
        ? "Syncing: a newer version checked in at another site is still arriving here. Get Latest works now, but fetches it from that site."
        : Status.LocalCopy switch
    {
        LocalCopy.UpToDate => "On this PC: the latest version",
        LocalCopy.Outdated => "On this PC, but out of date: Get Latest to update it",
        LocalCopy.ChangedHere => "On this PC, changed without being checked out",
        LocalCopy.OnlyHere => "Only on this PC: not in the vault yet",
        _ => "Not on this PC",
    };

    /// <summary>The check-out mark: an X, blue when it's checked out to you, red when to someone else.</summary>
    public Visibility CheckOutVisibility => Status.State is FileState.CheckedOutToMe or FileState.CheckedOutToSomeoneElse ? Visibility.Visible : Visibility.Hidden;

    public Brush CheckOutBrush => Status.State == FileState.CheckedOutToMe ? Blue : Red;

    public string? CheckOutTip => Status.State switch
    {
        FileState.CheckedOutToMe => "Checked out to you",
        FileState.CheckedOutToSomeoneElse => $"Checked out to {Status.File?.LockedBy}",
        _ => null,
    };

    /// <summary>The lifecycle state's label colours: green released, amber locked (e.g. In Review), grey obsolete, blue editable.</summary>
    public Brush StateBackground => Status.File switch
    {
        { StateObsolete: true } => Freeze(new SolidColorBrush(Color.FromRgb(0xEE, 0xEF, 0xF1))),
        { StateReleased: true } => Freeze(new SolidColorBrush(Color.FromRgb(0xE3, 0xF4, 0xE6))),
        { StateLocked: true } => Freeze(new SolidColorBrush(Color.FromRgb(0xFD, 0xF1, 0xDC))),
        _ => Freeze(new SolidColorBrush(Color.FromRgb(0xE6, 0xEF, 0xF7))),
    };
}

/// <summary>Indexer for binding columns to properties by name (<c>Prop[Part Number]</c>), empty when absent.</summary>
public sealed class PropertyLookup(IReadOnlyDictionary<string, string>? properties)
{
    public string this[string name] => properties is not null && properties.TryGetValue(name, out var value) ? value : "";
}

/// <summary>One version in the History tab.</summary>
public sealed record HistoryRow(int Version, string When, string Who, string Comment, string State);
