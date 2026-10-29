using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Openhold.Client.Sdk.Workspace;
using Openhold.Core.Configuration;
using Openhold.Core.Contracts;

namespace Openhold.Desktop;

/// <summary>
/// Change State: offers the transitions the selected files' lifecycles allow this person, checks with the server
/// what would happen to each file (and why not), and says when the change needs approval first.
/// </summary>
public partial class ChangeStateWindow : Window
{
    // The design system's colours, so they follow a high-contrast theme too.
    private Brush Good => (Brush)FindResource("Brand");
    private Brush Bad => (Brush)FindResource("DangerText");

    private readonly VaultConfiguration _configuration;
    private readonly IReadOnlyList<FileStatus> _selected;
    private IReadOnlyList<FileStatus> _files;
    private int _checkNumber;

    public ChangeStateWindow(IReadOnlyList<FileStatus> files, VaultConfiguration configuration)
    {
        InitializeComponent();
        _configuration = configuration;
        _selected = _files = files.Where(f => f.File is not null).ToList();

        // Every state any selected file can move to; files that can't make the move are shown with the reason.
        Targets.ItemsSource = _files
            .SelectMany(f => TransitionsFor(f.File!))
            .GroupBy(t => t.To, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Target(g.First().To, g.First().DisplayName))
            .ToList();
        ShowRows(null);
        Loaded += (_, _) =>
        {
            if (Targets.Items.Count == 0)
                ShowProblem("These files have no lifecycle states you can move them to.");
            else
                Targets.SelectedIndex = 0;
        };
    }

    /// <summary>The files to change (the selection, plus the files they use if chosen). Set when the user clicks the button.</summary>
    public IReadOnlyList<string> Paths { get; private set; } = [];

    public string TargetState => (Targets.SelectedItem as Target)?.State ?? "";

    public string CommentText => Comment.Text.Trim();

    private IEnumerable<LifecycleTransition> TransitionsFor(VaultFileInfo file)
    {
        var lifecycle = _configuration.LifecycleFor(file.Category);
        if (lifecycle is null || file.State is null)
            return [];
        return lifecycle.TransitionsFrom(file.State).Where(t => t.Allows(_configuration, App.Client.Username));
    }

    private async void Targets_SelectionChanged(object sender, SelectionChangedEventArgs e) => await CheckAsync();

    private async void IncludeUses_Changed(object sender, RoutedEventArgs e)
    {
        if (IncludeUses.IsChecked == true)
        {
            IsEnabled = false;
            try
            {
                // Only the used files that aren't already where they're going, and that have a lifecycle at all.
                var all = await App.Workspace.WithDependenciesAsync(_selected.Select(f => f.LocalPath));
                var statuses = await App.Workspace.GetStatusAsync(all);
                _files = statuses
                    .Where(s => s.File is { State: not null } file && (_selected.Any(x => x.LocalPath == s.LocalPath) || !file.StateReleased))
                    .ToList();
            }
            finally
            {
                IsEnabled = true;
            }
        }
        else
        {
            _files = _selected;
        }
        await CheckAsync();
    }

    /// <summary>Asks the server what would happen, without changing anything, and shows it per file.</summary>
    private async Task CheckAsync()
    {
        if (Targets.SelectedItem is not Target target)
            return;
        var check = ++_checkNumber;
        Apply.IsEnabled = false;
        Apply.Content = target.Label;
        StateChangeResult result;
        try
        {
            result = await App.Client.ChangeStateAsync(new ChangeStateRequest(Guid.NewGuid(), _files.Select(f => f.File!.Id).ToList(), target.State, CommentText, CheckOnly: true));
        }
        catch (Exception ex) when (ex is Client.Sdk.OpenholdApiException or System.Net.Http.HttpRequestException)
        {
            ShowProblem(ex.Message);
            return;
        }
        if (check != _checkNumber)
            return; // the user has moved on to another choice

        ShowRows(result);
        var blocking = result.Problems.Where(p => !p.Message.StartsWith("a comment is required", StringComparison.Ordinal)).ToList();
        var commentNeeded = result.Problems.Count > blocking.Count;
        ShowProblem(blocking.Count > 0
            ? $"Nothing can change until every file can: {blocking.Count} problem(s) shown above."
            : commentNeeded ? "A comment is required for this change." : null);
        ApprovalNote.Text = result.NeedsApproval ? ApprovalText(target) : "";
        ApprovalNote.Visibility = result.NeedsApproval ? Visibility.Visible : Visibility.Collapsed;
        Apply.Content = result.NeedsApproval ? "Send for approval" : target.Label;
        Apply.IsEnabled = blocking.Count == 0;
    }

    private string ApprovalText(Target target)
    {
        var groups = _files
            .Select(f => _configuration.LifecycleFor(f.File!.Category)?.Transition(f.File.State ?? "", target.State))
            .SelectMany(t => t?.Approvals ?? [])
            .Select(a => $"{a.Required} from {_configuration.Group(a.Group)?.Name ?? a.Group}")
            .Distinct()
            .ToList();
        return $"This change needs approval ({string.Join(", ", groups)}). The files stay as they are until it's approved.";
    }

    private void ShowRows(StateChangeResult? result)
    {
        Items.ItemsSource = _files.Select(f =>
        {
            var problems = result?.Problems.Where(p => p.FileId == f.File!.Id).Select(p => p.Message).ToList() ?? [];
            var outcome = result is null ? "" : problems.Count > 0 ? string.Join("; ", problems) : TargetState;
            return new Row(Path.GetFileName(f.LocalPath), f.File!.State ?? "(no lifecycle)", outcome, problems.Count > 0 ? Bad : Good);
        }).ToList();
    }

    private void ShowProblem(string? text)
    {
        Problem.Text = text ?? "";
        Problem.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        // Check again with the comment as typed, so a required comment is caught here rather than after closing.
        await CheckAsync();
        if (!Apply.IsEnabled || Problem.Visibility == Visibility.Visible)
            return;
        Paths = _files.Select(f => f.LocalPath).ToList();
        DialogResult = true;
    }

    private sealed record Target(string State, string Label)
    {
        public string Text => string.Equals(Label, $"Change to {State}", StringComparison.Ordinal) ? State : $"{Label}  →  {State}";
    }

    private sealed record Row(string Name, string Current, string Outcome, Brush OutcomeBrush);
}
