using System.Windows;
using Openhold.Core.Contracts;

namespace Openhold.Desktop;

/// <summary>Asks for a new Openhold account's details.</summary>
public partial class NewAccountWindow : Window
{
    public NewAccountWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Username.Focus();
    }

    public CreateAccountRequest? Request { get; private set; }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        var problem = Username.Text.Trim().Length == 0 ? "Give the account a username."
            : Username.Text.Trim().Any(char.IsWhiteSpace) ? "A username can't contain spaces."
            : DisplayName.Text.Trim().Length == 0 ? "Give the person's full name."
            : null;
        Problem.Text = problem ?? "";
        Problem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        if (problem is not null)
            return;
        Request = new CreateAccountRequest(Username.Text.Trim(), DisplayName.Text.Trim(),
            Email.Text.Trim().Length > 0 ? Email.Text.Trim() : null, RequireTwoFactor.IsChecked == true);
        DialogResult = true;
    }
}
