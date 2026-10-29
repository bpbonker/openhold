using System.Runtime.InteropServices;
using System.Windows.Forms;
using Inventor;
using Openhold.Client.Sdk;
using Openhold.Client.Sdk.Workspace;
using Openhold.Client.Ui;
using Openhold.Core.Properties;
using Directory = System.IO.Directory;
using File = System.IO.File;
using Path = System.IO.Path;

namespace Openhold.Inventor.AddIn;

/// <summary>
/// Save to Openhold: the first save of a new document, named by the vault's naming rules where it goes. Fields fill in
/// from the folders and, for a component made inside an assembly, from that assembly's number, so nobody needs to
/// get a number from Openhold Explorer first.
/// </summary>
internal sealed partial class InventorCommands
{
    private OpenholdClient? _client;
    private string? _lastFolder;

    private OpenholdClient Client => _client ??= settings.CreateClient();

    public void SaveToOpenhold()
    {
        var top = inventor.ActiveDocument;
        if (top is null)
        {
            Prompts.Inform(Owner, "Open or create a document first.");
            return;
        }
        // Editing a component in place: that component is what's being saved, inside the assembly being edited.
        var document = inventor.ActiveEditDocument as Document ?? top;
        var name = document.DisplayName;
        var saved = !string.IsNullOrEmpty(document.FullFileName) && File.Exists(document.FullFileName);
        if (saved && workspace.Contains(document.FullFileName)
            && Work("Checking", () => workspace.GetStatusAsync([document.FullFileName]))[0].State != FileState.NotInVault)
        {
            Prompts.Inform(Owner, $"{Path.GetFileName(document.FullFileName)} is already in the vault. To rename or move it, use Rename or Move in Openhold Explorer: " +
                "it updates every assembly and drawing that uses it.");
            return;
        }

        if (document is DrawingDocument drawing && SaveDrawingBesideModel(drawing))
            return;

        var parent = ParentAssembly(top, document);
        var parentPath = parent?.FullFileName is { Length: > 0 } p && workspace.Contains(p) ? p : null;
        var extension = ExtensionOf(document);
        var folder = parentPath is not null ? FolderOf(parentPath)
            : saved && workspace.Contains(document.FullFileName) ? FolderOf(document.FullFileName)
            : _lastFolder ?? "$/";

        var (configuration, schemes) = Work("Reading the naming rules", async () =>
            ((await Client.GetConfigurationAsync()).Configuration, await Client.ListNumberingSchemesAsync()));
        using var dialog = new SaveToOpenholdDialog(name, extension, folder, parentPath is null ? null : Path.GetFileName(parentPath), configuration, schemes,
            (scheme, fields) => UiThreadBridge.Run(() => Client.PreviewNumberAsync(scheme.Id, fields)).Next,
            BrowseFolder);
        if (dialog.ShowDialog(new Win32Owner(Owner)) != DialogResult.OK)
            return;

        // Take the number only now, so cancelling never uses one up.
        var reserved = dialog.Scheme is { } chosen
            ? Work("Taking the number", () => Client.ReserveNumbersAsync(chosen.Id, 1, dialog.Fields)).Single()
            : null;
        var fileName = (reserved?.Formatted ?? dialog.FreeName) + extension;
        var path = Path.Combine(workspace.ToLocalPath(dialog.Folder), fileName);
        try
        {
            if (File.Exists(path))
                throw new InvalidOperationException($"{fileName} already exists in {dialog.Folder}.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            SetIfEmpty(document, "Part Number", Path.GetFileNameWithoutExtension(fileName), name);
            if (dialog.Description.Length > 0)
                Set(document, "Description", dialog.Description);
            document.SaveAs(path, false);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // Nothing was saved under the number: give it back so it can be used again.
            if (reserved is not null)
                Work("Returning the number", () => Client.ReleaseNumbersAsync([reserved.Formatted]));
            throw;
        }
        _lastFolder = dialog.Folder;
        inventor.StatusBarText = $"Openhold: saved {fileName}";
        Prompts.Inform(Owner, $"Saved as {fileName} in {dialog.Folder}.\n\nIt's in your workspace but not in the vault yet: Check In puts it there" +
            (parentPath is null ? "." : ", together with the assembly."));
    }

    /// <summary>
    /// A drawing is named after its model and kept beside it (the Autodesk Vault convention). Returns false when it has no
    /// model in the workspace yet, so it's named like any other file.
    /// </summary>
    private bool SaveDrawingBesideModel(DrawingDocument drawing)
    {
        var model = drawing.ReferencedDocuments.Cast<Document>().FirstOrDefault(d => d.FullFileName is { Length: > 0 } f && workspace.Contains(f));
        if (model is null)
            return false;
        var extension = ExtensionOf((Document)drawing);
        var path = Path.ChangeExtension(model.FullFileName, extension);
        if (File.Exists(path))
        {
            Prompts.Inform(Owner, $"{Path.GetFileName(path)} already exists beside its model. Open that drawing, or save this one under another name.");
            return true;
        }
        if (!Prompts.Confirm(Owner, "Save drawing", $"Save the drawing as {Path.GetFileName(path)}, beside its model in {FolderOf(model.FullFileName)}?"))
            return true;
        ((Document)drawing).SaveAs(path, false);
        Prompts.Inform(Owner, $"Saved as {Path.GetFileName(path)}. Check In puts it in the vault.");
        return true;
    }

    /// <summary>The assembly a component being edited in place belongs to: its parent sub-assembly, or the top assembly.</summary>
    private static Document? ParentAssembly(Document top, Document document)
    {
        if (ReferenceEquals(top, document) || top is not AssemblyDocument assembly)
            return null;
        var occurrence = assembly.ComponentDefinition.ActiveOccurrence;
        return occurrence?.ParentOccurrence?.Definition.Document as Document ?? top;
    }

    private static string ExtensionOf(Document document)
    {
        if (Path.GetExtension(document.FullFileName) is { Length: > 0 } existing)
            return existing.ToLowerInvariant();
        return document.DocumentType switch
        {
            DocumentTypeEnum.kAssemblyDocumentObject => ".iam",
            DocumentTypeEnum.kDrawingDocumentObject => ".idw",
            DocumentTypeEnum.kPresentationDocumentObject => ".ipn",
            _ => ".ipt",
        };
    }

    private string FolderOf(string localPath) => workspace.ToVaultPath(Path.GetDirectoryName(localPath)!);

    /// <summary>Picks a folder inside the workspace; returns its vault path.</summary>
    private string? BrowseFolder(string current)
    {
        var start = workspace.ToLocalPath(current);
        using var picker = new FolderBrowserDialog
        {
            Description = "Choose the vault folder to save in",
            UseDescriptionForTitle = true,
            InitialDirectory = Directory.Exists(start) ? start : workspace.Root,
            ShowNewFolderButton = true,
        };
        if (picker.ShowDialog(new Win32Owner(Owner)) != DialogResult.OK)
            return null;
        if (!workspace.Contains(Path.Combine(picker.SelectedPath, "x")))
        {
            Prompts.Inform(Owner, $"Choose a folder inside your Openhold workspace ({workspace.Root}).");
            return null;
        }
        return workspace.ToVaultPath(picker.SelectedPath);
    }

    /// <summary>Sets a Design Tracking iProperty only if it's empty or still Inventor's placeholder (the document's name).</summary>
    private static void SetIfEmpty(Document document, string name, string value, string placeholder)
    {
        var current = Value(document, InventorProperties.DesignTrackingSet, name) as string;
        if (string.IsNullOrWhiteSpace(current) || string.Equals(current, placeholder, StringComparison.OrdinalIgnoreCase))
            Set(document, name, value);
    }

    private static void Set(Document document, string name, string value) =>
        document.PropertySets[InventorProperties.DesignTrackingSet][name].Value = value;
}
