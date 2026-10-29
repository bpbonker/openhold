using System.Windows;

namespace Openhold.Desktop;

/// <summary>Asks for one line of text, e.g. a new folder name or a file's new name.</summary>
public partial class PromptWindow : Window
{
    public PromptWindow(string title, string label, string initialValue = "")
    {
        InitializeComponent();
        Title = $"Openhold: {title}";
        Label.Text = label;
        Input.Text = initialValue;
        Loaded += (_, _) =>
        {
            Input.Focus();
            // Like Windows Explorer, select the name but not the extension, so typing replaces just the name.
            var extension = System.IO.Path.GetExtension(initialValue);
            Input.Select(0, initialValue.Length - extension.Length);
        };
    }

    public string Value => Input.Text;

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
