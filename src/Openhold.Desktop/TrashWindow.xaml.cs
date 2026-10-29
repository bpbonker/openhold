using System.Windows;
using System.Windows.Controls;
using Openhold.Core.Contracts;

namespace Openhold.Desktop;

/// <summary>The vault's trash bin: deleted files, restorable with their history.</summary>
public partial class TrashWindow : Window
{
    public TrashWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    /// <summary>True once something was restored, so the main window refreshes.</summary>
    public bool RestoredAny { get; private set; }

    private async Task LoadAsync()
    {
        var items = (await App.Client.ListTrashAsync())
            .Select(i => i with { DeletedAt = i.DeletedAt.ToLocalTime(), FolderPath = i.FolderPath.TrimStart('$', '/').Replace("/", " › ") })
            .ToList();
        Items.ItemsSource = items;
        EmptyHint.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Items_SelectionChanged(object sender, SelectionChangedEventArgs e) => RestoreButton.IsEnabled = Items.SelectedItems.Count > 0;

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var selected = Items.SelectedItems.Cast<TrashItem>().ToList();
        var all = await App.Client.ListTrashAsync();
        // Restore with the real vault paths (the list shows them in a friendlier form).
        var items = all.Where(i => selected.Any(s => s.FileId == i.FileId)).ToList();
        IsEnabled = false;
        Openhold.Client.Sdk.Workspace.WorkspaceResult result;
        try
        {
            result = await App.Workspace.RestoreFromTrashAsync(items);
        }
        finally
        {
            IsEnabled = true;
        }
        MessageBox.Show(this, result.Summary, "Openhold", MessageBoxButton.OK, result.Succeeded ? MessageBoxImage.Information : MessageBoxImage.Warning);
        RestoredAny |= result.Succeeded;
        await LoadAsync();
    }
}
