using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Openhold.Client.Sdk.Workspace;
using Openhold.Core.Contracts;
using Openhold.Core.Properties;

namespace Openhold.Desktop;

/// <summary>
/// Edits properties of one file or many at once. Each property shows its value, or "(differs between files)", and
/// where it's saved: in the file (iProperty, title block, through the file type's property writer) or in the vault
/// only. Only the properties the user changes are written, so every other value stays as each file has it.
/// </summary>
public partial class EditPropertiesWindow : Window
{
    // Worked out from the file itself (Inventor's Physical tab, file subtype; a drawing's sheet count); not typed in.
    private static readonly HashSet<string> NotEditable = new(
        InventorProperties.All.Where(p => !p.Editable).Select(p => p.Name).Append("Sheet Count"), StringComparer.OrdinalIgnoreCase);

    // Inventor's iProperties dialog, tab by tab (Summary, Project, Status), as the dialog names them.
    private static readonly IEnumerable<string> InventorFields = InventorProperties.All.Where(p => p.Editable).Select(p => p.Name);

    private readonly List<PropertyRow> _rows;
    private readonly IReadOnlyList<(string Path, IPropertyWriter? Writer)> _targets;

    /// <param name="pairs">Each file's local path and vault record.</param>
    /// <param name="writers">The property writers for the file types this PC can write (e.g. Inventor, AutoCAD).</param>
    public EditPropertiesWindow(IReadOnlyList<(string Path, VaultFileInfo File)> pairs, IReadOnlyList<IPropertyWriter> writers)
    {
        InitializeComponent();
        var files = pairs.Select(p => p.File).ToList();
        _targets = pairs.Select(p => (p.Path, writers.FirstOrDefault(w => w.CanWrite(p.Path)))).ToList();

        // Name the dialog after what's being edited: iProperties, a title block, or (mixed or other files) properties.
        var kinds = _targets.Select(t => t.Writer?.StoredAs).Distinct().ToList();
        Title = kinds is ["iProperty"] ? "Openhold: Edit iProperties" : kinds is ["Title block"] ? "Openhold: Edit Title Block" : "Openhold: Edit Properties";
        Heading.Text = Title["Openhold: ".Length..];
        Intro.Text = files.Count == 1
            ? $"{files[0].Name}: change any value and click Save. Openhold checks the file out, writes the values into it, and checks it in as a new version."
            : $"{files.Count} files: values that differ between them show as \"(differs between files)\" and are left as they are unless you type a new value. " +
              "Openhold checks the files out, writes the values into them, and checks them in together as new versions.";
        Intro.Text += " \"Saved in\" shows where each value goes: into the file itself, or kept in the vault only (for values a file has no place for).";
        Comment.Text = "Properties edited in Openhold";

        // Every field the file can hold is offered, filled in or not, so empty iProperties and title-block fields can be
        // filled in here: Inventor's standard iProperties in the order of Inventor's own dialog, a drawing's mapped
        // title-block fields, and the fields the files' categories ask for. Custom values the files have follow.
        var offered = new List<string>();
        if (kinds.Contains("iProperty"))
            offered.AddRange(InventorFields);
        if (kinds.Contains("Title block"))
            offered.AddRange(App.Settings.TitleBlock.Attributes.Values);
        offered.AddRange(files.SelectMany(f => App.Configuration.Category(f.Category)?.Properties ?? []));
        // Values recorded under Inventor's internal names (e.g. "Engr Approved By", by earlier versions) match their row.
        var recorded = files.Select(f => f.Properties.GroupBy(p => InventorProperties.Canonical(p.Key), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase)).ToList();
        var names = offered
            .Concat(App.Settings.PropertyColumns.Select(InventorProperties.Canonical))
            .Concat(recorded.SelectMany(r => r.Keys).Order(StringComparer.OrdinalIgnoreCase))
            .Where(n => !NotEditable.Contains(n))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        _rows = names.Select(name =>
        {
            var values = recorded.Select(r => r.GetValueOrDefault(name) ?? "").Distinct().ToList();
            return new PropertyRow(name, values.Count == 1 ? values[0] : null) { SavedIn = SavedIn(name) };
        }).ToList();
        Items.ItemsSource = _rows;
    }

    /// <summary>
    /// Where a property is saved for these files, e.g. "iProperty", "Title block", "Vault only", or a mix when the
    /// selection holds different kinds of file.
    /// </summary>
    private string SavedIn(string property)
    {
        var places = _targets
            .Select(t => t.Writer is { } writer && writer.CanHold(t.Path, property)
                ? writer.StoredAs == "iProperty" ? $"iProperty ({InventorProperties.Find(property)?.Tab ?? "Custom"})" : writer.StoredAs
                : "Vault only")
            .Distinct()
            .ToList();
        return string.Join(" / ", places);
    }

    /// <summary>The properties the user changed, by name (an empty value clears a property). Set when the user clicks Save.</summary>
    public IReadOnlyDictionary<string, string> Changes { get; private set; } = new Dictionary<string, string>();

    public string VersionComment => Comment.Text.Trim();

    private void Items_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e) =>
        Dispatcher.BeginInvoke(() => RevisionNote.Visibility =
            _rows.Any(r => r.Edited && r.Name.Equals("Revision", StringComparison.OrdinalIgnoreCase)) ? Visibility.Visible : Visibility.Collapsed);

    private void AddProperty_Click(object sender, RoutedEventArgs e)
    {
        var prompt = new PromptWindow("Add Property",
            "Name of the property. Inventor files get it as a custom iProperty; drawings hold it in the title block if it has an attribute for it, otherwise it's kept in the vault.")
        { Owner = this };
        if (prompt.ShowDialog() != true || prompt.Value.Trim() is not { Length: > 0 } name)
            return;
        var row = _rows.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            row = new PropertyRow(name, "") { SavedIn = SavedIn(name) };
            _rows.Add(row);
            Items.Items.Refresh();
        }
        Items.SelectedItem = row;
        Items.ScrollIntoView(row);
        Items.CurrentCell = new DataGridCellInfo(row, Items.Columns[1]);
        Items.BeginEdit();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Items.CommitEdit(DataGridEditingUnit.Row, true);
        Changes = _rows.Where(r => r.Edited).ToDictionary(r => r.Name, r => r.Value.Trim(), StringComparer.OrdinalIgnoreCase);
        DialogResult = Changes.Count > 0;
    }

    private sealed class PropertyRow(string name, string? commonValue) : INotifyPropertyChanged
    {
        private readonly string? _original = commonValue;

        public string Name { get; } = name;

        public string SavedIn { get; init; } = "";

        public string Value
        {
            get;
            set
            {
                field = value;
                Changed();
                Changed(nameof(Edited));
                Changed(nameof(ShowsVaries));
            }
        } = commonValue ?? "";

        /// <summary>True once the user typed a value different from what the files have (any value, where they differ).</summary>
        public bool Edited => _original is null ? Value.Length > 0 : Value != _original;

        public bool ShowsVaries => _original is null && Value.Length == 0;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}
