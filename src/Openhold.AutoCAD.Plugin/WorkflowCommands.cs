using System.Globalization;
using System.IO;
using System.Net.Http;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Openhold.Client.Sdk;
using Openhold.Client.Sdk.Workspace;
using Openhold.Client.Ui;
using Openhold.Core.Properties;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(Openhold.AutoCAD.Plugin.WorkflowCommands))]

namespace Openhold.AutoCAD.Plugin;

/// <summary>
/// Check-out / check-in from inside AutoCAD. Every message is written for the person at the keyboard,
/// and every failure ends with what to do next. Check-in never changes the drawing's revision.
/// </summary>
public sealed class WorkflowCommands
{
    [CommandMethod("OPENHOLDCHECKOUT", CommandFlags.Session)]
    public static void CheckOut() => Run((document, editor) =>
    {
        var path = document.Name;
        if (!document.IsReadOnly)
        {
            // Already editable (checked out, or a new drawing): just confirm the lock with the server.
            Report(editor, Work(editor, $"Checking out {Path.GetFileName(path)}", () => OpenholdPlugin.Workspace.CheckOutAsync([path])));
            return;
        }

        // Close first: if the local copy is out of date, Openhold replaces the file, which Windows won't allow
        // while AutoCAD holds it open. Unsaved edits are kept in a backup before closing.
        var unsavedBackup = IsModified() ? SaveUnsavedCopy(document) : null;
        document.CloseAndDiscard();

        var result = Work(null, $"Checking out {Path.GetFileName(path)}", () => OpenholdPlugin.Workspace.CheckOutAsync([path]));

        // If the copy you were editing was already the latest version, carry your edits straight into the
        // checked-out drawing. If a newer version had to be downloaded, keep them aside instead of
        // silently overwriting someone else's work.
        var editsCarriedOver = false;
        if (result.Succeeded && unsavedBackup is not null && !result.UpdatedFiles.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            File.Copy(unsavedBackup, path, overwrite: true);
            editsCarriedOver = true;
        }

        var reopened = Application.DocumentManager.Open(path, !result.Succeeded);
        Application.DocumentManager.MdiActiveDocument = reopened;
        Report(reopened.Editor, result);
        if (editsCarriedOver)
            reopened.Editor.WriteMessage("\nThe changes you made before checking out have been kept.\n");
        else if (unsavedBackup is not null)
            reopened.Editor.WriteMessage($"\nA newer version was in the vault, so it was opened instead of your copy. The changes you made before checking out were kept in {unsavedBackup}.\n");
        if (result.Succeeded)
            reopened.Editor.WriteMessage($"\nOpenhold: {Path.GetFileName(path)} is open and ready to edit.\n");
    });

