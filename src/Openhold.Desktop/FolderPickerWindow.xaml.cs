using System.Windows;

namespace Openhold.Desktop;

/// <summary>Choose a vault folder to move files into.</summary>
public partial class FolderPickerWindow : Window
{
    private readonly FolderNode _root = new("Vault", "$", null);

    public FolderPickerWindow(string heading)
    {
        InitializeComponent();
        Heading.Text = heading;
        Folders.ItemsSource = new[] { _root };
        _root.IsExpanded = true;
    }

    public string? SelectedVaultPath => (Folders.SelectedItem as FolderNode)?.VaultPath;

    private void Folders_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        MoveButton.IsEnabled = e.NewValue is FolderNode { VaultPath.Length: > 0 };

    private void Move_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
