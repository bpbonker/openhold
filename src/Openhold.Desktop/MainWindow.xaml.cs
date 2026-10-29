using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Openhold.Client.Sdk;
using Openhold.Client.Sdk.Workspace;
using Openhold.Core.Contracts;

namespace Openhold.Desktop;

public partial class MainWindow : Window
{
    private readonly FolderNode _root = new("Vault", "$", null);
    private FolderNode _current;

    /// <summary>The search being shown, or null when browsing a folder.</summary>
    private string? _search;

    private const string EmptyFolderText = "This folder is empty.\nDrag files here from Windows Explorer to add them to the vault.";

    public MainWindow()
    {
        InitializeComponent();
        _current = _root;
        Identity.Text = $"Signed in as {App.Client.Username}  ·  {App.Settings.Vault ?? "default vault"} on {App.Settings.ServerUrl.TrimEnd('/')}";
        Folders.ItemsSource = new[] { _root };
        ApplyPropertyColumns();
        _root.IsExpanded = true;
        Loaded += async (_, _) =>
        {
            await LoadConfigurationAsync();
            await ShowFolderAsync(_root);
        };
        KeyDown += async (_, e) =>
        {
            var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
            var ctrlShift = Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift);
            if (ctrl && e.Key == Key.L && GetLatestButton.IsEnabled)
                GetLatest_Click(this, new RoutedEventArgs());
            else if (ctrl && e.Key == Key.E && CheckOutButton.IsEnabled)
                CheckOut_Click(this, new RoutedEventArgs());
            else if (ctrl && e.Key == Key.I && CheckInButton.IsEnabled)
                CheckIn_Click(this, new RoutedEventArgs());
            else if (ctrlShift && e.Key == Key.A)
                AddFiles_Click(this, new RoutedEventArgs());
            else if (ctrlShift && e.Key == Key.N)
                NewFolder_Click(this, new RoutedEventArgs());
            else if (ctrlShift && e.Key == Key.C && Selected.Count > 0)
                CopyPath_Click(this, new RoutedEventArgs());
            else if (e.Key == Key.F5)
                await RefreshAsync();
            else if (ctrl && e.Key == Key.F)
                SearchBox.Focus();
            else if (e.Key == Key.Delete && Files.IsKeyboardFocusWithin && Selected.Count > 0)
                Delete_Click(this, new RoutedEventArgs());
            else if (e.Key == Key.F2 && Files.IsKeyboardFocusWithin && Selected.Count == 1)
                Rename_Click(this, new RoutedEventArgs());
        };
        UpdateButtons();
    }

    /// <summary>Enter opens the selected file, as in Windows Explorer and Autodesk Vault (the grid would move down a row).</summary>
    private async void Files_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None)
            return;
        e.Handled = true;
        if (OpenButton.IsEnabled)
            await OpenAsync();
    }

    private IReadOnlyList<FileRow> Selected => Files.SelectedItems.Cast<FileRow>().ToList();

    // ---- Navigation ----

    private async void Folders_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderNode { VaultPath.Length: > 0 } node)
            await ShowFolderAsync(node);
    }

    private async Task ShowFolderAsync(FolderNode node, IReadOnlyCollection<string>? reselect = null)
    {
        _current = node;
        _search = null;
        FolderPath.Text = node.VaultPath == "$" ? "Vault" : node.VaultPath.TrimStart('$', '/').Replace("/", "  ›  ");
        List<FileRow> rows;
        using (Busy())
            rows = (await App.Workspace.GetFolderAsync(node.VaultPath)).Files.Select(f => new FileRow(f)).ToList();
        ShowRows(rows, searching: false, EmptyFolderText, reselect);
        await ShowDetailsAsync();
    }

    private async Task ShowSearchAsync(string text, IReadOnlyCollection<string>? reselect = null)
    {
        _search = text;
        var scope = SearchThisFolder.IsChecked == true ? _current.VaultPath : null;
        WorkspaceSearchResults results;
        using (Busy())
            results = await App.Workspace.SearchAsync(text, scope);

        var where = scope is null ? "the vault" : _current.Name;
        var count = results.Hits.Count == 1 ? "1 file" : $"{results.Hits.Count} files";
        FolderPath.Text = results.Truncated
            ? $"Search \"{text}\" in {where}: showing the first {count}. Add more words to narrow it down."
            : $"Search \"{text}\" in {where}: {count}";
        ShowRows(results.Hits.Select(h => new FileRow(h.Status, h.FolderPath, h.Match)).ToList(), searching: true,
            $"Nothing in {where} matches \"{text}\".\nTry fewer or shorter words, or * as a wildcard.", reselect);
        await ShowDetailsAsync();
    }

    private void ShowRows(List<FileRow> rows, bool searching, string emptyText, IReadOnlyCollection<string>? reselect)
    {
        Files.ItemsSource = rows;
        FolderColumn.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        MatchColumn.Visibility = searching && rows.Any(r => !string.IsNullOrEmpty(r.Match)) ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Text = emptyText;
        EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var row in rows.Where(r => reselect?.Contains(r.LocalPath, StringComparer.OrdinalIgnoreCase) == true))
            Files.SelectedItems.Add(row);
        UpdateButtons();
    }

    /// <summary>
    /// Reads the vault's configuration and how many changes wait for this person's approval. Failure isn't fatal:
    /// browsing works without it, and the next refresh tries again.
    /// </summary>
    private async Task LoadConfigurationAsync()
    {
        try
        {
            var configuration = App.Client.GetConfigurationAsync();
            var requests = App.Client.ListChangeRequestsAsync();
            App.Configuration = (await configuration).Configuration;
            var waiting = (await requests).Count(r => r.CanApprove);
            ApprovalsText.Text = waiting == 0 ? "Approvals" : $"Approvals ({waiting})";
            ApprovalsText.FontWeight = waiting == 0 ? FontWeights.Normal : FontWeights.SemiBold;
        }
        catch (Exception ex) when (ex is Client.Sdk.OpenholdApiException or System.Net.Http.HttpRequestException)
        {
            ApprovalsText.Text = "Approvals";
        }
    }

    private async Task RefreshAsync()
    {
        await LoadConfigurationAsync();
        var selected = Selected.Select(r => r.LocalPath).ToList();
        if (_search is not null)
        {
            await ShowSearchAsync(_search, selected);
            return;
        }
        await _current.LoadChildrenAsync();
        await ShowFolderAsync(_current, selected);
    }

    // ---- Search ----

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            await ShowSearchAsync(SearchBox.Text.Trim());
        }
        else if (e.Key == Key.Escape)
        {
            SearchBox.Clear();
            await ShowFolderAsync(_current);
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void Files_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateButtons();
        await ShowDetailsAsync();
    }

    // ---- Actions ----

    private async void GetLatest_Click(object sender, RoutedEventArgs e) =>
        await RunAsync("Getting the latest versions", () => App.Workspace.GetLatestAsync(Selected.Select(r => r.LocalPath)));

    private async void GetLatestWithUses_Click(object sender, RoutedEventArgs e)
    {
        var paths = await App.Workspace.WithDependenciesAsync(Selected.Select(r => r.LocalPath));
        await RunAsync($"Getting the latest of {paths.Count} files", () => App.Workspace.GetLatestAsync(paths));
    }

    private async void CheckOutWithUses_Click(object sender, RoutedEventArgs e)
    {
        var paths = await App.Workspace.WithDependenciesAsync(Selected.Select(r => r.LocalPath));
        await RunAsync($"Checking out {paths.Count} files", () => App.Workspace.CheckOutAsync(paths));
    }

    private async void CheckOut_Click(object sender, RoutedEventArgs e) =>
        await RunAsync("Checking out", () => App.Workspace.CheckOutAsync(Selected.Select(r => r.LocalPath)));

    private async void CheckIn_Click(object sender, RoutedEventArgs e)
    {
        var rows = Selected.Where(r => r.Status.State is FileState.CheckedOutToMe or FileState.NotInVault).ToList();
        await CheckInAsync(rows.Select(r => r.LocalPath).ToList());
    }

    private async Task CheckInAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return;
        var dialog = new CheckInWindow(paths.Select(Path.GetFileName).OfType<string>().ToList()) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            StatusLine.Text = "Check-in cancelled. Nothing was changed.";
            return;
        }
        // Previews are made here on the UI thread: some Windows thumbnail handlers need it.
        var thumbnails = paths.ToDictionary(p => p, p => Openhold.Client.Ui.ShellThumbnails.TryCreatePng(p), StringComparer.OrdinalIgnoreCase);
        await RunAsync("Checking in", () => App.Workspace.CheckInAsync(paths, dialog.Comment,
            new CheckInOptions { KeepCheckedOut = dialog.KeepCheckedOut, Thumbnail = p => thumbnails.GetValueOrDefault(p) }));
    }

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        var rows = Selected.Where(r => r.Status.State == FileState.CheckedOutToMe).ToList();
        if (rows.Count == 0)
            return;
        var what = rows.Count == 1 ? rows[0].Name : $"{rows.Count} files";
        var answer = MessageBox.Show(this,
            $"Release {what} without checking in?\n\nThe vault's copy will be restored. Your changes will be kept in a backup in case you need them.",
            "Openhold: Undo Check Out", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes)
            await RunAsync("Releasing", () => App.Workspace.UndoCheckOutAsync(rows.Select(r => r.LocalPath)));
    }

    private async void Open_Click(object sender, RoutedEventArgs e) => await OpenAsync();

    private async void Files_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: FileRow })
            await OpenAsync();
    }

    private async Task OpenAsync()
    {
        if (Selected is not [var row])
            return;
        if (row.Status.State is FileState.NotDownloaded or FileState.Outdated)
        {
            var result = await RunAsync("Getting the latest version", () => App.Workspace.GetLatestAsync([row.LocalPath]));
            if (!result.Succeeded)
                return;
        }
        if (!File.Exists(row.LocalPath))
            return;
        Process.Start(new ProcessStartInfo(row.LocalPath) { UseShellExecute = true });
        StatusLine.Text = row.Status.State == FileState.CheckedOutToMe
            ? $"Opened {row.Name}."
            : $"Opened {row.Name} read-only. Check it out to edit it.";
    }

    private void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is [var row] && File.Exists(row.LocalPath))
            Process.Start("explorer.exe", $"/select,\"{row.LocalPath}\"");
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var rows = Selected;
        if (rows.Count == 0)
            return;
        var what = rows.Count == 1 ? rows[0].Name : $"{rows.Count} files";
        var answer = MessageBox.Show(this,
            $"Delete {what}?\n\nFiles from the vault go to the Trash and can be restored with their full history. " +
            "Files that are only on this PC are moved to the backup folder.",
            "Openhold: Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes)
            await RunAsync("Deleting", () => App.Workspace.DeleteAsync(rows.Select(r => r.LocalPath)));
    }

    /// <summary>How Explorer repairs references when files are renamed or moved: Inventor files, AutoCAD Electrical projects and DWG xrefs.</summary>
    private static readonly IReferenceRepairer[] Repairers =
        [new AcadeProjectRepairer(), new Openhold.Client.Cad.InventorReferenceRepairer(), new Openhold.Client.Cad.AutoCadReferenceRepairer()];

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not [var row])
            return;
        var dialog = new PromptWindow("Rename", $"New name for {row.Name}:", row.Name) { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Value) || dialog.Value.Trim() == row.Name)
            return;
        var newName = dialog.Value.Trim();
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(this, $"\"{newName}\" can't be used as a file name.", "Openhold", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!string.Equals(Path.GetExtension(newName), Path.GetExtension(row.Name), StringComparison.OrdinalIgnoreCase)
            && MessageBox.Show(this, $"Change the file type from {Path.GetExtension(row.Name)} to {Path.GetExtension(newName)}? The file may not open any more.",
                "Openhold: Rename", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }
        await RunAsync($"Renaming {row.Name}", () => Task.Run(() => App.Workspace.RenameAsync(row.LocalPath, newName, Repairers)));
    }

    private async void Move_Click(object sender, RoutedEventArgs e)
    {
        var rows = Selected;
        if (rows.Count == 0)
            return;
        var what = rows.Count == 1 ? rows[0].Name : $"{rows.Count} files";
        var picker = new FolderPickerWindow($"Move {what} to:") { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedVaultPath is not { } target)
            return;
        await RunAsync($"Moving {what}", () => Task.Run(() => App.Workspace.MoveAsync(rows.Select(r => r.LocalPath).ToList(), target, Repairers)));
    }

    /// <summary>How Explorer writes edited properties into files: iProperties through Apprentice, title blocks through accoreconsole.</summary>
    private static IPropertyWriter[] PropertyWriters =>
        [new Openhold.Client.Cad.InventorPropertyWriter(), new Openhold.Client.Cad.AutoCadTitleBlockWriter(App.Settings.TitleBlock)];

    private async void EditProperties_Click(object sender, RoutedEventArgs e)
    {
        var rows = Selected.Where(r => r.Status.File is not null).ToList();
        if (rows.Count == 0)
            return;
        var writers = PropertyWriters;
        var dialog = new EditPropertiesWindow(rows.Select(r => (r.LocalPath, r.Status.File!)).ToList(), writers) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        var changes = rows.ToDictionary(r => r.LocalPath, _ => dialog.Changes);
        var what = rows.Count == 1 ? rows[0].Name : $"{rows.Count} files";
        // Off the UI thread: writing title blocks runs AutoCAD's console for a few seconds per drawing.
        await RunAsync($"Updating the properties of {what}",
            () => Task.Run(() => App.Workspace.EditPropertiesAsync(changes, dialog.VersionComment, writers)));
    }

    private void Catalogue_Click(object sender, RoutedEventArgs e) => new CatalogueWindow { Owner = this }.ShowDialog();

    /// <summary>Tools › New Number: a scheme's next number; the selected file's number is offered as the parent for child numbers.</summary>
    private void NewNumber_Click(object sender, RoutedEventArgs e) =>
        new NewNumberWindow(Selected is [{ } one] ? System.IO.Path.GetFileNameWithoutExtension(one.Name).Split(" - ", 2)[0] : null) { Owner = this }.ShowDialog();

    /// <summary>
    /// Opens the file's catalogue item, or starts a new one from it: its description, its current part number kept as a
    /// cross-reference, and the file linked. Then offers to fill the file's properties in from the new item.
    /// </summary>
    private async void AddToCatalogue_Click(object sender, RoutedEventArgs e)
    {
        if (Selected.FirstOrDefault(r => r.Status.File is not null) is not { Status.File: { } file } row)
            return;
        try
        {
            if (await App.Client.GetItemsForFileAsync(file.Id) is [var existing, ..])
            {
                new CatalogueWindow(search: existing.Number) { Owner = this }.ShowDialog();
                return;
            }
        }
        catch (OpenholdApiException ex)
        {
            StatusLine.Text = ex.Message;
            return;
        }

        var extension = System.IO.Path.GetExtension(file.Name).ToLowerInvariant();
        var role = extension switch { ".idw" or ".dwg" or ".dxf" => "drawing", ".pdf" => "datasheet", ".ipt" or ".iam" or ".step" or ".stp" => "model", _ => "other" };
        var oldNumber = file.Properties.GetValueOrDefault("Part Number");
        var draft = new SaveItemRequest(
            file.Properties.GetValueOrDefault("Description") is { Length: > 0 } description ? description : System.IO.Path.GetFileNameWithoutExtension(file.Name),
            ItemKinds.Made,
            CrossReferences: oldNumber is { Length: > 0 } ? [new CrossReference("Previous part number", oldNumber)] : null,
            Files: [new ItemFileLink(file.Id, role) { Name = file.Name, FolderPath = App.Workspace.ToVaultPath(System.IO.Path.GetDirectoryName(row.LocalPath)!) }]);
        var catalogue = new CatalogueWindow(draft) { Owner = this };
        catalogue.ShowDialog();
        if (catalogue.SavedItem is { } item)
            await FillFromCatalogueAsync(row, file, item);
    }

    /// <summary>Right-click › Fill iProperties from Catalogue: the file's item's number, description and supplier details.</summary>
    private async void FillFromCatalogue_Click(object sender, RoutedEventArgs e)
    {
        if (Selected.FirstOrDefault(r => r.Status.File is not null) is not { Status.File: { } file } row)
            return;
        try
        {
            if (await App.Client.GetItemsForFileAsync(file.Id) is not [var summary, ..])
            {
                StatusLine.Text = $"{file.Name} isn't linked to a catalogue item yet. Right-click it and choose Add to Catalogue.";
                return;
            }
            await FillFromCatalogueAsync(row, file, await App.Client.GetItemAsync(summary.Id));
        }
        catch (OpenholdApiException ex)
        {
            StatusLine.Text = ex.Message;
        }
    }

    /// <summary>
    /// Shows what the catalogue item would change in the file (Part Number, Description, Vendor, Stock Number) and, if
    /// the person agrees, checks the file out, writes the values into it and checks it in.
    /// </summary>
    private async Task FillFromCatalogueAsync(FileRow row, VaultFileInfo file, ItemInfo item)
    {
        var changes = CatalogueProperties.Changes(item, file.Properties);
        if (changes.Count == 0)
        {
            StatusLine.Text = $"{file.Name} already has {item.Number}'s details.";
            return;
        }
        var list = string.Join("\n", changes.Select(c => $"   {c.Key}: {(file.Properties.GetValueOrDefault(c.Key) is { Length: > 0 } old ? old + "  →  " : "")}{c.Value}"));
        if (MessageBox.Show(this, $"Fill in {file.Name} from catalogue item {item.Number}?\n\n{list}\n\nOpenhold checks the file out, writes the values into it, and checks it in as a new version.",
                "Openhold: Catalogue", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes)
            return;
        var writers = PropertyWriters;
        await RunAsync($"Filling in {file.Name} from {item.Number}",
            () => Task.Run(() => App.Workspace.EditPropertiesAsync(
                new Dictionary<string, IReadOnlyDictionary<string, string>> { [row.LocalPath] = changes }, $"Details of {item.Number} from the catalogue", writers)));
    }

    private async void Revise_Click(object sender, RoutedEventArgs e)
    {
        var rows = Selected.Where(r => r.Status.File is not null).ToList();
        if (rows.Count == 0)
            return;
        var dialog = new ReviseWindow(rows, App.Configuration) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        var what = rows.Count == 1 ? rows[0].Name : $"{rows.Count} files";

        // Released files go back to an editable state first; that's part of revising them, and recorded as such.
        foreach (var group in rows.Where(r => r.Status.File!.StateLocked).GroupBy(r => ReopenState(r.Status.File!)))
        {
            if (group.Key is null)
            {
                MessageBox.Show(this, $"{group.First().Name} is {group.First().State} and its lifecycle has no way back to an editable state, so it can't be revised.",
                    "Openhold", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var reopened = await RunAsync($"Reopening {what}", () => App.Workspace.ChangeStateAsync(group.Select(r => r.LocalPath), group.Key, $"Reopened to revise: {dialog.Reason}"));
            if (!reopened.Succeeded)
                return;
            if (reopened.AwaitingApproval)
            {
                MessageBox.Show(this, "Reopening these files needs approval first. Revise them once it's approved.", "Openhold", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        var changes = dialog.NewRevisions.ToDictionary(
            r => r.Key, r => (IReadOnlyDictionary<string, string>)new Dictionary<string, string> { ["Revision"] = r.Value });
        var writers = PropertyWriters;
        await RunAsync($"Revising {what}", () => Task.Run(() => App.Workspace.EditPropertiesAsync(changes, dialog.Reason, writers)));
    }

    /// <summary>The editable state a locked file goes back to for a new revision, preferring the lifecycle's first state.</summary>
    private static string? ReopenState(Openhold.Core.Contracts.VaultFileInfo file)
    {
        var lifecycle = App.Configuration.LifecycleFor(file.Category);
        var ways = lifecycle?.TransitionsFrom(file.State ?? "")
            .Where(t => lifecycle.State(t.To) is { Locked: false } && t.Allows(App.Configuration, App.Client.Username))
            .ToList() ?? [];
        return (ways.FirstOrDefault(t => string.Equals(t.To, lifecycle!.Initial.Name, StringComparison.OrdinalIgnoreCase)) ?? ways.FirstOrDefault())?.To;
    }

    private async void ChangeState_Click(object sender, RoutedEventArgs e)
    {
        var rows = Selected.Where(r => r.Status.File is not null).ToList();
        if (rows.Count == 0)
            return;
        var dialog = new ChangeStateWindow(rows.Select(r => r.Status).ToList(), App.Configuration) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        var what = dialog.Paths.Count == 1 ? Path.GetFileName(dialog.Paths[0]) : $"{dialog.Paths.Count} files";
        await RunAsync($"Changing {what} to {dialog.TargetState}", () => App.Workspace.ChangeStateAsync(dialog.Paths, dialog.TargetState, dialog.CommentText));
    }

    private async void ChangeCategory_Click(object sender, RoutedEventArgs e)
    {
        var rows = Selected.Where(r => r.Status.File is not null).ToList();
        if (rows.Count == 0)
            return;
        var dialog = new ChangeCategoryWindow(rows.Count, rows.Select(r => r.Status.File!.Category).Distinct().ToList(), App.Configuration) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Chosen is not { } category)
            return;
        await RunAsync($"Changing the category to {category.Name}", () => App.Workspace.ChangeCategoryAsync(rows.Select(r => r.LocalPath), category.Id, category.Name));
    }

    private async void Approvals_Click(object sender, RoutedEventArgs e)
    {
        var window = new ApprovalsWindow { Owner = this };
        window.ShowDialog();
        if (window.ChangedAny)
            await RefreshAsync();
        else
            await LoadConfigurationAsync();
    }

    private async void CopyDesign_Click(object sender, RoutedEventArgs e)
    {
        var rows = Selected.Where(r => r.Status.File is not null).ToList();
        if (rows.Count == 0)
            return;
        IReadOnlyList<FileStatus> files;
        IReadOnlyList<Openhold.Core.Contracts.NumberingScheme> schemes;
        using (Busy())
        {
            files = await App.Workspace.GetStatusAsync(await App.Workspace.WithDependenciesAsync(rows.Select(r => r.LocalPath)));
            schemes = await App.Client.ListNumberingSchemesAsync();
        }
        var dialog = new CopyDesignWindow(files, App.Configuration, schemes) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        var source = rows.Count == 1 ? rows[0].Name : $"{rows.Count} files";
        await RunAsync($"Copying {source}", () => Task.Run(() => App.Workspace.CopyDesignAsync(dialog.Copies, $"Copied from {source}", Repairers)));
    }

    private async void OpenPdf_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not [{ Status.File: { } file } row])
            return;
        (string Path, int Version)? pdf;
        using (Busy())
            pdf = await App.Workspace.GetPublishedPdfAsync(file.Id, row.Name);
        if (pdf is not { } published)
        {
            MessageBox.Show(this,
                $"No PDF has been published for {row.Name} yet.\n\nA PDF is made when a drawing is released, by the Openhold job worker. " +
                "If it was released a while ago, ask your Openhold administrator to check the job worker is running.",
                "Openhold", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Process.Start(new ProcessStartInfo(published.Path) { UseShellExecute = true });
        StatusLine.Text = published.Version == file.LatestVersion
            ? $"Opened the published PDF of {row.Name}."
            : $"Opened the published PDF of {row.Name}, made from version {published.Version} (the latest release with a PDF).";
    }

    private async void PackAndGo_Click(object sender, RoutedEventArgs e)
    {
        var rows = Selected.Where(r => r.Status.File is not null).ToList();
        if (rows.Count == 0)
            return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Pack and Go: save the files as a zip",
            FileName = $"{Path.GetFileNameWithoutExtension(rows[0].Name)} Pack and Go.zip",
            Filter = "Zip files (*.zip)|*.zip",
        };
        if (dialog.ShowDialog(this) != true)
            return;

        StatusLine.Text = "Packing the latest vault versions and everything they use…";
        int count;
        using (Busy())
            count = await App.Workspace.PackAndGoAsync(rows.Select(r => r.LocalPath), dialog.FileName);
        StatusLine.Text = $"Packed {count} file(s) into {dialog.FileName}. These are the latest vault versions; uncommitted local changes aren't included.";
        Process.Start("explorer.exe", $"/select,\"{dialog.FileName}\"");
    }

    private async void Trash_Click(object sender, RoutedEventArgs e)
    {
        var trash = new TrashWindow { Owner = this };
        trash.ShowDialog();
        if (trash.RestoredAny)
            await RefreshAsync();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (new SettingsWindow(App.Settings) { Owner = this }.ShowDialog() != true)
            return;
        var restart = MessageBox.Show(this,
            "Settings saved. They take effect when Openhold Explorer restarts.\n\nRestart it now? Restart AutoCAD and Inventor too if they're open.",
            "Openhold: Settings", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.Yes);
        if (restart != MessageBoxResult.Yes)
        {
            StatusLine.Text = "Settings saved. They take effect the next time Openhold Explorer starts.";
            return;
        }
        Process.Start(Environment.ProcessPath!);
        Application.Current.Shutdown();
    }

    // ---- Menu bar ----

    /// <summary>Each menu offers exactly what the toolbar and right-click menu allow for the current selection.</summary>
    private void MainMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        FilesMenu_Opened(sender, e);
        MainOpen.IsEnabled = MenuOpen.IsEnabled;
        MainOpenPdf.IsEnabled = MenuOpenPdf.IsEnabled;
        MainShowInFolder.IsEnabled = MenuShowInFolder.IsEnabled;
        MainPackAndGo.IsEnabled = MenuPackAndGo.IsEnabled;
        MainEditProperties.IsEnabled = MenuEditProperties.IsEnabled;
        MainRename.IsEnabled = MenuRename.IsEnabled;
        MainMove.IsEnabled = MenuMove.IsEnabled;
        MainDelete.IsEnabled = MenuDelete.IsEnabled;
        MainCopyPath.IsEnabled = MenuCopyPath.IsEnabled;
        MainHistory.IsEnabled = MenuHistory.IsEnabled;
        MainUses.IsEnabled = MenuUses.IsEnabled;
        MainWhereUsed.IsEnabled = MenuWhereUsed.IsEnabled;
        MainGetLatest.IsEnabled = MenuGetLatest.IsEnabled;
        MainGetLatestWithUses.IsEnabled = MenuGetLatestWithUses.IsEnabled;
        MainCheckOut.IsEnabled = MenuCheckOut.IsEnabled;
        MainCheckOutWithUses.IsEnabled = MenuCheckOutWithUses.IsEnabled;
        MainCheckIn.IsEnabled = MenuCheckIn.IsEnabled;
        MainUndo.IsEnabled = MenuUndo.IsEnabled;
        MainChangeState.IsEnabled = MenuChangeState.IsEnabled;
        MainRevise.IsEnabled = MenuRevise.IsEnabled;
        MainCopyDesign.IsEnabled = MenuCopyDesign.IsEnabled;
        MainChangeCategory.Visibility = MenuChangeCategory.Visibility;
        MainChangeCategory.IsEnabled = MenuChangeCategory.IsEnabled;
        MainSignOut.Visibility = App.Settings.UsesOpenholdAccount ? Visibility.Visible : Visibility.Collapsed;
        MainAdministration.Visibility = App.Configuration.HasPrivilege(App.Client.Username, Openhold.Core.Configuration.Privileges.ManageConfiguration)
            || App.Configuration.HasPrivilege(App.Client.Username, Openhold.Core.Configuration.Privileges.ManageUsers) ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = $"Add files to {_current.Name}", Multiselect = true };
        if (dialog.ShowDialog(this) == true)
            await AddFilesAsync(dialog.FileNames);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Account_Click(object sender, RoutedEventArgs e) => new AccountWindow { Owner = this }.ShowDialog();

    /// <summary>Ends this PC's Openhold-account session (for every Openhold program on it) and starts again at sign-in.</summary>
    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        if (Client.Sdk.SessionStore.Load(App.Settings.ServerUri) is { } session)
        {
            try
            {
                await App.Client.SignOutAsync(session.SessionToken);
            }
            catch (Exception ex) when (ex is Client.Sdk.OpenholdApiException or System.Net.Http.HttpRequestException or Client.Sdk.SignInRequiredException)
            {
                // Signed out on this PC regardless; the server's session expires on its own.
            }
            Client.Sdk.SessionStore.Delete(App.Settings.ServerUri);
        }
        Process.Start(Environment.ProcessPath!);
        Application.Current.Shutdown();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        Files.Focus();
        Files.SelectAll();
    }

    private void Find_Click(object sender, RoutedEventArgs e) => SearchBox.Focus();

    private void Columns_Click(object sender, RoutedEventArgs e) => ShowColumnChooser();

    private async void Administration_Click(object sender, RoutedEventArgs e)
    {
        var window = new AdministrationWindow { Owner = this };
        window.ShowDialog();
        if (window.Saved)
            await RefreshAsync();
    }

    private void Shortcuts_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "Enter\tOpen the selected file\n" +
        "Ctrl+L\tGet Latest\n" +
        "Ctrl+E\tCheck Out\n" +
        "Ctrl+I\tCheck In\n" +
        "F2\tRename\n" +
        "Del\tDelete\n" +
        "Ctrl+A\tSelect all\n" +
        "Ctrl+Shift+C\tCopy path\n" +
        "Ctrl+Shift+A\tAdd files to the vault\n" +
        "Ctrl+Shift+N\tNew folder\n" +
        "Ctrl+F\tSearch\n" +
        "F5\tRefresh",
        "Openhold: Keyboard shortcuts", MessageBoxButton.OK, MessageBoxImage.Information);

    private void Legend_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "The bubble beside each file is your copy on this PC:\n\n" +
        "\u25CB  Hollow: not on this PC\n" +
        "\u25CF  Green: the latest version\n" +
        "\u25CF  Red: on this PC, but out of date (Get Latest)\n" +
        "\u25CF  Amber: changed on this PC without being checked out\n" +
        "\u25CC  Dashed: only on this PC, not in the vault yet\n\n" +
        "An X beside it means the file is checked out: blue to you, red to someone else.\n\n" +
        "The State column shows where the file is in its lifecycle, e.g. Work in Progress or Released.",
        "Openhold: What the status marks mean", MessageBoxButton.OK, MessageBoxImage.Information);

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var version = typeof(MainWindow).Assembly.GetName().Version;
        MessageBox.Show(this,
            $"Openhold Explorer {version?.ToString(3)}\n\n" +
            $"Server: {App.Settings.ServerUrl.TrimEnd('/')}\n" +
            $"Vault: {App.Settings.Vault ?? "the server's default vault"}\n" +
            $"Workspace: {App.Settings.WorkspaceRoot}\n" +
            $"Signed in as: {App.Client.Username}\n\n" +
            "Autodesk, AutoCAD, Inventor and Vault are trademarks of Autodesk, Inc. Openhold is not affiliated with Autodesk.",
            "About Openhold", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---- Property columns ----

    private readonly List<DataGridColumn> _propertyColumns = [];

    /// <summary>Shows the chosen property columns (Part Number, Description, …) straight after Version and State.</summary>
    private void ApplyPropertyColumns()
    {
        foreach (var column in _propertyColumns)
            Files.Columns.Remove(column);
        _propertyColumns.Clear();

        var position = Files.Columns.IndexOf(StateColumn) + 1;
        foreach (var name in App.Settings.PropertyColumns)
        {
            var column = new DataGridTextColumn
            {
                Header = name,
                Binding = new System.Windows.Data.Binding($"Prop[{name}]"),
                Width = DataGridLength.Auto,
                MinWidth = 110,
                SortMemberPath = $"Prop[{name}]",
            };
            Files.Columns.Insert(position++, column);
            _propertyColumns.Add(column);
        }
    }

    /// <summary>Right-clicking a column header offers every property the listed files have, as columns to show or hide.</summary>
    private void ShowColumnChooser()
    {
        var available = (Files.ItemsSource as IEnumerable<FileRow> ?? [])
            .SelectMany(r => r.Status.File?.Properties.Keys ?? Enumerable.Empty<string>())
            .Concat(App.Settings.PropertyColumns)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var menu = new ContextMenu { PlacementTarget = Files };
        menu.Items.Add(new MenuItem { Header = "Show property columns", IsEnabled = false, FontWeight = FontWeights.SemiBold });
        foreach (var name in available)
        {
            var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = App.Settings.PropertyColumns.Contains(name, StringComparer.OrdinalIgnoreCase), StaysOpenOnClick = true };
            item.Click += (_, _) =>
            {
                App.Settings.PropertyColumns.RemoveAll(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
                if (item.IsChecked)
                    App.Settings.PropertyColumns.Add(name);
                App.Settings.Save();
                ApplyPropertyColumns();
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    // ---- Right-click menus ----

    /// <summary>Right-clicking a file that isn't selected selects it first, as Windows Explorer does.</summary>
    private void Files_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Ancestor<System.Windows.Controls.Primitives.DataGridColumnHeader>(e.OriginalSource) is not null)
        {
            ShowColumnChooser();
            e.Handled = true;
            return;
        }
        if (Ancestor<DataGridRow>(e.OriginalSource) is { IsSelected: false, Item: var item })
        {
            Files.SelectedItems.Clear();
            Files.SelectedItems.Add(item);
        }
    }

    /// <summary>Right-clicking a folder selects it, so its menu acts on the folder that was clicked.</summary>
    private void Folders_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Ancestor<TreeViewItem>(e.OriginalSource) is { } item)
            item.IsSelected = true;
    }

    private static T? Ancestor<T>(object source) where T : DependencyObject
    {
        var node = source as DependencyObject;
        while (node is not null and not T)
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        return node as T;
    }

    private static readonly HashSet<string> InventorExtensions = new(StringComparer.OrdinalIgnoreCase) { ".ipt", ".iam", ".idw", ".ipn", ".ide" };

    /// <summary>"Edit iProperties…" for Inventor files, "Edit Title Block…" for drawings, otherwise "Edit Properties…".</summary>
    private static string PropertiesCommandName(IReadOnlyList<string> paths)
    {
        var extensions = paths.Select(System.IO.Path.GetExtension).ToList();
        if (extensions.Count > 0 && extensions.All(e => InventorExtensions.Contains(e!)))
            return "Edit i_Properties…";
        if (extensions.Count > 0 && extensions.All(e => string.Equals(e, ".dwg", StringComparison.OrdinalIgnoreCase)))
            return "Edit Title Block _Properties…";
        return "Edit _Properties…";
    }

    private void FilesMenu_Opened(object sender, RoutedEventArgs e)
    {
        // The menu offers exactly what the toolbar allows for the current selection.
        MenuOpen.IsEnabled = OpenButton.IsEnabled;
        MenuGetLatest.IsEnabled = GetLatestButton.IsEnabled;
        MenuCheckOut.IsEnabled = CheckOutButton.IsEnabled;
        MenuCheckIn.IsEnabled = CheckInButton.IsEnabled;
        MenuUndo.IsEnabled = UndoButton.IsEnabled;
        MenuShowInFolder.IsEnabled = ShowInFolderButton.IsEnabled;
        var single = Selected is [{ Status.File: not null }];
        MenuHistory.IsEnabled = MenuUses.IsEnabled = MenuWhereUsed.IsEnabled = single;
        MenuGetLatestWithUses.IsEnabled = MenuCheckOutWithUses.IsEnabled = MenuPackAndGo.IsEnabled = MenuCopyDesign.IsEnabled = MenuEditProperties.IsEnabled = MenuRevise.IsEnabled = Selected.Any(r => r.Status.File is not null);
        MenuChangeState.IsEnabled = Selected.Any(r => r.Status.File is not null);
        MenuAddToCatalogue.IsEnabled = MenuFillFromCatalogue.IsEnabled = Selected is [{ Status.File: not null }];
        MenuFillFromCatalogue.Header = Selected is [{ } one] && InventorExtensions.Contains(System.IO.Path.GetExtension(one.Name)) ? "Fill iProperties from Catalogue" : "Fill Properties from Catalogue";
        // Name the command after what it edits, so Inventor users find their iProperties straight away.
        MenuEditProperties.Header = MainEditProperties.Header = PropertiesCommandName(Selected.Where(r => r.Status.File is not null).Select(r => r.LocalPath).ToList());
        MenuEditProperties.ToolTip = MenuEditProperties.IsEnabled ? null : "Only files in the vault have editable properties. Check the file in first.";
        ToolTipService.SetShowOnDisabled(MenuEditProperties, true);
        // The category decides how a file is released, so only administrators change it.
        MenuChangeCategory.Visibility = App.Configuration.HasPrivilege(App.Client.Username, Openhold.Core.Configuration.Privileges.ChangeCategory) ? Visibility.Visible : Visibility.Collapsed;
        MenuChangeCategory.IsEnabled = MenuChangeState.IsEnabled;
        MenuOpenPdf.IsEnabled = single;
        MenuCopyPath.IsEnabled = MenuDelete.IsEnabled = MenuMove.IsEnabled = Selected.Count > 0;
        MenuRename.IsEnabled = Selected.Count == 1;
    }

    private void ShowHistory_Click(object sender, RoutedEventArgs e) => HistoryTab.IsSelected = true;

    // ---- Earlier versions ----

    private async void OpenVersion_Click(object sender, RoutedEventArgs e)
    {
        if (History.SelectedItem is not HistoryRow version || Selected is not [{ Status.File: { } file } row])
            return;
        StatusLine.Text = $"Getting version {version.Version} of {row.Name}…";
        string copy;
        using (Busy())
            copy = await App.Workspace.GetVersionCopyAsync(file.Id, row.Name, version.Version);
        Process.Start(new ProcessStartInfo(copy) { UseShellExecute = true });
        StatusLine.Text = $"Opened version {version.Version} of {row.Name} as a separate read-only copy. Your workspace file is unchanged.";
    }

    private async void RestoreVersion_Click(object sender, RoutedEventArgs e)
    {
        if (History.SelectedItem is not HistoryRow version || Selected is not [{ Status.File: not null } row])
            return;
        if (version.Version == row.Status.File.LatestVersion)
        {
            StatusLine.Text = $"Version {version.Version} is already the latest.";
            return;
        }
        var answer = MessageBox.Show(this,
            $"Make version {version.Version} of {row.Name} the newest version?\n\n" +
            "It's added as a new version, so nothing is lost and you can change your mind later. " +
            "Your local copy will be replaced; any changes you've made to it are kept in a backup.",
            "Openhold: Restore Earlier Version", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes)
            await RunAsync($"Restoring version {version.Version}", () => App.Workspace.RestoreVersionAsync(row.LocalPath, version.Version));
    }

    private void ShowUses_Click(object sender, RoutedEventArgs e) => UsesTab.IsSelected = true;

    private void ShowWhereUsed_Click(object sender, RoutedEventArgs e) => WhereUsedTab.IsSelected = true;

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(string.Join(Environment.NewLine, Selected.Select(r => r.LocalPath)));
        StatusLine.Text = Selected.Count == 1 ? "Copied the file's path." : $"Copied {Selected.Count} paths.";
    }

    private async void FolderGetLatest_Click(object sender, RoutedEventArgs e)
    {
        var folder = await App.Workspace.GetFolderAsync(_current.VaultPath);
        var stale = folder.Files
            .Where(f => f.State is FileState.Outdated or FileState.NotDownloaded or FileState.ModifiedLocally)
            .Select(f => f.LocalPath)
            .ToList();
        if (stale.Count == 0)
        {
            StatusLine.Text = $"Everything in {_current.Name} is up to date.";
            return;
        }
        await RunAsync($"Getting the latest versions in {_current.Name}", () => App.Workspace.GetLatestAsync(stale));
    }

    private void FolderShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        var path = App.Workspace.ToLocalPath(_current.VaultPath);
        Directory.CreateDirectory(path);
        Process.Start("explorer.exe", $"\"{path}\"");
    }

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new PromptWindow("New folder", "Folder name:") { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Value))
            return;
        var name = dialog.Value.Trim();
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(this, $"\"{name}\" can't be used as a folder name.", "Openhold", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var vaultPath = $"{_current.VaultPath}/{name}";
        await App.Client.EnsureFolderAsync(vaultPath);
        Directory.CreateDirectory(App.Workspace.ToLocalPath(vaultPath));
        _current.IsExpanded = true;
        await _current.LoadChildrenAsync();
        StatusLine.Text = $"Created folder {name}.";
    }

    /// <summary>Removes the selected folder if it's empty; the server refuses (and says why) if anything is in it.</summary>
    private async void DeleteFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { Parent: { } parent } folder)
        {
            StatusLine.Text = "The vault's top folder can't be removed.";
            return;
        }
        if (MessageBox.Show(this, $"Remove the folder {folder.Name}? Only an empty folder can be removed.", "Openhold",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        try
        {
            await App.Client.DeleteFolderAsync(folder.VaultPath);
            var local = App.Workspace.ToLocalPath(folder.VaultPath);
            if (Directory.Exists(local) && !Directory.EnumerateFileSystemEntries(local).Any())
                Directory.Delete(local);
            await parent.LoadChildrenAsync();
            StatusLine.Text = $"Removed the folder {folder.Name}.";
        }
        catch (OpenholdApiException ex)
        {
            StatusLine.Text = ex.Message;
        }
    }

    // ---- Drag and drop: add files to the vault ----

    private void Files_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Files_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped)
            await AddFilesAsync(dropped);
    }

    /// <summary>Copies files into the folder being shown and checks them in, as dropping them on the list does.</summary>
    private async Task AddFilesAsync(IReadOnlyList<string> dropped)
    {
        var target = App.Workspace.ToLocalPath(_current.VaultPath);
        Directory.CreateDirectory(target);
        var added = new List<string>();
        var skipped = new List<string>();
        foreach (var source in dropped)
        {
            var destination = Path.Combine(target, Path.GetFileName(source));
            if (Directory.Exists(source) || File.Exists(destination))
            {
                skipped.Add(Path.GetFileName(source));
                continue;
            }
            if (!string.Equals(Path.GetFullPath(source), destination, StringComparison.OrdinalIgnoreCase))
                File.Copy(source, destination);
            added.Add(destination);
        }

        if (skipped.Count > 0)
        {
            MessageBox.Show(this,
                $"These weren't added because a file with the same name is already in this folder, or they are folders:\n\n{string.Join("\n", skipped)}",
                "Openhold", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        if (added.Count > 0)
            await CheckInAsync(added);
        await RefreshAsync();
    }

    // ---- Previews ----

    /// <summary>Previews already loaded, by file and version, so moving around the list doesn't fetch them again.</summary>
    private readonly Dictionary<(Guid, int), System.Windows.Media.ImageSource?> _previews = [];

    /// <summary>
    /// A file's preview: the one stored in the vault, or, when the vault has none (e.g. files checked in before
    /// previews were captured) and this PC has the latest version, one made from Windows' own thumbnail. That one is
    /// saved to the vault too, so everyone sees it from then on.
    /// </summary>
    private async Task<System.Windows.Media.ImageSource?> PreviewAsync(FileStatus status)
    {
        if (status.File is not { } file)
            return File.Exists(status.LocalPath) ? ToImage(Openhold.Client.Ui.ShellThumbnails.TryCreatePng(status.LocalPath)) : null;
        var key = (file.Id, file.LatestVersion);
        if (_previews.TryGetValue(key, out var cached))
            return cached;

        byte[]? png = null;
        try
        {
            png = await App.Client.GetThumbnailAsync(file.Id);
        }
        catch (Exception ex) when (ex is Client.Sdk.OpenholdApiException or System.Net.Http.HttpRequestException)
        {
            return null; // no preview is better than an error while browsing
        }
        if (png is null && status.State == FileState.UpToDate)
        {
            // Made here on the UI thread: some Windows thumbnail handlers need it.
            png = Openhold.Client.Ui.ShellThumbnails.TryCreatePng(status.LocalPath);
            if (png is not null)
                _ = FillInPreviewAsync(file.Id, file.LatestVersion, png);
        }
        return _previews[key] = ToImage(png);
    }

    private static async Task FillInPreviewAsync(Guid fileId, int version, byte[] png)
    {
        try
        {
            await App.Client.AddMissingThumbnailAsync(fileId, version, png);
        }
        catch (Exception ex) when (ex is Client.Sdk.OpenholdApiException or System.Net.Http.HttpRequestException)
        {
            // It's only filling a gap; the next person to look at the file tries again.
        }
    }

    private static System.Windows.Media.ImageSource? ToImage(byte[]? png)
    {
        if (png is null)
            return null;
        var image = new System.Windows.Media.Imaging.BitmapImage();
        image.BeginInit();
        image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static string NoPreviewReason(FileStatus status) => status.State switch
    {
        FileState.NotDownloaded => "No preview yet. Get the file and Openhold will make one from it.",
        FileState.Outdated => "No preview yet. Get the latest version and Openhold will make one from it.",
        _ => "No preview for this file. Windows has no thumbnail for this kind of file on this PC.",
    };

    private void ShowPreview(System.Windows.Media.ImageSource? image, string note)
    {
        DetailThumbnail.Source = image;
        PreviewPlaceholder.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
        PreviewNote.Text = note;
    }

    /// <summary>Hovering over a file's name shows its preview, so you can see what you're about to click.</summary>
    private async void Name_ToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FileRow row } element)
            return;
        var image = new Image { MaxWidth = 240, MaxHeight = 200, Stretch = System.Windows.Media.Stretch.Uniform, Margin = new Thickness(0, 0, 0, 6) };
        var text = new TextBlock { Text = row.State.Length > 0 ? $"{row.Name}  ·  {row.State}" : row.Name };
        element.ToolTip = new StackPanel { Children = { image, text } };
        image.Source = await PreviewAsync(row.Status);
        image.Visibility = image.Source is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- Details pane ----

    private async Task ShowDetailsAsync()
    {
        History.ItemsSource = null;
        Uses.ItemsSource = null;
        WhereUsed.ItemsSource = null;
        DetailProperties.ItemsSource = null;
        ShowPreview(null, Selected.Count > 1 ? $"{Selected.Count} files selected" : "Select a file to see its preview");

        if (Selected is not [var row])
        {
            DetailName.Text = Selected.Count > 1 ? $"{Selected.Count} files selected" : "Select a file";
            DetailStatus.Text = "";
            return;
        }

        DetailName.Text = row.Name;
        DetailStatus.Text = row.Status.Description;
        var preview = await PreviewAsync(row.Status);
        if (!ReferenceEquals(Selected.FirstOrDefault(), row))
            return;
        ShowPreview(preview, NoPreviewReason(row.Status));
        if (row.Status.File is not { } file)
            return;

        // Load everything first, then check once that the selection hasn't moved on, so the panes never
        // show a mix of the old and new file.
        var historyTask = App.Client.GetHistoryAsync(file.Id);
        var usesTask = App.Client.GetUsesAsync(file.Id);
        var whereUsedTask = App.Client.GetWhereUsedAsync(file.Id);
        await Task.WhenAll(historyTask, usesTask, whereUsedTask);
        if (!ReferenceEquals(Selected.FirstOrDefault(), row))
            return;

        var history = historyTask.Result;
        History.ItemsSource = history.Select(v => new HistoryRow(
            v.Version, v.CreatedAt.ToLocalTime().ToString("d MMM yyyy h:mm tt", CultureInfo.CurrentCulture), v.Username, v.Comment, v.State ?? "")).ToList();

        var latest = history.FirstOrDefault();
        var properties = new List<KeyValuePair<string, string>>
        {
            new("Version", $"v{file.LatestVersion}"),
            new("Location", row.LocalPath),
        };
        if (file.State is not null)
            properties.Add(new("State", file.State));
        if (file.CategoryName is not null)
            properties.Add(new("Category", file.CategoryName));
        if (latest is not null)
        {
            properties.Add(new("Last checked in", $"{latest.CreatedAt.ToLocalTime():d MMM yyyy h:mm tt} by {latest.Username} ({latest.OriginSite})"));
            properties.AddRange(latest.Properties.OrderBy(p => p.Key));
        }
        DetailProperties.ItemsSource = properties;

        Uses.ItemsSource = usesTask.Result.Select(r => $"{r.Name}   (v{r.Version})").ToList();
        WhereUsed.ItemsSource = whereUsedTask.Result.Select(r => $"{r.Name}   ({r.Path})").ToList();
    }

    // ---- Helpers ----

    /// <summary>Runs an action, shows its plain-language result, and refreshes. Problems are shown, never thrown.</summary>
    private async Task<WorkspaceResult> RunAsync(string label, Func<Task<WorkspaceResult>> action)
    {
        StatusLine.Text = $"{label}…";
        WorkspaceResult result;
        using (Busy())
            result = await action();

        var details = string.Join("\n", result.Files.Select(f => "•  " + f.Message));
        SetStatus(result.Files.Count == 0 ? result.Summary : $"{result.Summary}  (hover for details)", details.Length > 0 ? details : null);
        if (!result.Succeeded)
            MessageBox.Show(this, details.Length > 0 ? $"{result.Summary}\n\n{details}" : result.Summary, "Openhold", MessageBoxButton.OK, MessageBoxImage.Warning);

        await RefreshAsync();
        return result;
    }

    /// <summary>Shows a result on the status line and tells screen readers about it.</summary>
    private void SetStatus(string text, string? details = null)
    {
        StatusLine.Text = text;
        StatusLine.ToolTip = details;
        System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(StatusLine)?
            .RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

    /// <summary>Disables the window while something runs, and puts keyboard focus back where it was afterwards.</summary>
    private IDisposable Busy()
    {
        var focused = Keyboard.FocusedElement as IInputElement;
        Mouse.OverrideCursor = Cursors.Wait;
        IsEnabled = false;
        return new Restore(() =>
        {
            IsEnabled = true;
            Mouse.OverrideCursor = null;
            if (focused is UIElement { IsVisible: true } element)
                element.Focus();
            else
                Files.Focus();
        });
    }

    private void UpdateButtons()
    {
        var rows = Selected;
        GetLatestButton.IsEnabled = rows.Any(r => r.Status.State is FileState.Outdated or FileState.NotDownloaded or FileState.ModifiedLocally);
        CheckOutButton.IsEnabled = rows.Any(r => r.Status.State is FileState.UpToDate or FileState.Outdated or FileState.NotDownloaded or FileState.ModifiedLocally);
        CheckInButton.IsEnabled = rows.Any(r => r.Status.State is FileState.CheckedOutToMe or FileState.NotInVault);
        UndoButton.IsEnabled = rows.Any(r => r.Status.State == FileState.CheckedOutToMe);
        OpenButton.IsEnabled = rows is [var only] && (only.Status.State != FileState.NotInVault || File.Exists(only.LocalPath));
        ShowInFolderButton.IsEnabled = rows.Count == 1 && File.Exists(rows[0].LocalPath);
    }

    private sealed class Restore(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
