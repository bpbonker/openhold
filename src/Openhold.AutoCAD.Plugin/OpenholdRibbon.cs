using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Windows;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Brushes = System.Windows.Media.Brushes;
using FlowDirection = System.Windows.FlowDirection;

namespace Openhold.AutoCAD.Plugin;

/// <summary>
/// The Openhold ribbon tab, so nobody has to remember command names. Buttons run the same commands as
/// typing them. Icons are drawn from the Windows icon font, so there are no image files to ship.
/// </summary>
internal static class OpenholdRibbon
{
    private const string TabId = "OPENHOLD_TAB";

    private static readonly Color Brand = Color.FromRgb(0x1F, 0x5F, 0x8B);

    public static void Install()
    {
        if (ComponentManager.Ribbon is { } ribbon)
            AddTab(ribbon);
        else
            ComponentManager.ItemInitialized += OnItemInitialized;
    }

    /// <summary>Puts the tab back if a workspace switch rebuilt the ribbon without it.</summary>
    public static void EnsureTab()
    {
        if (ComponentManager.Ribbon is { } ribbon)
            AddTab(ribbon);
    }

    private static void OnItemInitialized(object? sender, RibbonItemEventArgs e)
    {
        if (ComponentManager.Ribbon is not { } ribbon)
            return;
        ComponentManager.ItemInitialized -= OnItemInitialized;
        AddTab(ribbon);
    }

    private static void AddTab(RibbonControl ribbon)
    {
        if (ribbon.FindTab(TabId) is not null)
            return;

        var tab = new RibbonTab { Title = "Openhold", Id = TabId };

        var edit = new RibbonPanelSource { Title = "Check out / in" };
        edit.Items.Add(Button("Check Out", "OPENHOLDCHECKOUT", '', "Check out to edit",
            "Gets the latest version and makes this drawing editable. Nobody else can change it until you check it in. AutoCAD Electrical projects are checked out as a whole set.", large: true));
        edit.Items.Add(Button("Check In", "OPENHOLDCHECKIN", '', "Check in your changes",
            "Saves your changes to the vault as a new version, with a comment, and lets others edit the drawing again. The revision is never changed for you.", large: true));
        edit.Items.Add(Button("Undo Check Out", "OPENHOLDUNDO", '', "Release without checking in",
            "Gives the drawing back without saving a new version and restores the vault's copy. Your changes are kept in a backup.", large: true));

        var vault = new RibbonPanelSource { Title = "Vault" };
        var row = new RibbonRowPanel();
        row.Items.Add(Button("Get Latest", "OPENHOLDGET", '', "Get the latest version",
            "Updates this drawing to the newest version in the vault.", large: false));
        row.Items.Add(new RibbonRowBreak());
        row.Items.Add(Button("Status", "OPENHOLDSTATUS", '', "Where does this drawing stand?",
            "Tells you whether the drawing is up to date, checked out to you, or checked out to someone else.", large: false));
        vault.Items.Add(row);

        tab.Panels.Add(new RibbonPanel { Source = edit });
        tab.Panels.Add(new RibbonPanel { Source = vault });
        ribbon.Tabs.Add(tab);
    }

    private static RibbonButton Button(string text, string command, char glyph, string tipTitle, string tipText, bool large) => new()
    {
        Text = text,
        Id = "OPENHOLD_" + command,
        ShowText = true,
        ShowImage = true,
        Size = large ? RibbonItemSize.Large : RibbonItemSize.Standard,
        Orientation = large ? System.Windows.Controls.Orientation.Vertical : System.Windows.Controls.Orientation.Horizontal,
        LargeImage = Icon(glyph, 32),
        Image = Icon(glyph, 16),
        CommandHandler = new RunCommand(command),
        ToolTip = new RibbonToolTip { Title = tipTitle, Content = tipText, Command = command },
    };

    /// <summary>A white glyph on a rounded brand-coloured square.</summary>
    private static BitmapSource Icon(char glyph, int size)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var radius = size / 5.0;
            context.DrawRoundedRectangle(new SolidColorBrush(Brand), null, new Rect(0, 0, size, size), radius, radius);
            var text = new FormattedText(glyph.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe Fluent Icons, Segoe MDL2 Assets"), size * 0.6, Brushes.White, 1.0);
            context.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2));
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private sealed class RunCommand(string command) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) =>
            Application.DocumentManager.MdiActiveDocument?.SendStringToExecute($"_{command} ", true, false, true);
    }
}
