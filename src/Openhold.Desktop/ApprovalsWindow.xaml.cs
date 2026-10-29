using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Openhold.Client.Sdk;
using Openhold.Core.Contracts;

namespace Openhold.Desktop;

/// <summary>The approvals inbox: change requests waiting for this person's sign-off, and their own pending requests.</summary>
public partial class ApprovalsWindow : Window
{
    public ApprovalsWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync(null);
    }

    /// <summary>True once anything was approved, rejected or withdrawn, so the main window refreshes.</summary>
    public bool ChangedAny { get; private set; }

    private ChangeRequestInfo? Current => (Requests.SelectedItem as RequestRow)?.Request;

    private async Task LoadAsync(Guid? select)
    {
        var requests = await App.Client.ListChangeRequestsAsync();
        var rows = requests.Select(r => new RequestRow(r)).ToList();
        Requests.ItemsSource = rows;
        EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Requests.SelectedItem = rows.FirstOrDefault(r => r.Request.Id == select) ?? rows.FirstOrDefault();
        ShowDetail();
    }

    private void Requests_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDetail();

    private void ShowDetail()
    {
        ShowProblem(null);
        var request = Current;
        Detail.Visibility = request is null ? Visibility.Collapsed : Visibility.Visible;
        ApproveButton.IsEnabled = RejectButton.IsEnabled = request?.CanApprove == true;
        CancelButton.IsEnabled = request is { IsMine: true, Status: "pending" };
        if (request is null)
            return;

        DetailTitle.Text = $"{request.RequestedBy} asks to change {Files(request.Files.Count)} to {request.ToState}";
        DetailComment.Text = request.Comment.Length > 0 ? $"“{request.Comment}”" : "No comment.";
        DetailFiles.ItemsSource = request.Files.Select(f => $"{f.Name}   (v{f.Version}, {f.FromState})").ToList();
        DetailProgress.ItemsSource = request.Approvals.Select(a => $"{a.GroupName}: {a.Approved} of {a.Required} approved").ToList();
        DetailDecisions.ItemsSource = request.Decisions.Select(d =>
            $"{d.Username} {d.Decision} {d.DecidedAt.ToLocalTime().ToString("d MMM h:mm tt", CultureInfo.CurrentCulture)}" +
            (d.Comment.Length > 0 ? $": “{d.Comment}”" : "")).ToList();
    }

    private async void Approve_Click(object sender, RoutedEventArgs e) =>
        await DecideAsync(id => App.Client.ApproveAsync(id, Comment.Text.Trim()));

    private async void Reject_Click(object sender, RoutedEventArgs e)
    {
        if (Comment.Text.Trim().Length == 0)
        {
            ShowProblem("Say why you're rejecting it, so the person who asked knows what to change.");
            Comment.Focus();
            return;
        }
        await DecideAsync(id => App.Client.RejectAsync(id, Comment.Text.Trim()));
    }

    private async void Withdraw_Click(object sender, RoutedEventArgs e) =>
        await DecideAsync(id => App.Client.CancelChangeRequestAsync(id));

    private async Task DecideAsync(Func<Guid, Task<ChangeRequestInfo>> decide)
    {
        if (Current is not { } request)
            return;
        IsEnabled = false;
        try
        {
            var result = await decide(request.Id);
            ChangedAny = true;
            Comment.Clear();
            await LoadAsync(request.Id);
            if (result.Status == "approved")
                ShowProblem(null, $"Approved. {Files(result.Files.Count)} changed to {result.ToState}.");
            else if (result.Status == "blocked")
                ShowProblem($"Your approval is recorded, but the change can't be made any more: {string.Join("; ", result.Problems)}. {result.RequestedBy} can submit it again.");
        }
        catch (OpenholdApiException ex)
        {
            ShowProblem(ex.Message);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void ShowProblem(string? problem, string? note = null)
    {
        Problem.Text = problem ?? note ?? "";
        Problem.Foreground = (System.Windows.Media.Brush)FindResource(problem is null ? "Brand" : "DangerText");
        Problem.Visibility = Problem.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string Files(int count) => count == 1 ? "1 file" : $"{count} files";

    private sealed class RequestRow(ChangeRequestInfo request)
    {
        public ChangeRequestInfo Request { get; } = request;
        public string Title => $"{Files(Request.Files.Count)} → {Request.ToState}" + (Request.Files.Count == 1 ? $" ({Request.Files[0].Name})" : "");
        public string RequestedBy => Request.IsMine ? "You" : Request.RequestedBy;
        public string When => Request.RequestedAt.ToLocalTime().ToString("d MMM h:mm tt", CultureInfo.CurrentCulture);
        public string ForYou => Request.CanApprove ? "Your approval" : Request.IsMine ? "Waiting" : "";
        public override string ToString() => Title;
    }
}
