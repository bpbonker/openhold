using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Inventor;
using Openhold.Client.Sdk;
using Openhold.Client.Sdk.Workspace;
using Openhold.Client.Ui;
using Openhold.Core.Properties;
using Application = Inventor.Application;
using File = System.IO.File;
using FileInfo = System.IO.FileInfo;
using InventorFile = Inventor.File;
using Path = System.IO.Path;

namespace Openhold.Inventor.AddIn;

/// <summary>
/// Check-out / check-in from Inventor. Commands act on the active document and every file it uses inside
/// the Openhold workspace, taken from the file reference graph (not just what Inventor has loaded: express
/// mode, suppressed components and model states leave files unloaded). Every result is shown in plain words;
/// nothing here ever changes a revision.
/// </summary>
internal sealed partial class InventorCommands(Application inventor, OpenholdWorkspace workspace, ClientSettings settings)
{
    private IntPtr Owner => new(inventor.MainFrameHWND);

    /// <summary>Runs a command so that no failure can escape into Inventor, and says what happened instead.</summary>
    public void Run(Action command)
    {
        try
        {
            command();
        }
        catch (HttpRequestException)
        {
            Prompts.Inform(Owner, $"Couldn't reach the Openhold server at {settings.ServerUrl}. Nothing was changed. Check your network and try again.");
        }
        catch (OpenholdApiException ex)
        {
            Prompts.Inform(Owner, ex.Message);
        }
        catch (COMException ex)
        {
            Prompts.Inform(Owner, $"Inventor reported a problem, and nothing was changed: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Prompts.Inform(Owner, $"Openhold hit a problem, and nothing was changed: {ex.Message}");
        }
        finally
        {
            inventor.StatusBarText = "";
        }
    }

    /// <summary>
    /// A workspace file that is read-only on disk is in the vault but not checked out to this PC
    /// (Openhold keeps such files read-only), so it must be checked out before it changes.
    /// </summary>
    public bool NeedsCheckOut(string fullFileName) =>
        workspace.Contains(fullFileName) && File.Exists(fullFileName) && new FileInfo(fullFileName).IsReadOnly;

    public void OfferCheckOut(string path)
    {
        var name = Path.GetFileName(path);
        // A released file, or one someone else has, can't be checked out: say so rather than offer something that fails.
        IReadOnlyList<FileStatus>? statuses = null;
        Run(() => statuses = Work("Checking", () => workspace.GetStatusAsync([path])));
        if (statuses?.FirstOrDefault() is not { } status)
            return; // the server couldn't be reached; Run has said so
        if (status.File is { StateLocked: true } locked)
        {
            Prompts.Inform(Owner, $"{name} is {locked.State}, so it's read-only. To change it, move it back to an editable state in Openhold Explorer " +
                "(Change State, e.g. Reopen for a new revision), then check it out.");
            return;
        }
        if (status.State == FileState.CheckedOutToSomeoneElse)
        {
            Prompts.Inform(Owner, $"{name} is checked out to {status.File?.LockedBy}, so it's read-only until they check it in.");
            return;
        }
        if (Prompts.Confirm(Owner, "Check out to edit",
                $"{name} isn't checked out to you, so it can't be changed yet.\n\nCheck it out now? Then repeat your last change."))
        {
            Run(() => CheckOutPaths([path]));
        }
    }

    public void CheckOut()
    {
        if (ActiveWorkspaceDocument() is { } document)
            CheckOutPaths([document.FullFileName]);
    }

    private void CheckOutPaths(IReadOnlyList<string> paths)
    {
        var result = Work("Checking out", () => workspace.CheckOutAsync(paths));
        // Newer versions were downloaded over files Inventor already has open: reload them from disk.
        if (result.Succeeded && result.UpdatedFiles.Count > 0)
            inventor.FileManager.RefreshAllDocuments();
        Report(result);
    }

    /// <summary>
    /// Checks in the active document together with every file it uses that is checked out to you or new,
    /// e.g. parts added to an assembly, recording iProperties and the references between them.
    /// </summary>
    public void CheckIn()
    {
        if (ActiveWorkspaceDocument() is not { } document)
            return;

        var graph = WorkspaceFileGraph(document);
        var statuses = Work("Checking status", () => workspace.GetStatusAsync(graph.Keys));
        var toCheckIn = statuses.Where(s => s.State is FileState.CheckedOutToMe or FileState.NotInVault).Select(s => s.LocalPath).ToList();
        if (toCheckIn.Count == 0)
        {
            Prompts.Inform(Owner, $"Nothing here is checked out to you or new, so there's nothing to check in.\n\n{statuses[0].Description}");
            return;
        }

        // Read everything from Inventor here, on its own thread, before any background work starts.
        using var documents = new DocumentAccess(inventor);
        var properties = toCheckIn.ToDictionary(p => p, p => ReadProperties(documents.Get(p)), StringComparer.OrdinalIgnoreCase);
        var references = toCheckIn.ToDictionary(p => p, p => DirectReferences(graph[p]), StringComparer.OrdinalIgnoreCase);

        var mainPath = toCheckIn.Contains(document.FullFileName, StringComparer.OrdinalIgnoreCase) ? document.FullFileName : toCheckIn[0];
        var title = toCheckIn.Count == 1 ? Path.GetFileName(mainPath) : $"{Path.GetFileName(mainPath)} and {toCheckIn.Count - 1} related file(s)";
        using var dialog = new CheckInDialog(title, [], properties[mainPath]);
        if (dialog.ShowDialog(new Win32Owner(Owner)) != DialogResult.OK)
        {
            Prompts.Inform(Owner, "Check-in cancelled. Nothing was changed.");
            return;
        }

        foreach (var path in toCheckIn)
        {
            if (documents.GetIfLoaded(path) is { Dirty: true } dirty)
                dirty.Save2(false);
        }

        // After saving, so previews show what is being checked in; made here because thumbnail handlers need this thread.
        var thumbnails = toCheckIn.ToDictionary(p => p, p => ShellThumbnails.TryCreatePng(p), StringComparer.OrdinalIgnoreCase);
        var options = new CheckInOptions
        {
            KeepCheckedOut = dialog.KeepCheckedOut,
            Properties = path => properties.GetValueOrDefault(path),
            References = path => references.GetValueOrDefault(path),
            Thumbnail = path => thumbnails.GetValueOrDefault(path),
        };
        Report(Work("Checking in", () => workspace.CheckInAsync(toCheckIn, dialog.Comment, options)));
    }

    public void UndoCheckOut()
    {
        if (ActiveWorkspaceDocument() is not { } document)
            return;
        var path = document.FullFileName;
        if (!Prompts.Confirm(Owner, "Release without checking in",
                $"Release {Path.GetFileName(path)} without checking in?\n\nThe vault's copy will be restored. Unsaved changes in Inventor are discarded; " +
                "saved changes are kept in a backup in case you need them."))
        {
            return;
        }

        document.Close(true);
        var result = Work("Releasing", () => workspace.UndoCheckOutAsync([path]));
        inventor.Documents.Open(path, true);
        Report(result);
    }

    public void GetLatest()
    {
        if (ActiveWorkspaceDocument() is not { } document)
            return;
        var graph = WorkspaceFileGraph(document);
        using (var documents = new DocumentAccess(inventor))
        {
            if (graph.Keys.Any(p => documents.GetIfLoaded(p) is { Dirty: true }))
            {
                Prompts.Inform(Owner, "Save your changes first, then Get Latest. (Getting the latest versions reopens the document.)");
                return;
            }
        }

        var statuses = Work("Checking status", () => workspace.GetStatusAsync(graph.Keys));
        var stale = statuses.Where(s => s.State is FileState.Outdated or FileState.NotDownloaded or FileState.ModifiedLocally).Select(s => s.LocalPath).ToList();
        if (stale.Count == 0)
        {
            Prompts.Inform(Owner, "Everything here is up to date.");
            return;
        }

        var path = document.FullFileName;
        document.Close(true);
        var result = Work("Getting the latest versions", () => workspace.GetLatestAsync(stale));
        inventor.Documents.Open(path, true);
        Report(result);
    }

    public void Status()
    {
        if (ActiveWorkspaceDocument() is not { } document)
            return;
        var statuses = Work("Checking status", () => workspace.GetStatusAsync(WorkspaceFileGraph(document).Keys));
        var main = statuses[0];
        var others = statuses.Skip(1).GroupBy(s => s.State).Select(g => $"{g.Count()} {Describe(g.Key)}").ToList();
        Prompts.Inform(Owner, others.Count == 0
            ? main.Description
            : $"{main.Description}\n\nFiles it uses: {string.Join(", ", others)}.");
    }

    private static string Describe(FileState state) => state switch
    {
        FileState.UpToDate => "up to date",
        FileState.CheckedOutToMe => "checked out to you",
        FileState.CheckedOutToSomeoneElse => "checked out to someone else",
        FileState.Outdated => "with a newer version in the vault",
        FileState.NotDownloaded => "not on this PC",
        FileState.ModifiedLocally => "changed without check-out",
        FileState.NotInVault => "not in the vault yet",
        _ => state.ToString(),
    };

    private Document? ActiveWorkspaceDocument()
    {
        var document = inventor.ActiveDocument;
        if (document is null)
        {
            Prompts.Inform(Owner, "Open a document first.");
            return null;
        }
        if (string.IsNullOrEmpty(document.FullFileName))
        {
            Prompts.Inform(Owner, $"Save this document inside your Openhold workspace ({settings.WorkspaceRoot}) first.");
            return null;
        }
        if (!workspace.Contains(document.FullFileName))
        {
            Prompts.Inform(Owner, $"{Path.GetFileName(document.FullFileName)} isn't in your Openhold workspace ({settings.WorkspaceRoot}). Save it inside the workspace to put it in the vault.");
            return null;
        }
        return document;
    }

    /// <summary>
    /// The document and every file it uses (at any depth) that lives in the workspace, by full path, with the
    /// active document first. Built from file references, so files Inventor hasn't loaded are included too.
    /// </summary>
    private Dictionary<string, InventorFile> WorkspaceFileGraph(Document document)
    {
        var graph = new Dictionary<string, InventorFile>(StringComparer.OrdinalIgnoreCase) { [document.FullFileName] = document.File };
        foreach (InventorFile file in document.File.AllReferencedFiles)
        {
            if (workspace.Contains(file.FullFileName))
                graph.TryAdd(file.FullFileName, file);
        }
        return graph;
    }

    private static IReadOnlyList<string> DirectReferences(InventorFile file)
    {
        var references = new List<string>();
        foreach (FileDescriptor descriptor in file.ReferencedFileDescriptors)
        {
            if (!descriptor.ReferenceMissing)
                references.Add(descriptor.FullFileName);
        }
        return references;
    }

    /// <summary>
    /// The iProperties Openhold records with each version: every standard one in Inventor's iProperties dialog (see
    /// <see cref="InventorProperties"/>, recorded under the dialog's names; calculated mass properties only when
    /// Inventor says they're up to date) and every custom one.
    /// </summary>
    private static Dictionary<string, string> ReadProperties(Document document)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var massValid = Value(document, InventorProperties.DesignTrackingSet, "Valid MassProps") is not (0 or null);
        foreach (var definition in InventorProperties.All)
        {
            if (definition.Unit is not null && !massValid)
                continue;
            if (InventorProperties.Format(definition, Value(document, definition.Set, definition.Internal)) is { } value)
                properties[definition.Name] = value;
        }
        try
        {
            foreach (Property property in document.PropertySets[InventorProperties.CustomSet])
            {
                if (PropertyValues.Format(Value(property)) is { } value)
                    properties.TryAdd(property.Name, value);
            }
        }
        catch (COMException)
        {
            // not every document type has a custom set
        }
        return properties;
    }

