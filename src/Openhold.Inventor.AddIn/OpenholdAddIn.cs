using System.Runtime.InteropServices;
using Inventor;
using Openhold.Client.Sdk;
using Openhold.Client.Sdk.Workspace;
using Openhold.Client.Ui;
using Application = Inventor.Application;
using Path = System.IO.Path;

namespace Openhold.Inventor.AddIn;

/// <summary>
/// Inventor entry point. Adds the Openhold ribbon tab and enforces check-out before editing:
/// the moment a document that isn't checked out is about to change, the edit is stopped and the user
/// is offered a check-out, the same flow Autodesk Vault users know, with nothing lost.
/// </summary>
[ComVisible(true)]
[Guid(ClientId)]
public sealed class OpenholdAddIn : ApplicationAddInServer
{
    public const string ClientId = "1B56EE6F-9CA9-441D-967D-8C325E417AB9";

    private static readonly string[] Ribbons = ["ZeroDoc", "Part", "Assembly", "Drawing"];

    private readonly List<ButtonDefinition> _buttons = [];
    private Application _inventor = null!;
    private InventorCommands _commands = null!;

    // Kept as fields so the COM event sinks stay alive for the session.
    private FileAccessEvents? _fileEvents;
    private ApplicationEvents? _appEvents;

    public object? Automation => null;

    public void Activate(ApplicationAddInSite addInSiteObject, bool firstTime)
    {
        _inventor = addInSiteObject.Application;
        var settings = ClientSettings.Load();
        var workspace = new OpenholdWorkspace(settings.WorkspaceRoot, settings.CreateClient());
        _commands = new InventorCommands(_inventor, workspace, settings);

        AddRibbon();

        _fileEvents = _inventor.FileAccessEvents;
        _fileEvents.OnFileDirty += OnFileDirty;
        _appEvents = _inventor.ApplicationEvents;
        _appEvents.OnSaveDocument += OnSaveDocument;
    }

    public void Deactivate()
    {
        if (_fileEvents is not null)
            _fileEvents.OnFileDirty -= OnFileDirty;
        if (_appEvents is not null)
            _appEvents.OnSaveDocument -= OnSaveDocument;
        foreach (var button in _buttons)
            Marshal.FinalReleaseComObject(button);
        _buttons.Clear();
    }

    public void ExecuteCommand(int commandID)
    {
    }

    private void AddRibbon()
    {
        var save = Button("Save to Openhold", "SaveToOpenhold", '',
            "Save a new document into the vault folder of your choice, named by the vault's naming rules: the number fills in from the folders and the assembly it's made in.",
            _commands.SaveToOpenhold);
        var checkOut = Button("Check Out", "CheckOut", '', "Check out to edit. Gets the latest version first; nobody else can change it until you check it in.", _commands.CheckOut);
        var checkIn = Button("Check In", "CheckIn", '', "Save your changes to the vault as a new version, with a comment. New parts in an assembly are added too.", _commands.CheckIn);
        var undo = Button("Undo Check Out", "Undo", '', "Release without checking in and restore the vault's copy. Your changes are kept in a backup.", _commands.UndoCheckOut);
        var getLatest = Button("Get Latest", "GetLatest", '', "Update this document and everything it uses to the newest versions in the vault.", _commands.GetLatest);
        var status = Button("Status", "Status", '', "Where do this document and its parts stand?", _commands.Status);

        foreach (var ribbonName in Ribbons)
        {
            var ribbon = _inventor.UserInterfaceManager.Ribbons[ribbonName];
            var tab = ribbon.RibbonTabs.Add("Openhold", $"Openhold.Tab.{ribbonName}", ClientId);
            if (ribbonName != "ZeroDoc")
                tab.RibbonPanels.Add("Save", $"Openhold.Panel.Save.{ribbonName}", ClientId).CommandControls.AddButton(save, true);
            var editPanel = tab.RibbonPanels.Add("Check out / in", $"Openhold.Panel.Edit.{ribbonName}", ClientId);
            editPanel.CommandControls.AddButton(checkOut, true);
            editPanel.CommandControls.AddButton(checkIn, true);
            editPanel.CommandControls.AddButton(undo, true);
            var vaultPanel = tab.RibbonPanels.Add("Vault", $"Openhold.Panel.Vault.{ribbonName}", ClientId);
            vaultPanel.CommandControls.AddButton(getLatest, true);
            vaultPanel.CommandControls.AddButton(status, true);
        }
    }

    private ButtonDefinition Button(string text, string id, char glyph, string tooltip, Action run)
    {
        var button = _inventor.CommandManager.ControlDefinitions.AddButtonDefinition(
            text, $"Openhold.{id}", CommandTypesEnum.kNonShapeEditCmdType, "{" + ClientId + "}", tooltip, tooltip,
            GlyphIcons.Create(glyph, 16), GlyphIcons.Create(glyph, 32), ButtonDisplayEnum.kAlwaysDisplayText);
        button.OnExecute += _ => _commands.Run(run);
        _buttons.Add(button);
        return button;
    }

    /// <summary>Stops an edit to a document that isn't checked out, and offers to check it out.</summary>
    private void OnFileDirty(string relativeFileName, string libraryName, ref byte[] customLogicalName, string fullFileName,
        _Document documentObject, EventTimingEnum beforeOrAfter, NameValueMap context, out HandlingCodeEnum handlingCode)
    {
        handlingCode = HandlingCodeEnum.kEventNotHandled;
        if (beforeOrAfter != EventTimingEnum.kBefore || !_commands.NeedsCheckOut(fullFileName))
            return;

        handlingCode = HandlingCodeEnum.kEventCanceled;
        _commands.OfferCheckOut(fullFileName);
    }

    /// <summary>Saving over a file that isn't checked out is stopped with an explanation instead of a Windows error.</summary>
    private void OnSaveDocument(_Document documentObject, EventTimingEnum beforeOrAfter, NameValueMap context, out HandlingCodeEnum handlingCode)
    {
        handlingCode = HandlingCodeEnum.kEventNotHandled;
        if (beforeOrAfter != EventTimingEnum.kBefore || !_commands.NeedsCheckOut(documentObject.FullFileName))
            return;

        handlingCode = HandlingCodeEnum.kEventCanceled;
        Prompts.Inform(new IntPtr(_inventor.MainFrameHWND),
            $"{Path.GetFileName(documentObject.FullFileName)} isn't checked out to you, so it wasn't saved. " +
            "Click Check Out on the Openhold tab, then save.");
    }
}
