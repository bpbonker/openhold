using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Openhold.Client.Sdk;
using Openhold.Core.Configuration;
using Openhold.Core.Contracts;
using Openhold.Desktop.Administration;

namespace Openhold.Desktop;

/// <summary>
/// The catalogue (item master): find parts, add new ones (they get their universal part number at once), edit them,
/// and, for part librarians, release or obsolete them. Can open on a new item prefilled from a file ("Add to Catalogue").
/// </summary>
public partial class CatalogueWindow : Window
{
    private ItemInfo? _item;              // the item being edited; null for a new one
    private bool _loading;                // filling the editor: changes aren't the person's
    private bool _dirty;
    private readonly List<ItemFileLink> _files = [];

    /// <summary>The item last saved here, so Explorer can offer to write its number into a file.</summary>
    public ItemInfo? SavedItem { get; private set; }

    /// <param name="newItem">Open on this unsaved new item (e.g. prefilled from a file).</param>
    /// <param name="search">Start with this search, e.g. a file's existing item.</param>
    public CatalogueWindow(SaveItemRequest? newItem = null, string? search = null)
    {
        InitializeComponent();
        SearchText.Text = search ?? "";
        FillClassLists();
        ManageClassesButton.Visibility = App.Configuration.HasPrivilege(App.Client.Username, Privileges.ManageConfiguration) ? Visibility.Visible : Visibility.Collapsed;
        Loaded += async (_, _) =>
        {
            if (newItem is not null)
                StartNew(newItem);
            await SearchAsync();
            if (search is not null && Results.Items.Count > 0)
                Results.SelectedIndex = 0;
            SearchText.Focus();
        };
        Closing += (_, e) =>
        {
            if (!App.Unattended && _dirty && !Ask("Close without saving the item?"))
                e.Cancel = true;
        };
    }

    /// <summary>Opens Administration on Item Classes; afterwards the class lists show any changes.</summary>
    private async void ManageClasses_Click(object sender, RoutedEventArgs e)
    {
        var admin = new AdministrationWindow(itemClasses: true) { Owner = this };
        admin.ShowDialog();
        if (!admin.Saved)
            return;
        App.Configuration = (await App.Client.GetConfigurationAsync()).Configuration;
        FillClassLists();
        if (_item is { } item)
            Show(item);
    }

    private void FillClassLists()
    {
        var classes = (App.Configuration.ItemClasses ?? [])
            .Select(c => new Choice(c.Id, App.Configuration.ItemClassPath(c.Id) ?? c.Name))
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        ItemClass.ItemsSource = new[] { new Choice("", "(no class yet)") }.Concat(classes).ToList();
        var filter = ClassFilter.SelectedValue as string;
        ClassFilter.ItemsSource = new[] { new Choice("", "All classes") }.Concat(classes).ToList();
        ClassFilter.SelectedValue = filter ?? "";
    }

    private bool IsLibrarian => App.Configuration.HasPrivilege(App.Client.Username, Privileges.PartLibrarian);

    // ---- Searching ----

    private async Task SearchAsync()
    {
        try
        {
            var request = new ItemSearchRequest(SearchText.Text.Trim() is { Length: > 0 } text ? text : null,
                ClassFilter.SelectedValue as string is { Length: > 0 } classId ? classId : null,
                Chosen(StateFilter), Chosen(KindFilter), 500);
            var results = await App.Client.SearchItemsAsync(request);
            Results.ItemsSource = results;
            ShowMessage(results.Count == 0 && request.Text is not null ? $"Nothing in the catalogue matches \"{request.Text}\". If it's a new part, click New Item." : null);
        }
        catch (Exception ex) when (ex is OpenholdApiException or System.Net.Http.HttpRequestException)
        {
            ShowMessage($"Couldn't search the catalogue: {ex.Message}", problem: true);
        }
    }

    private static string? Chosen(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string is { Length: > 0 } tag ? tag : null;

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync();

    private async void SearchText_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            await SearchAsync();
    }

