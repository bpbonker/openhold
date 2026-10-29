using System.Windows;
using System.Windows.Controls;
using Openhold.Core.Configuration;
using Openhold.Core.Properties;

namespace Openhold.Desktop;

/// <summary>
/// Revise: the explicit, user-chosen revision change (check-in and check-out never revise). Suggests each
/// file's next revision from its category's revision scheme, which the user can type over. Released files are
/// reopened first, as part of the same action.
/// </summary>
public partial class ReviseWindow : Window
{
    private readonly List<ReviseRow> _rows;

    public ReviseWindow(IEnumerable<FileRow> files, VaultConfiguration configuration)
    {
        InitializeComponent();
        _rows = files.Select(f =>
        {
            var file = f.Status.File!;
            var current = file.Properties.GetValueOrDefault("Revision") ?? "";
            var scheme = configuration.RevisionScheme(configuration.Category(file.Category)?.RevisionSchemeId);
            return new ReviseRow(f.LocalPath, f.Name, current.Length == 0 ? "(none)" : current, current, scheme, file.StateLocked ? $"{file.State}: reopened first" : file.State ?? "");
        }).ToList();
        Major.Visibility = _rows.Any(r => r.Scheme is { Parts.Count: > 1 }) ? Visibility.Visible : Visibility.Collapsed;
        Suggest();
        Items.ItemsSource = _rows;
        Loaded += (_, _) => Comment.Focus();
    }

    /// <summary>Local path → new revision. Set when the user clicks Revise.</summary>
    public IReadOnlyDictionary<string, string> NewRevisions { get; private set; } = new Dictionary<string, string>();

    public string Reason => Comment.Text.Trim();

    private void Major_Changed(object sender, RoutedEventArgs e)
    {
        Items.CommitEdit(DataGridEditingUnit.Row, true);
        Suggest();
        Items.Items.Refresh();
    }

    /// <summary>Fills in each file's suggested revision: the next minor revision, or major if ticked.</summary>
    private void Suggest()
    {
        foreach (var row in _rows)
        {
            row.NewRevision = row.Scheme is { } scheme
                ? scheme.Next(row.Value, Major.IsChecked == true ? 0 : null)
                : Revisions.Next(row.Value);
        }
    }

    private void Revise_Click(object sender, RoutedEventArgs e)
    {
        Items.CommitEdit(DataGridEditingUnit.Row, true);
        var problem = Reason.Length == 0 ? "Say why the files are being revised; it's recorded with the new version."
            : _rows.FirstOrDefault(r => r.NewRevision.Trim().Length == 0) is { } blank ? $"Give {blank.Name} a new revision."
            : _rows.FirstOrDefault(r => r.NewRevision.Trim() == r.Current) is { } same ? $"{same.Name} is already at revision {same.Current}."
            : null;
        Problem.Text = problem ?? "";
        Problem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        if (problem is not null)
            return;
        NewRevisions = _rows.ToDictionary(r => r.LocalPath, r => r.NewRevision.Trim());
        DialogResult = true;
    }

    private sealed class ReviseRow(string localPath, string name, string current, string value, RevisionScheme? scheme, string state)
    {
        public string LocalPath { get; } = localPath;
        public string Name { get; } = name;
        public string Current { get; } = current;
        public string Value { get; } = value;
        public RevisionScheme? Scheme { get; } = scheme;
        public string State { get; } = state;
        public string NewRevision { get; set; } = "";
    }
}
