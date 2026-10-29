using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Openhold.Client.Sdk;
using Openhold.Client.Sdk.Workspace;
using Openhold.Core.Contracts;
using Openhold.Desktop;

// Usage: Openhold.UiPreview <output folder> <vault folder, e.g. $/Electrical>
// Renders each Explorer dialog for the files in that folder, using this PC's Openhold settings. Nothing is changed.
if (args is not [var output, var folder])
{
    Console.Error.WriteLine("Usage: Openhold.UiPreview <output folder> <vault folder>");
    return 2;
}

var exit = 0;
var thread = new Thread(() => exit = Preview.Run(output, folder));
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
return exit;

internal static class Preview
{
    public static int Run(string output, string folder)
    {
        App.Unattended = true;
        var app = new App();
        app.InitializeComponent(); // the shared styles and brushes the dialogs use
        var settings = ClientSettings.Load();
        var client = settings.CreateClient();
        var workspace = new OpenholdWorkspace(settings.WorkspaceRoot, client);
        SetStatic(nameof(App.Settings), settings);
        SetStatic(nameof(App.Client), client);
        SetStatic(nameof(App.Workspace), workspace);
        App.Configuration = Task.Run(() => client.GetConfigurationAsync()).GetAwaiter().GetResult().Configuration;

        var files = Task.Run(() => workspace.GetFolderAsync(folder)).GetAwaiter().GetResult().Files
            .Where(f => f.File is not null).Select(f => new FileRow(f)).ToList();
        if (files.Count == 0)
        {
            Console.Error.WriteLine($"No vault files in {folder}.");
            return 1;
        }
        Directory.CreateDirectory(output);

        IPropertyWriter[] writers = [new Openhold.Client.Cad.InventorPropertyWriter(), new Openhold.Client.Cad.AutoCadTitleBlockWriter(settings.TitleBlock)];
        Render(new EditPropertiesWindow([(files[0].LocalPath, files[0].Status.File!)], writers), Path.Combine(output, "edit-properties-one.png"));
        Render(new EditPropertiesWindow(files.Select(f => (f.LocalPath, f.Status.File!)).ToList(), writers), Path.Combine(output, "edit-properties-many.png"));
        Render(new ReviseWindow(files, App.Configuration), Path.Combine(output, "revise.png"));
        Render(new ChangeStateWindow(files.Select(f => f.Status).ToList(), App.Configuration), Path.Combine(output, "change-state.png"));
        Render(new ChangeCategoryWindow(files.Count, files.Select(f => f.Status.File!.Category).Distinct().ToList(), App.Configuration), Path.Combine(output, "change-category.png"));
        Render(new ApprovalsWindow(), Path.Combine(output, "approvals.png"));
        Render(new CatalogueWindow(search: "breaker"), Path.Combine(output, "catalogue.png"));
        Render(new NewNumberWindow(), Path.Combine(output, "new-number.png"), w =>
        {
            var schemes = (System.Windows.Controls.ComboBox)w.FindName("Schemes");
            schemes.SelectedItem = schemes.Items.OfType<Openhold.Core.Contracts.NumberingScheme>().FirstOrDefault(s => s.Id == Environment.GetEnvironmentVariable("OPENHOLD_PREVIEW_SCHEME")) ?? schemes.SelectedItem;
        });
        Render(new CatalogueWindow(new Openhold.Core.Contracts.SaveItemRequest(files[0].Status.File!.Properties.GetValueOrDefault("Description") ?? "Bracket",
                Openhold.Core.Contracts.ItemKinds.Made, ClassId: "machined",
                CrossReferences: [new("Previous part number", files[0].Status.File!.Properties.GetValueOrDefault("Part Number") ?? files[0].Name)],
                Files: [new(files[0].Status.File!.Id) { Name = files[0].Name, FolderPath = "$/Tests" }])),
            Path.Combine(output, "catalogue-new.png"));
        // Administration, one picture per tab.
        for (var tab = 0; tab < 14; tab++)
        {
            var admin = new AdministrationWindow();
            var index = tab;
            admin.Loaded += async (_, _) =>
            {
                var tabs = (System.Windows.Controls.TabControl)admin.FindName("Tabs");
                tabs.SelectedIndex = index;
                if (tabs.SelectedItem is System.Windows.Controls.TabItem { Header: "Numbering" })
                {
                    await Task.Delay(800);
                    var list = (System.Windows.Controls.ListBox)admin.FindName("NumberingList");
                    var schemeId = Environment.GetEnvironmentVariable("OPENHOLD_PREVIEW_SCHEME") ?? "";
                    list.SelectedItem = list.Items.OfType<NumberingScheme>().FirstOrDefault(s => s.Id == schemeId) ?? list.Items.OfType<object>().FirstOrDefault();
                    // Optionally a part of the scheme, scrolled down to its settings, to see the lower half of the builder.
                    if (int.TryParse(Environment.GetEnvironmentVariable("OPENHOLD_PREVIEW_PART"), out var part))
                    {
                        ((System.Windows.Controls.ListBox)admin.FindName("PartList")).SelectedIndex = part;
                        await Task.Delay(200);
                        (((FrameworkElement)admin.FindName("NumberingEditor")).Parent as System.Windows.Controls.ScrollViewer)?.ScrollToVerticalOffset(330);
                    }
                    return;
                }
                if (tabs.SelectedItem != admin.FindName("NamingTab"))
                    return;
                // Naming Rules: try a part made inside an assembly in a machine's area folder.
                await Task.Delay(800);
                ((System.Windows.Controls.TextBox)admin.FindName("NamingTryFolder")).Text = Environment.GetEnvironmentVariable("OPENHOLD_PREVIEW_NAMING_FOLDER") ?? "$/Models/PR01 - Example/02 - Frame";
                ((System.Windows.Controls.TextBox)admin.FindName("NamingTryParent")).Text = Environment.GetEnvironmentVariable("OPENHOLD_PREVIEW_NAMING_PARENT") ?? "PR01-02-001A-00.iam";
                typeof(AdministrationWindow).GetMethod("TryNaming_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(admin, [admin, new RoutedEventArgs()]);
            };
            Render(admin, Path.Combine(output, $"admin-{tab}.png"));
        }
        // The main window showing the folder's files, as a search for the folder's own path would.
        var main = new MainWindow();
        main.Loaded += async (_, _) =>
        {
            await Task.Delay(1500); // after the window's own first folder load
            await (Task)typeof(MainWindow).GetMethod("ShowSearchAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(main, [Path.GetFileNameWithoutExtension(files[0].Name)[..6], null])!;
            ((System.Windows.Controls.DataGrid)typeof(MainWindow).GetField("Files", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(main)!).SelectedIndex = int.TryParse(Environment.GetEnvironmentVariable("OPENHOLD_PREVIEW_ROW"), out var row) ? row : 0; // shows the first file's details and preview
        };
        Render(main, Path.Combine(output, "main.png"));
        var uses = Task.Run(() => workspace.WithDependenciesAsync([files[0].LocalPath])).GetAwaiter().GetResult();
        var statuses = Task.Run(() => workspace.GetStatusAsync(uses)).GetAwaiter().GetResult();
        var schemes = Task.Run(() => client.ListNumberingSchemesAsync()).GetAwaiter().GetResult();
        Render(new CopyDesignWindow(statuses, App.Configuration, schemes), Path.Combine(output, "copy-design.png"));
        Render(new TrashWindow(), Path.Combine(output, "trash.png"));

        // The smaller dialogs, and the inline messages some of them show.
        var names = files.Select(f => f.Name).ToList();
        Render(new CheckInWindow(names.Take(1).ToList()), Path.Combine(output, "check-in-one.png"));
        Render(new CheckInWindow(names), Path.Combine(output, "check-in-many.png"));
        Render(new PromptWindow("Rename", "New name for the file", files[0].Name), Path.Combine(output, "prompt.png"));
        Render(new FolderPickerWindow($"Move {files.Count} files to which folder?"), Path.Combine(output, "folder-picker.png"));
        Render(new SettingsWindow(settings), Path.Combine(output, "settings.png"));
        Render(new SignInWindow(settings), Path.Combine(output, "sign-in.png"));
        Render(new SignInWindow(settings), Path.Combine(output, "sign-in-problem.png"),
            w => ShowMessage(w, "Problem", "That username and password don't match. Check them and try again."));
        Render(new AccountWindow(), Path.Combine(output, "account.png"), w =>
        {
            Field<UIElement>(w, "PasswordGroup").Visibility = Visibility.Visible; // as an Openhold account sees it
            Field<UIElement>(w, "TwoFactorGroup").Visibility = Visibility.Visible;
            ShowMessage(w, "Message", "Your password has been changed.");
            Field<System.Windows.Controls.TextBlock>(w, "Message").Foreground = (System.Windows.Media.Brush)w.FindResource("Brand");
        });
        Render(new NewAccountWindow(), Path.Combine(output, "new-account.png"),
            w => ShowMessage(w, "Problem", "There's already an account called jsmith."));
        Render(new ReviseWindow(files, App.Configuration), Path.Combine(output, "revise-problem.png"),
            w => ShowMessage(w, "Problem", "Give every file a new revision, or untick it."));
        Render(new ChangeStateWindow(files.Select(f => f.Status).ToList(), App.Configuration), Path.Combine(output, "change-state-problem.png"),
            w => ShowMessage(w, "Problem", "A comment is required for this change."));
        Render(MenuSample(), Path.Combine(output, "menu.png"));

        // Every status mark at once: made-up rows (nothing on the server changes), drawn by the main window's own columns.
        var states = new MainWindow();
        states.Loaded += async (_, _) =>
        {
            await Task.Delay(1500);
            var status = files[0].Status;
            var file = status.File!;
            FileRow Row(string name, FileState state, LocalCopy local, VaultFileInfo? info) =>
                new(status with { LocalPath = Path.Combine(Path.GetDirectoryName(status.LocalPath)!, name), State = state, LocalCopy = local, File = info });
            var grid = (System.Windows.Controls.DataGrid)typeof(MainWindow).GetField("Files", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(states)!;
            grid.ItemsSource = new List<FileRow>
            {
                Row("Up to date.iam", FileState.UpToDate, LocalCopy.UpToDate, file),
                Row("Checked out to me.ipt", FileState.CheckedOutToMe, LocalCopy.UpToDate, file with { LockedBy = "asmith", LockSite = "AU" }),
                Row("Checked out to someone else.ipt", FileState.CheckedOutToSomeoneElse, LocalCopy.Outdated, file with { LockedBy = "jsmith", LockSite = "NZ" }),
                Row("Newer version in vault.idw", FileState.Outdated, LocalCopy.Outdated, file),
                Row("Changed without check-out.dwg", FileState.ModifiedLocally, LocalCopy.ChangedHere, file),
                Row("Not on this PC.ipt", FileState.NotDownloaded, LocalCopy.None, file),
                Row("Not in vault yet.dwg", FileState.NotInVault, LocalCopy.OnlyHere, null),
                Row("Syncing from another site.ipt", FileState.Outdated, LocalCopy.Outdated, file with { Syncing = true }),
                Row("Syncing, not on this PC.dwg", FileState.NotDownloaded, LocalCopy.None, file with { Syncing = true }),
                Row("In review.idw", FileState.UpToDate, LocalCopy.UpToDate, file with { State = "In Review", StateLocked = true }),
                Row("Released.idw", FileState.UpToDate, LocalCopy.UpToDate, file with { State = "Released", StateLocked = true, StateReleased = true }),
                Row("Obsolete.ipt", FileState.UpToDate, LocalCopy.UpToDate, file with { State = "Obsolete", StateLocked = true, StateObsolete = true }),
            };
            grid.SelectedIndex = 2;
            grid.Focus();
        };
        Render(states, Path.Combine(output, "main-status-marks.png"));
        return 0;
    }

    /// <summary>Menus open in popups, which a window render doesn't include: this draws a menu's panel and items directly.</summary>
    private static Window MenuSample()
    {
        System.Windows.Controls.MenuItem Item(string header, string? glyph = null, string? gesture = null, bool enabled = true, bool check = false) => new()
        {
            Header = header,
            InputGestureText = gesture ?? "",
            IsEnabled = enabled,
            IsCheckable = check,
            IsChecked = check,
            Icon = glyph is null ? null : new System.Windows.Controls.TextBlock { Text = glyph, Style = (Style)Application.Current.FindResource("IconText") },
        };
        var items = new System.Windows.Controls.StackPanel { Width = 300 };
        System.Windows.Controls.Grid.SetIsSharedSizeScope(items, true);
        items.Children.Add(Item("_Open", ""));
        items.Children.Add(new System.Windows.Controls.Separator { Style = (Style)Application.Current.FindResource(System.Windows.Controls.MenuItem.SeparatorStyleKey) });
        items.Children.Add(Item("_Get Latest", "", "Ctrl+L"));
        var highlighted = Item("Check Ou_t", "", "Ctrl+E");
        items.Children.Add(highlighted);
        items.Children.Add(Item("Check _In…", "", "Ctrl+I", enabled: false));
        items.Children.Add(Item("Part Number", check: true));
        items.Children.Add(Item("_Delete", "", "Del"));
        var panel = new System.Windows.Controls.Border { Style = (Style)Application.Current.FindResource("PopupPanel"), Child = items, Margin = new Thickness(16) };
        var window = new Window { Content = panel, SizeToContent = SizeToContent.WidthAndHeight, Style = (Style)Application.Current.FindResource("AppWindow") };
        window.Loaded += (_, _) => typeof(System.Windows.Controls.MenuItem).GetProperty("IsHighlighted")!.SetValue(highlighted, true);
        return window;
    }

    private static T Field<T>(Window window, string name) =>
        (T)window.GetType().GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window)!;

    private static void ShowMessage(Window window, string name, string text)
    {
        var block = Field<System.Windows.Controls.TextBlock>(window, name);
        block.Text = text;
        block.Visibility = Visibility.Visible;
    }

    private static void SetStatic(string name, object value) =>
        typeof(App).GetProperty(name)!.SetValue(null, value);

    private static void Render(Window window, string path, Action<Window>? prepare = null)
    {
        // OPENHOLD_PREVIEW_HIGHCONTRAST=1 draws with the high-contrast colour mapping (using this PC's current system colours).
        // Applied before every window: pumping the dispatcher runs the app's own startup, which sets the PC's real setting.
        if (Environment.GetEnvironmentVariable("OPENHOLD_PREVIEW_HIGHCONTRAST") == "1")
            typeof(App).GetMethod("UseContrastColours", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(Application.Current, [true]);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -30000;
        window.ShowActivated = false;
        window.Show();
        // Let bindings, layout and Loaded handlers (some load data) finish before drawing.
        for (var i = 0; i < 60; i++)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(50);
        }
        if (prepare is not null)
        {
            prepare(window);
            for (var i = 0; i < 10; i++)
            {
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Thread.Sleep(50);
            }
        }

        // The window's own background and padding too, not just its content.
        var content = System.Windows.Media.VisualTreeHelper.GetChildrenCount(window) > 0
            ? (FrameworkElement)System.Windows.Media.VisualTreeHelper.GetChild(window, 0)
            : (FrameworkElement)window.Content;
        var width = (int)Math.Ceiling(content.ActualWidth);
        var height = (int)Math.Ceiling(content.ActualHeight);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            context.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path))
            encoder.Save(stream);
        window.Close();
        Console.WriteLine($"{path} ({width}x{height})");
    }
}