    [CommandMethod("OPENHOLDCHECKIN", CommandFlags.Session)]
    public static void CheckIn() => Run((document, editor) =>
    {
        var path = document.Name;
        if (document.IsReadOnly)
        {
            editor.WriteMessage($"\nOpenhold: {Path.GetFileName(path)} isn't checked out to you, so there is nothing to check in. Type OPENHOLDCHECKOUT to edit it.\n");
            return;
        }

        // Read the title blocks from the drawing as it is now, before asking anything else,
        // so a sheet left on an old revision is the first thing the user hears about.
        var index = DrawingIndex.Read(document.Database);
        var settings = OpenholdPlugin.Settings.TitleBlock;
        var reading = new TitleBlockMapping(settings.BlockNamePattern, settings.Attributes, settings.SheetTitleAttribute)
            .Read(index.AttributedBlocks.Select(b => new BlockAttributes(b.BlockName, b.Layout, b.Attributes)));

        var properties = new Dictionary<string, string>(reading.Properties, StringComparer.OrdinalIgnoreCase);
        if (reading.Sheets.Count > 0)
            properties["Sheet Count"] = reading.Sheets.Count.ToString(CultureInfo.InvariantCulture);

        using var dialog = new CheckInDialog(Path.GetFileName(path), reading.Mismatches.Select(m => m.Describe()).ToList(), properties);
        if (Application.ShowModalDialog(Application.MainWindow.Handle, dialog, false) != System.Windows.Forms.DialogResult.OK)
        {
            Cancelled(editor);
            return;
        }
        var comment = dialog.Comment;
        var keep = dialog.KeepCheckedOut;

        if (IsModified())
        {
            using (document.LockDocument())
                document.Database.SaveAs(path, true, DwgVersion.Current, document.Database.SecurityParameters);
        }

        var folder = Path.GetDirectoryName(path)!;
        var references = index.References
            .Where(r => !string.IsNullOrWhiteSpace(r.StoredPath))
            .Select(r => Path.GetFullPath(Path.IsPathRooted(r.StoredPath) ? r.StoredPath : Path.Combine(folder, r.StoredPath)))
            .Where(OpenholdPlugin.IsInWorkspace)
            .ToList();

        // The drawing was saved above, so the preview reflects what is being checked in.
        var thumbnail = ShellThumbnails.TryCreatePng(path);
        var options = new CheckInOptions
        {
            KeepCheckedOut = keep,
            Properties = p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase) ? properties : null,
            // Only this drawing was read; the rest of its project set keeps the references it had.
            References = p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase) ? references : null,
            Thumbnail = p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase) ? thumbnail : null,
        };
        var result = Work(editor, $"Checking in {Path.GetFileName(path)}",
            () => OpenholdPlugin.Workspace.CheckInAsync([path], comment, options));
        Report(editor, result);
        if (result.Succeeded)
            editor.WriteMessage(comment.Length > 0 ? $"Comment recorded: \"{comment}\"\n" : "No comment recorded.\n");

        if (result.Succeeded && !keep)
        {
            // Reopen read-only so the editor matches the file: checked in means no longer editable.
            document.CloseAndDiscard();
            Application.DocumentManager.MdiActiveDocument = Application.DocumentManager.Open(path, true);
        }
    });

    [CommandMethod("OPENHOLDUNDO", CommandFlags.Session)]
    public static void UndoCheckOut() => Run((document, editor) =>
    {
        var path = document.Name;
        if (!Prompts.Confirm(Application.MainWindow.Handle, "Release without checking in",
                $"Release {Path.GetFileName(path)} without checking in?\n\nThe vault's copy will be restored. Your changes will be kept in a backup in case you need them."))
        {
            editor.WriteMessage("\nOpenhold: nothing was changed. The drawing is still checked out to you.\n");
            return;
        }

        document.CloseAndDiscard();
        var result = Work(null, $"Releasing {Path.GetFileName(path)}", () => OpenholdPlugin.Workspace.UndoCheckOutAsync([path]));
        var reopened = Application.DocumentManager.Open(path, true);
        Application.DocumentManager.MdiActiveDocument = reopened;
        Report(reopened.Editor, result);
    });

    [CommandMethod("OPENHOLDGET", CommandFlags.Session)]
    public static void GetLatest() => Run((document, editor) =>
    {
        var path = document.Name;
        var status = Work(editor, "Checking status", () => OpenholdPlugin.Workspace.GetStatusAsync([path])).Single();
        if (status.State is FileState.UpToDate or FileState.CheckedOutToMe or FileState.NotInVault)
        {
            editor.WriteMessage($"\nOpenhold: {status.Description}\n");
            return;
        }

        var readOnly = document.IsReadOnly;
        document.CloseAndDiscard();
        var result = Work(null, $"Getting the latest {Path.GetFileName(path)}", () => OpenholdPlugin.Workspace.GetLatestAsync([path]));
        var reopened = Application.DocumentManager.Open(path, readOnly);
        Application.DocumentManager.MdiActiveDocument = reopened;
        Report(reopened.Editor, result);
    });

    [CommandMethod("OPENHOLDSTATUS")]
    public static void Status() => Run((document, editor) =>
    {
        var status = Work(editor, "Checking status", () => OpenholdPlugin.Workspace.GetStatusAsync([document.Name])).Single();
        editor.WriteMessage($"\nOpenhold: {status.Description}\n");
    });

    private static void Run(Action<Document, Editor> command)
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        if (document is null)
            return;
        var editor = document.Editor;
        try
        {
            if (!OpenholdPlugin.IsInWorkspace(document.Name))
            {
                editor.WriteMessage($"\nOpenhold: this drawing isn't in your Openhold workspace ({OpenholdPlugin.Settings.WorkspaceRoot}). Save it inside the workspace to put it in the vault.\n");
                return;
            }
            command(document, editor);
        }
        catch (HttpRequestException)
        {
            editor.WriteMessage($"\nOpenhold: couldn't reach the Openhold server ({OpenholdPlugin.Settings.ServerUrl}). Nothing was changed. Check your network and try again.\n");
        }
        catch (OpenholdApiException ex)
        {
            editor.WriteMessage($"\nOpenhold: {ex.Message}\n");
        }
        catch (System.Exception ex)
        {
            editor.WriteMessage($"\nOpenhold hit an unexpected problem and nothing was changed: {ex.Message}\n");
        }
    }

    /// <summary>
    /// Runs Openhold's server work on the thread pool (never on AutoCAD's UI thread, which would deadlock)
    /// while a status-bar progress meter shows that something is happening.
    /// </summary>
    private static T Work<T>(Editor? editor, string label, Func<Task<T>> work)
    {
        editor?.WriteMessage($"\nOpenhold: {label}...");
        var task = Task.Run(work);
        var meter = new ProgressMeter();
        meter.Start($"Openhold: {label}");
        meter.SetLimit(100);
        try
        {
            var tick = 0;
            while (!task.Wait(100))
            {
                if (++tick % 100 == 0)
                    meter.SetLimit(100);
                meter.MeterProgress();
            }
        }
        finally
        {
            meter.Stop();
        }
        return task.GetAwaiter().GetResult();
    }

    private static void Cancelled(Editor editor) =>
        editor.WriteMessage("\nOpenhold: check-in cancelled. Nothing was changed, and the drawing is still checked out to you.\n");

    private static void Report(Editor editor, WorkspaceResult result)
    {
        editor.WriteMessage($"\nOpenhold: {result.Summary}");
        foreach (var file in result.Files)
            editor.WriteMessage($"\n  {file.Message}");
        editor.WriteMessage("\n");
    }

    private static bool IsModified() => Convert.ToInt32(Application.GetSystemVariable("DBMOD"), CultureInfo.InvariantCulture) != 0;

    /// <summary>Saves the in-memory drawing to a backup before it is closed, so no edit is ever lost.</summary>
    private static string SaveUnsavedCopy(Document document)
    {
        var backup = OpenholdPlugin.Workspace.NewBackupPath(document.Name, "unsaved-");
        using (document.LockDocument())
            document.Database.SaveAs(backup, DwgVersion.Current);
        return backup;
    }
}
