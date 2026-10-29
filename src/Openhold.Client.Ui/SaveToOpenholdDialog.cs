using System.Drawing;
using System.Windows.Forms;
using Openhold.Core.Configuration;
using Openhold.Core.Contracts;
using Openhold.Core.Numbering;

namespace Openhold.Client.Ui;

/// <summary>
/// Save to Openhold: names a new file by the vault's naming rules where it is saved. The folder picks the category and
/// its numbering scheme; the scheme's fields fill in from the folders above and the assembly the file is made in;
/// people pick or type the rest and see the name it will get. Nothing is reserved until Save is clicked.
/// Shared by the CAD add-ins so every host names files the same way.
/// </summary>
public sealed class SaveToOpenholdDialog : Form
{
    private readonly string _extension;
    private readonly string? _parentFileName;
    private readonly VaultConfiguration _configuration;
    private readonly IReadOnlyList<NumberingScheme> _schemes;
    private readonly Func<NumberingScheme, IReadOnlyDictionary<string, string>, string> _preview;

    private readonly TextBox _folder;
    private readonly Label _rule;
    private readonly ComboBox _scheme;
    private readonly TableLayoutPanel _fields;
    private readonly TextBox _freeName;
    private readonly Label _freeNameLabel;
    private readonly TextBox _description;
    private readonly Label _name;
    private readonly Button _save;
    private readonly Dictionary<string, Control> _inputs = new(StringComparer.OrdinalIgnoreCase);
    private string? _schemeChosenByPerson;
    private bool _updating;

    /// <param name="documentName">What is being saved, e.g. "Part3" or "Bracket.ipt".</param>
    /// <param name="extension">The file's extension, e.g. ".ipt".</param>
    /// <param name="folder">The vault folder to start in, e.g. the parent assembly's.</param>
    /// <param name="parentFileName">The assembly it is made in, if any (its file name).</param>
    /// <param name="preview">The next number for a scheme and field values; throws with a readable message if there's none.</param>
    /// <param name="browse">Lets people pick a folder; returns its vault path, or null if they cancel.</param>
    public SaveToOpenholdDialog(string documentName, string extension, string folder, string? parentFileName, VaultConfiguration configuration,
        IReadOnlyList<NumberingScheme> schemes, Func<NumberingScheme, IReadOnlyDictionary<string, string>, string> preview, Func<string, string?> browse)
    {
        _extension = extension;
        _parentFileName = parentFileName;
        _configuration = configuration;
        _schemes = schemes.Where(s => s.Enabled && !configuration.NumbersCatalogueItems(s.Id)).ToList();
        _preview = preview;

        Text = $"Openhold: Save {documentName} to the vault";
        Font = SystemFonts.MessageBoxFont;
        AutoScaleMode = AutoScaleMode.Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };

        layout.Controls.Add(Caption("Save in folder"));
        var folderRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        _folder = new TextBox { Width = 470, Text = folder, AccessibleName = "Vault folder" };
        var browseButton = new Button { Text = "Browse…", AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
        browseButton.Click += (_, _) =>
        {
            if (browse(_folder.Text) is { } picked)
                _folder.Text = picked;
        };
        folderRow.Controls.Add(_folder);
        folderRow.Controls.Add(browseButton);
        layout.Controls.Add(folderRow);
        if (parentFileName is not null)
            layout.Controls.Add(Note($"Made in {parentFileName}"));

        _rule = Note("");
        layout.Controls.Add(_rule);

        layout.Controls.Add(Caption("Numbering"));
        _scheme = new ComboBox { Width = 560, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Numbering scheme" };
        _scheme.Items.Add("(name it myself)");
        foreach (var scheme in _schemes)
            _scheme.Items.Add(new SchemeItem(scheme));
        layout.Controls.Add(_scheme);

        _fields = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 8, 0, 0) };
        _fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        _fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
        _fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.Controls.Add(_fields);

        _freeNameLabel = Caption("File name");
        _freeName = new TextBox { Width = 560, AccessibleName = "File name" };
        layout.Controls.Add(_freeNameLabel);
        layout.Controls.Add(_freeName);

        layout.Controls.Add(Caption("Description (optional)"));
        _description = new TextBox { Width = 560, AccessibleName = "Description" };
        layout.Controls.Add(_description);
        layout.Controls.Add(Note("Goes in the Description iProperty. The number goes in Part Number."));

        _name = new Label { AutoSize = true, MaximumSize = new Size(560, 0), Font = new Font(Font.FontFamily, Font.Size * 1.25f, FontStyle.Bold), Margin = new Padding(0, 14, 0, 4) };
        layout.Controls.Add(_name);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        _save = new Button { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_save);
        layout.Controls.Add(buttons);

        Controls.Add(layout);
        AcceptButton = _save;
        CancelButton = cancel;

