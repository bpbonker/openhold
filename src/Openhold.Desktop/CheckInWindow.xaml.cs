using System.Windows;

namespace Openhold.Desktop;

/// <summary>Check-in dialog: what's being checked in, a comment, and "keep checked out". Enter checks in; Esc cancels.</summary>
public partial class CheckInWindow : Window
{
    public CheckInWindow(IReadOnlyList<string> fileNames)
    {
        InitializeComponent();
        Heading.Text = fileNames.Count == 1 ? $"Check in {fileNames[0]}" : $"Check in {fileNames.Count} files";
        FileList.Text = fileNames.Count == 1 ? "" : string.Join(", ", fileNames.Take(8)) + (fileNames.Count > 8 ? $" and {fileNames.Count - 8} more" : "");
        FileList.Visibility = fileNames.Count == 1 ? Visibility.Collapsed : Visibility.Visible;
        Loaded += (_, _) => CommentBox.Focus();
    }

    public string Comment => CommentBox.Text.Trim();

    public bool KeepCheckedOut => KeepBox.IsChecked == true;

    private void CheckIn_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
