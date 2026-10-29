using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using Openhold.Client.Sdk;
using Openhold.Client.Sdk.Workspace;
using CoreApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: ExtensionApplication(typeof(Openhold.AutoCAD.Plugin.OpenholdPlugin))]

namespace Openhold.AutoCAD.Plugin;

/// <summary>
/// Plugin entry point. Watches documents so users are told, in plain words, when a drawing is read-only
/// because it isn't checked out, and stops saves over drawings they don't hold. Uses only core APIs here,
/// so loading in accoreconsole (headless indexing) stays safe.
/// </summary>
public sealed class OpenholdPlugin : IExtensionApplication
{
    private static readonly Lazy<(ClientSettings Settings, OpenholdWorkspace Workspace)> Session = new(() =>
    {
        var settings = ClientSettings.Load();
        return (settings, new OpenholdWorkspace(settings.WorkspaceRoot, settings.CreateClient()));
    });

    public static ClientSettings Settings => Session.Value.Settings;

    public static OpenholdWorkspace Workspace => Session.Value.Workspace;

    public void Initialize()
    {
        var documents = CoreApplication.DocumentManager;
        documents.DocumentCreated += OnDocumentCreated;
        documents.DocumentLockModeChanged += OnDocumentLockModeChanged;
        CoreApplication.SystemVariableChanged += OnSystemVariableChanged;

        // No ribbon when running headless in accoreconsole.
        if (!IsHeadless)
            InstallRibbon();
    }

    private static bool IsHeadless =>
        System.Diagnostics.Process.GetCurrentProcess().ProcessName.Equals("accoreconsole", StringComparison.OrdinalIgnoreCase);

    // Separate method so the ribbon assembly is only loaded when a ribbon exists.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void InstallRibbon()
    {
        try
        {
            OpenholdRibbon.Install();
        }
        catch (System.Exception)
        {
            // The commands still work without the ribbon.
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void InstallRibbonAgain() => OpenholdRibbon.EnsureTab();

    private static readonly HashSet<string> HintedDrawings = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The first time someone changes a drawing that isn't checked out, say so straight away, rather than
    /// letting them discover it when saving fails.
    /// </summary>
    private static void OnSystemVariableChanged(object? sender, Autodesk.AutoCAD.ApplicationServices.SystemVariableChangedEventArgs e)
    {
        try
        {
            if (string.Equals(e.Name, "WSCURRENT", StringComparison.OrdinalIgnoreCase) && !IsHeadless)
            {
                // Switching workspace rebuilds the ribbon; put the Openhold tab back.
                InstallRibbonAgain();
                return;
            }
            if (!string.Equals(e.Name, "DBMOD", StringComparison.OrdinalIgnoreCase))
                return;
            var document = CoreApplication.DocumentManager.MdiActiveDocument;
            if (document is not { IsReadOnly: true } || !IsInWorkspace(document.Name) || !HintedDrawings.Add(document.Name))
                return;
            document.Editor.WriteMessage(
                $"\nOpenhold: {Path.GetFileName(document.Name)} isn't checked out, so these changes can't be saved yet. " +
                "Type OPENHOLDCHECKOUT and your changes will be carried over.\n");
        }
        catch (System.Exception)
        {
            // A hint must never interfere with drawing.
        }
    }

    public void Terminate()
    {
    }

    public static bool IsInWorkspace(string path) => Workspace.Contains(path);

    private static void OnDocumentCreated(object? sender, DocumentCollectionEventArgs e)
    {
        try
        {
            if (e.Document is { IsReadOnly: true } document && IsInWorkspace(document.Name))
            {
                document.Editor.WriteMessage(
                    $"\nOpenhold: {Path.GetFileName(document.Name)} is read-only because it isn't checked out to you. Type OPENHOLDCHECKOUT to edit it.\n");
            }
        }
        catch (System.Exception)
        {
            // A hint must never interfere with opening a drawing.
        }
    }

    private static void OnDocumentLockModeChanged(object? sender, DocumentLockModeChangedEventArgs e)
    {
        try
        {
            if (e.GlobalCommandName is not ("QSAVE" or "SAVE"))
                return;
            var document = e.Document;
            if (!IsInWorkspace(document.Name) || !File.Exists(document.Name) || !new FileInfo(document.Name).IsReadOnly)
                return;

            e.Veto();
            document.Editor.WriteMessage(
                $"\nOpenhold: {Path.GetFileName(document.Name)} isn't checked out to you, so it wasn't saved. " +
                "Your changes are still open. Type OPENHOLDCHECKOUT and they'll be carried over, then save as normal.\n");
        }
        catch (System.Exception)
        {
            // Never block AutoCAD because of an Openhold problem.
        }
    }
}