        _folder.Leave += (_, _) => Suggest();
        _folder.TextChanged += (_, _) => { if (!_folder.Focused) Suggest(); };
        _scheme.SelectedIndexChanged += (_, _) =>
        {
            if (_updating)
                return;
            _schemeChosenByPerson = (_scheme.SelectedItem as SchemeItem)?.Scheme.Id ?? "";
            Suggest();
        };
        _freeName.TextChanged += (_, _) => ShowName();
        Shown += (_, _) => Suggest();
    }

    /// <summary>The vault folder chosen.</summary>
    public string Folder => _folder.Text.Trim().TrimEnd('/');

    /// <summary>The scheme to take a number from; null when people named the file themselves.</summary>
    public NumberingScheme? Scheme => (_scheme.SelectedItem as SchemeItem)?.Scheme;

    /// <summary>The scheme's field values as chosen.</summary>
    public IReadOnlyDictionary<string, string> Fields => _inputs.ToDictionary(i => i.Key, i => Value(i.Value), StringComparer.OrdinalIgnoreCase);

    /// <summary>The file name (without extension) when no scheme is used.</summary>
    public string FreeName => _freeName.Text.Trim();

    public string Description => _description.Text.Trim();

    /// <summary>Re-reads the rules for the folder: category, scheme and fields filled in from where the file goes.</summary>
    private void Suggest()
    {
        if (!Folder.StartsWith('$'))
        {
            _rule.Text = "Give a vault folder, e.g. $/Models/PRJ01 - Pump skid/02 - Structure.";
            return;
        }
        var suggestion = NamingRules.Suggest(_configuration, _schemes, Folder, "new" + _extension, _parentFileName,
            string.IsNullOrEmpty(_schemeChosenByPerson) ? null : _schemeChosenByPerson);
        if (_schemeChosenByPerson == "")
            suggestion = suggestion with { Scheme = null, Fields = [] };
        _rule.Text = suggestion.CategoryName is null
            ? "No category's rules cover this folder, so the file has no numbering rule here."
            : $"Category: {suggestion.CategoryName}." + (suggestion.Enforced ? " Its new files must be named by its numbering." : "");

        _updating = true;
        _scheme.SelectedItem = suggestion.Scheme is { } chosen ? _scheme.Items.OfType<SchemeItem>().First(s => s.Scheme.Id == chosen.Id) : _scheme.Items[0];
        _updating = false;

        _fields.SuspendLayout();
        _fields.Controls.Clear();
        _fields.RowStyles.Clear();
        _inputs.Clear();
        foreach (var field in suggestion.Fields)
        {
            Control input;
            if (field.Field.Choices is { Count: > 0 } choices)
            {
                var list = new ComboBox { Width = 240, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = field.Label };
                list.Items.AddRange([.. choices.Select(c => new CodeItem(c.Code, c.Meaning))]);
                list.SelectedItem = list.Items.OfType<CodeItem>().FirstOrDefault(c => string.Equals(c.Code, field.Value, StringComparison.OrdinalIgnoreCase));
                list.SelectedIndexChanged += (_, _) => ShowName();
                input = list;
            }
            else
            {
                var text = new TextBox { Width = 240, Text = field.Value ?? "", AccessibleName = field.Label };
                text.TextChanged += (_, _) => ShowName();
                input = text;
            }
            _inputs[field.Field.Name] = input;
            _fields.Controls.Add(new Label { Text = field.Label, AutoSize = true, Margin = new Padding(0, 6, 8, 0) });
            _fields.Controls.Add(input);
            _fields.Controls.Add(new Label
            {
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(6, 6, 0, 0),
                Text = field.Source switch
                {
                    NumberingSources.Folder => "from the folders",
                    NumberingSources.Parent => "from the assembly",
                    "default" => "default",
                    _ => field.Field.Hint ?? "",
                },
            });
        }
        _fields.ResumeLayout();
        _freeName.Visible = _freeNameLabel.Visible = Scheme is null;
        ShowName();
    }

    /// <summary>Shows the name the file will get, or what's still missing.</summary>
    private void ShowName()
    {
        if (Scheme is not { } scheme)
        {
            _save.Enabled = FreeName.Length > 0 && FreeName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
            _name.ForeColor = SystemColors.ControlText;
            _name.Text = _save.Enabled ? $"{FreeName}{_extension}" : "Type a file name.";
            return;
        }
        var missing = _inputs.Where(i => Value(i.Value).Length == 0).Select(i => i.Value.AccessibleName ?? i.Key).ToList();
        if (missing.Count > 0)
        {
            _save.Enabled = false;
            _name.ForeColor = SystemColors.GrayText;
            _name.Text = $"Choose {string.Join(", ", missing)} to see the name.";
            return;
        }
        // The scheme's own rules (codes, letters or digits, length) are checked here, before asking the server.
        var values = Fields;
        if (scheme.Fields.Select(f => values.TryGetValue(f.Name, out var value) ? f.Problem(value) : null).FirstOrDefault(p => p is not null) is { } problem)
        {
            _save.Enabled = false;
            _name.ForeColor = Color.Firebrick;
            _name.Text = problem;
            return;
        }
        try
        {
            _name.Text = $"{_preview(scheme, Fields)}{_extension}";
            _name.ForeColor = SystemColors.ControlText;
            _save.Enabled = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _name.ForeColor = Color.Firebrick;
            _name.Text = ex.Message;
            _save.Enabled = false;
        }
    }

    private static string Value(Control input) => input switch
    {
        ComboBox { SelectedItem: CodeItem item } => item.Code,
        TextBox text => text.Text.Trim(),
        _ => "",
    };

    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(0, 10, 0, 4), Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold) };

    private static Label Note(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 4, 0, 0) };

    private sealed record SchemeItem(NumberingScheme Scheme)
    {
        public override string ToString() => $"{Scheme.Name}   ({Scheme.Template})";
    }

    private sealed record CodeItem(string Code, string Meaning)
    {
        public override string ToString() => string.IsNullOrWhiteSpace(Meaning) ? Code : $"{Code}  –  {Meaning}";
    }
}
