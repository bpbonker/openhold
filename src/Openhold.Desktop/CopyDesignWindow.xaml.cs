using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Openhold.Client.Sdk.Workspace;
using Openhold.Core.Configuration;
using Openhold.Core.Contracts;

namespace Openhold.Desktop;

/// <summary>
/// Copy Design: lists the chosen files and everything they use, lets the user pick which to copy and name the
/// copies: the next free numbers from a numbering scheme, or by hand / replace / prefix / suffix. Library parts
/// (e.g. buyout) start unticked so they stay shared, and a drawing gets the same number as its model.
/// </summary>
public partial class CopyDesignWindow : Window
{
    /// <summary>The "no scheme" choice: the user names the copies.</summary>
    private static readonly NumberingScheme KeepNames = new("", "(name them myself)", "", false, TimeSpan.Zero, true);

    private readonly List<CopyRow> _rows;
    private readonly List<ReservedNumber> _reserved = [];
    private string? _targetVaultFolder;
    private bool _copied;

    public CopyDesignWindow(IReadOnlyList<FileStatus> files, VaultConfiguration configuration, IReadOnlyList<NumberingScheme> schemes)
    {
        InitializeComponent();
        _rows = files.Select(f => new CopyRow(f.LocalPath, App.Workspace.ToVaultPath(Path.GetDirectoryName(f.LocalPath)!))
        {
            Copy = configuration.Category(f.File?.Category) is not { Library: true },
        }).ToList();
        foreach (var row in _rows)
            row.PropertyChanged += async (_, e) => { if (e.PropertyName == nameof(CopyRow.Copy)) await NameAsync(); };
        Items.ItemsSource = _rows;

        // Schemes that need typed-in fields (e.g. {field:Project}) can't be filled from here.
        var usable = schemes.Where(s => s.Enabled && !s.Template.Contains("{field:", StringComparison.Ordinal)).ToList();
        Schemes.ItemsSource = new[] { KeepNames }.Concat(usable).ToList();
        var categoryScheme = configuration.Category(files.FirstOrDefault()?.File?.Category)?.NumberingSchemeId;
        Schemes.SelectedItem = usable.FirstOrDefault(s => s.Id == categoryScheme) ?? KeepNames;
        Closed += async (_, _) => await ReleaseAsync(_copied ? _reserved.Where(r => !Copies.Values.Any(c => Path.GetFileNameWithoutExtension(c) == r.Formatted)) : _reserved);
    }

    /// <summary>Original local path → copy's local path, for the ticked files. Set when the user clicks Copy.</summary>
    public IReadOnlyDictionary<string, string> Copies { get; private set; } = new Dictionary<string, string>();

    private NumberingScheme? Scheme => Schemes.SelectedItem is NumberingScheme { Id.Length: > 0 } scheme ? scheme : null;

    private async void Schemes_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Numbers reserved from another scheme go back; they were never used.
        await ReleaseAsync(_reserved.ToList());
        ManualNaming.IsEnabled = Scheme is null;
        await NameAsync();
    }

    private async void Naming_Changed(object sender, TextChangedEventArgs? e) => await NameAsync();

    /// <summary>
    /// Names every ticked copy. With a scheme, each model gets the next number and a drawing named like its model
    /// gets the model's number (BRACKET.ipt and BRACKET.idw both become EL-000124). Numbers are reserved as they
    /// appear, so nobody else gets them; unused ones go back when the window closes.
    /// </summary>
    private async Task NameAsync()
    {
        var ticked = _rows.Where(r => r.Copy).ToList();
        foreach (var row in _rows.Where(r => !r.Copy))
            row.NewName = "";

        if (Scheme is not { } scheme)
        {
            foreach (var row in ticked)
            {
                var stem = Path.GetFileNameWithoutExtension(row.Name);
                if (FindText.Text.Length > 0)
                    stem = stem.Replace(FindText.Text, ReplaceText.Text, StringComparison.OrdinalIgnoreCase);
                row.NewName = PrefixText.Text + stem + SuffixText.Text + Path.GetExtension(row.Name);
            }
            return;
        }

        var groups = ticked.GroupBy(r => Path.GetFileNameWithoutExtension(r.Name), StringComparer.OrdinalIgnoreCase).ToList();
        if (groups.Count > _reserved.Count)
        {
            try
            {
                _reserved.AddRange(await App.Client.ReserveNumbersAsync(scheme.Id, groups.Count - _reserved.Count));
                ShowProblem(null);
            }
            catch (Exception ex) when (ex is Client.Sdk.OpenholdApiException or System.Net.Http.HttpRequestException)
            {
                ShowProblem($"Couldn't get numbers from {scheme.Name}: {ex.Message}");
                return;
            }
        }
        for (var i = 0; i < groups.Count; i++)
        {
            foreach (var row in groups[i])
                row.NewName = _reserved[i].Formatted + Path.GetExtension(row.Name);
        }
    }

    private static async Task ReleaseAsync(IEnumerable<ReservedNumber> numbers)
    {
        var list = numbers.ToList();
        if (list.Count == 0)
            return;
        try
        {
            await App.Client.ReleaseNumbersAsync(list.Select(n => n.Formatted));
        }
        catch (Exception ex) when (ex is Client.Sdk.OpenholdApiException or System.Net.Http.HttpRequestException)
        {
            // Unreleased reservations expire on their own; nothing is lost.
        }
    }

    private void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPickerWindow("Put the copies in:") { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedVaultPath is not { } target)
            return;
        _targetVaultFolder = target;
        TargetText.Text = target.TrimStart('$', '/').Replace("/", " › ") is { Length: > 0 } shown ? shown : "the vault's top folder";
    }

    private void ShowProblem(string? problem)
    {
        Problem.Text = problem ?? "";
        Problem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Items.CommitEdit(DataGridEditingUnit.Row, true);
        var ticked = _rows.Where(r => r.Copy).ToList();
        var copies = ticked.ToDictionary(r => r.LocalPath, r => Path.Combine(
            _targetVaultFolder is null ? Path.GetDirectoryName(r.LocalPath)! : App.Workspace.ToLocalPath(_targetVaultFolder), r.NewName.Trim()));

        var problem = ticked.Count == 0 ? "Tick at least one file to copy."
            : ticked.FirstOrDefault(r => r.NewName.Trim().Length == 0 || r.NewName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) is { } bad
                ? $"\"{bad.NewName}\" can't be used as a file name."
            : copies.FirstOrDefault(c => string.Equals(c.Key, c.Value, StringComparison.OrdinalIgnoreCase)) is { Key: not null } same
                ? $"{Path.GetFileName(same.Key)} needs a new name (or a different folder)."
            : copies.Values.GroupBy(v => v, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } clash
                ? $"Two copies would both be called {Path.GetFileName(clash.Key)}."
            : null;
        ShowProblem(problem);
        if (problem is not null)
            return;
        Copies = copies;
        _copied = true;
        DialogResult = true;
    }

    private sealed class CopyRow(string localPath, string vaultFolder) : INotifyPropertyChanged
    {
        public string LocalPath { get; } = localPath;
        public string Name { get; } = Path.GetFileName(localPath);
        public string Folder { get; } = vaultFolder.TrimStart('$', '/').Replace("/", " › ");

        public bool Copy
        {
            get;
            set
            {
                field = value;
                Changed();
            }
        } = true;

        public string NewName
        {
            get;
            set
            {
                field = value;
                Changed();
            }
        } = "";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