    private async void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
            await SearchAsync();
    }

    private async void Results_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Results.SelectedItem is not ItemSummary summary || summary.Id == _item?.Id)
            return;
        if (_dirty && !Ask("Discard your changes to the item you were editing?"))
            return;
        try
        {
            Show(await App.Client.GetItemAsync(summary.Id));
        }
        catch (OpenholdApiException ex)
        {
            ShowMessage(ex.Message, problem: true);
        }
    }

    // ---- Editing ----

    private void NewItem_Click(object sender, RoutedEventArgs e)
    {
        if (_dirty && !Ask("Discard your changes to the item you were editing?"))
            return;
        StartNew(new SaveItemRequest(SearchText.Text.Trim(), ItemKinds.Bought));
    }

    /// <summary>Opens an unsaved new item, e.g. prefilled from a file.</summary>
    private void StartNew(SaveItemRequest draft)
    {
        _item = null;
        Results.SelectedItem = null;
        Fill(draft.Description, draft.Kind, draft.Unit, draft.ClassId, draft.Attributes, draft.Notes, draft.ManufacturerParts, draft.CrossReferences, draft.Files);
        ItemNumber.Text = "New item";
        ItemStateChip.Visibility = Visibility.Collapsed;
        ItemHistory.Text = "It gets its part number when you save it.";
        ErpStatus.Text = "";
        _dirty = draft.Description.Length > 0;
        UpdateButtons();
        Description.Focus();
    }

    private async void Show(ItemInfo item)
    {
        _item = item;
        Fill(item.Description, item.Kind, item.Unit, item.ClassId, item.Attributes, item.Notes, item.ManufacturerParts, item.CrossReferences, item.Files);
        ItemNumber.Text = item.Number;
        ItemState.Text = item.State;
        ItemStateChip.Visibility = Visibility.Visible;
        ItemHistory.Text = $"Added by {item.CreatedBy} on {item.CreatedAt.ToLocalTime():d MMM yyyy}" +
            (item.ReleasedBy is { } by ? $"; released by {by} on {item.ReleasedAt!.Value.ToLocalTime():d MMM yyyy}" : "");
        _dirty = false;
        UpdateButtons();
        try
        {
            var erp = await App.Client.GetItemErpStatusAsync(item.Id);
            ErpStatus.Text = erp.Count == 0 ? (item.State == ItemStates.Draft ? "Goes to the ERPs once it's released." : "")
                : "In the ERPs: " + string.Join("; ", erp.Select(s => s.Status switch
                {
                    "done" => $"{s.ConnectionName} up to date" + (s.ExternalId is { } id ? $" (id {id})" : ""),
                    "pending" => $"{s.ConnectionName} waiting to be sent",
                    _ => $"{s.ConnectionName} failed ({s.Attempts} tries): {s.LastError}",
                }));
        }
        catch (OpenholdApiException)
        {
            ErpStatus.Text = "";
        }
    }

    private void Fill(string description, string kind, string unit, string? classId, IReadOnlyDictionary<string, string>? attributes, string? notes,
        IReadOnlyList<ManufacturerPart>? parts, IReadOnlyList<CrossReference>? references, IReadOnlyList<ItemFileLink>? files)
    {
        _loading = true;
        Editor.IsEnabled = true;
        Description.Text = description;
        Kind.SelectedItem = Kind.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == kind);
        Unit.Text = unit;
        ItemClass.SelectedValue = classId ?? "";
        ShowAttributes(classId, attributes ?? new Dictionary<string, string>());
        Notes.Text = notes ?? "";
        ManufacturerParts.ItemsSource = new ObservableCollection<PartRow>((parts ?? []).Select(p => new PartRow { Manufacturer = p.Manufacturer, PartNumber = p.PartNumber, Preferred = p.Preferred }));
        CrossReferences.ItemsSource = new ObservableCollection<ReferenceRow>((references ?? []).Select(r => new ReferenceRow { System = r.System, Number = r.Number }));
        _files.Clear();
        _files.AddRange(files ?? []);
        RefreshFiles();
        _loading = false;
    }

    /// <summary>The class's attributes (its own and its parents'), keeping values already typed, plus any others the item has.</summary>
    private void ShowAttributes(string? classId, IReadOnlyDictionary<string, string> values)
    {
        var defined = App.Configuration.ItemAttributesFor(classId);
        var rows = defined.Select(a => new AttributeRow(a.Name, a.Unit, a.Required, a.Choices) { Value = values.GetValueOrDefault(a.Name) ?? "" }).ToList();
        rows.AddRange(values.Where(v => !defined.Any(a => string.Equals(a.Name, v.Key, StringComparison.OrdinalIgnoreCase)))
            .Select(v => new AttributeRow(v.Key, null, false, null) { Value = v.Value }));
        Attributes.ItemsSource = rows;
        NoAttributes.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private IReadOnlyDictionary<string, string> CurrentAttributes() =>
        (Attributes.ItemsSource as IEnumerable<AttributeRow> ?? []).Where(a => a.Value.Trim().Length > 0).ToDictionary(a => a.Name, a => a.Value.Trim());

    private void ItemClass_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;
        ShowAttributes(ItemClass.SelectedValue as string is { Length: > 0 } id ? id : null, CurrentAttributes());
        Changed();
    }

    private void Field_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading)
            Changed();
    }

    private void Grid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e) => Changed();

    private void Changed()
    {
        _dirty = true;
        UpdateButtons();
    }

    private void UnlinkFile_Click(object sender, RoutedEventArgs e)
    {
        if (Files.SelectedItem is not ItemFileLink link)
            return;
        _files.Remove(link);
        RefreshFiles();
        Changed();
    }

    private void RefreshFiles()
    {
        Files.ItemsSource = null;
        Files.ItemsSource = _files;
        NoFiles.Visibility = _files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Files.Visibility = _files.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Save while there's something to save; Release and Make Obsolete only for part librarians, and only when saved.</summary>
    private void UpdateButtons()
    {
        SaveButton.IsEnabled = Editor.IsEnabled && (_dirty || _item is null);
        var state = _item?.State;
        ReleaseButton.Visibility = IsLibrarian && state is ItemStates.Draft or ItemStates.Obsolete ? Visibility.Visible : Visibility.Collapsed;
        ReleaseButton.Content = state == ItemStates.Obsolete ? "_Reinstate" : "_Release";
        ObsoleteButton.Visibility = IsLibrarian && state is ItemStates.Draft or ItemStates.Released ? Visibility.Visible : Visibility.Collapsed;
        ReleaseButton.IsEnabled = ObsoleteButton.IsEnabled = !_dirty;
    }

    private SaveItemRequest Request()
    {
        ManufacturerParts.CommitEdit(DataGridEditingUnit.Row, true);
        CrossReferences.CommitEdit(DataGridEditingUnit.Row, true);
        var parts = (ManufacturerParts.ItemsSource as IEnumerable<PartRow> ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p.Manufacturer) || !string.IsNullOrWhiteSpace(p.PartNumber))
            .Select(p => new ManufacturerPart(p.Manufacturer ?? "", p.PartNumber ?? "", p.Preferred)).ToList();
        var references = (CrossReferences.ItemsSource as IEnumerable<ReferenceRow> ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r.System) || !string.IsNullOrWhiteSpace(r.Number))
            .Select(r => new CrossReference(r.System ?? "", r.Number ?? "")).ToList();
        return new SaveItemRequest(Description.Text.Trim(), (string)((ComboBoxItem)Kind.SelectedItem).Tag, Unit.Text.Trim(),
            ItemClass.SelectedValue as string is { Length: > 0 } id ? id : null, CurrentAttributes(), Notes.Text,
            parts, references, _files.Select(f => new ItemFileLink(f.FileId, f.Role)).ToList(), _item?.RowVersion);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var request = Request();
        SaveButton.IsEnabled = false;
        try
        {
            // Before a new number is issued, check the part isn't already here under another manufacturer's name.
            if (_item is null && request.ManufacturerParts is { Count: > 0 } parts
                && await App.Client.FindDuplicateItemsAsync(new DuplicateCheckRequest(parts)) is { Count: > 0 } duplicates
                && !Ask("This may already be in the catalogue:\n\n" + string.Join("\n", duplicates.Select(d => $"{d.Item.Number}  {d.Item.Description}  ({d.Reason})")) +
                        "\n\nAdd it as a new item anyway?"))
            {
                Results.ItemsSource = duplicates.Select(d => d.Item).ToList();
                return;
            }
            var saved = _item is null ? await App.Client.CreateItemAsync(request) : await App.Client.UpdateItemAsync(_item.Id, request);
            var isNew = _item is null;
            SavedItem = saved;
            Show(saved);
            ShowMessage(isNew ? $"Added as {saved.Number}." : $"Saved {saved.Number}.");
            await SearchAsync();
            Results.SelectedItem = (Results.ItemsSource as IEnumerable<ItemSummary>)?.FirstOrDefault(i => i.Id == saved.Id);
        }
        catch (OpenholdApiException ex)
        {
            ShowMessage(ex.Message, problem: true);
        }
        finally
        {
            UpdateButtons();
        }
    }

    private async void Release_Click(object sender, RoutedEventArgs e) => await ChangeStateAsync(ItemStates.Released, null);

    private async void Obsolete_Click(object sender, RoutedEventArgs e)
    {
        var prompt = new PromptWindow("Make Obsolete", $"Why is {_item?.Number} obsolete? For example, what replaces it. Everyone sees this, and the ERPs are told.") { Owner = this };
        if (prompt.ShowDialog() == true && prompt.Value.Trim() is { Length: > 0 } reason)
            await ChangeStateAsync(ItemStates.Obsolete, reason);
    }

    private async Task ChangeStateAsync(string state, string? comment)
    {
        if (_item is not { } item)
            return;
        try
        {
            var changed = await App.Client.ChangeItemStateAsync(item.Id, new ItemStateRequest(state, item.RowVersion, comment));
            Show(changed);
            ShowMessage($"{changed.Number} is {changed.State.ToLowerInvariant()}.");
            await SearchAsync();
        }
        catch (OpenholdApiException ex)
        {
            ShowMessage(ex.Message, problem: true);
        }
    }

    private bool Ask(string question) =>
        MessageBox.Show(this, question, "Openhold: Catalogue", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void ShowMessage(string? text, bool problem = false)
    {
        Message.Text = text ?? "";
        Message.Foreground = (System.Windows.Media.Brush)FindResource(problem ? "DangerText" : "Brand");
        Message.Visibility = Message.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>An attribute of the item's class: a text box, or a drop-down when the class lists the allowed values.</summary>
    private sealed class AttributeRow(string name, string? unit, bool required, IReadOnlyList<string>? choices) : INotifyPropertyChanged
    {
        public string Name { get; } = name;

        public string Label { get; } = name + (unit is { Length: > 0 } ? $" ({unit})" : "") + (required ? " *" : "");

        public IReadOnlyList<string>? Choices { get; } = choices is { Count: > 0 } ? ["", .. choices] : null;

        public Visibility TextVisibility => Choices is null ? Visibility.Visible : Visibility.Collapsed;

        public Visibility ChoiceVisibility => Choices is null ? Visibility.Collapsed : Visibility.Visible;

        public string Value
        {
            get;
            set
            {
                field = value ?? "";
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            }
        } = "";

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>Rows the grids can add (they need a parameterless constructor and settable properties).</summary>
    public sealed class PartRow
    {
        public string? Manufacturer { get; set; }
        public string? PartNumber { get; set; }
        public bool Preferred { get; set; }
    }

    public sealed class ReferenceRow
    {
        public string? System { get; set; }
        public string? Number { get; set; }
    }
}