    private static object? Value(Document document, string set, string name)
    {
        try
        {
            return Value(document.PropertySets[set][name]);
        }
        catch (COMException)
        {
            return null; // not every document type has every set
        }
    }

    private static object? Value(Property property)
    {
        try
        {
            return property.Value;
        }
        catch (COMException)
        {
            return null; // some values (e.g. linked or expression-driven ones) can't be read outside a model update
        }
    }

    /// <summary>Runs Openhold's async work off Inventor's UI thread and shows progress in the status bar.</summary>
    private T Work<T>(string label, Func<Task<T>> work)
    {
        inventor.StatusBarText = $"Openhold: {label}...";
        return UiThreadBridge.Run(work);
    }

    private void Report(WorkspaceResult result)
    {
        inventor.StatusBarText = result.Summary;
        var details = string.Join("\n", result.Files.Select(f => "•  " + f.Message));
        Prompts.Inform(Owner, details.Length == 0 ? result.Summary : $"{result.Summary}\n\n{details}");
    }

    /// <summary>
    /// Gets documents by path: one already in memory if Inventor has it, otherwise opened invisibly and
    /// closed again on dispose, so reading a file's iProperties never leaves extra documents open.
    /// </summary>
    private sealed class DocumentAccess(Application inventor) : IDisposable
    {
        private readonly List<Document> _opened = [];

        public Document? GetIfLoaded(string path)
        {
            foreach (Document document in inventor.Documents)
            {
                if (string.Equals(document.FullFileName, path, StringComparison.OrdinalIgnoreCase))
                    return document;
            }
            return null;
        }

        public Document Get(string path)
        {
            if (GetIfLoaded(path) is { } loaded)
                return loaded;
            var opened = inventor.Documents.Open(path, false);
            _opened.Add(opened);
            return opened;
        }

        public void Dispose()
        {
            foreach (var document in _opened)
                document.Close(true);
            _opened.Clear();
        }
    }

    private sealed class Win32Owner(IntPtr handle) : IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }
}
