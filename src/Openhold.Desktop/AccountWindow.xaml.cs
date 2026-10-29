using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Openhold.Client.Sdk;

namespace Openhold.Desktop;

/// <summary>Your own account: change your Openhold password and set up two-factor sign-in.</summary>
public partial class AccountWindow : Window
{
    public AccountWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var me = await App.Client.WhoAmIAsync();
        Who.Text = me.DisplayName == me.Username ? me.Username : $"{me.DisplayName} ({me.Username})";
        var openholdAccount = me.Kind == "local";
        Kind.Text = me.Kind switch
        {
            "local" => "Openhold account.",
            "oidc" => "Organisation account: your password and two-factor sign-in are managed by your organisation.",
            _ => "Windows account: your password is your Windows password, managed by your IT team.",
        } + (me.Privileges.Count > 0 ? $" Privileges: {string.Join(", ", me.Privileges)}." : "");
        PasswordGroup.Visibility = TwoFactorGroup.Visibility = openholdAccount ? Visibility.Visible : Visibility.Collapsed;
        TwoFactorState.Text = me.TwoFactorEnabled
            ? "On. You enter a code from your authenticator app when you sign in. If you change phones, ask an administrator to reset it."
            : "Off. Turn it on so a stolen password alone isn't enough to sign in as you.";
        SetUpButton.Visibility = me.TwoFactorEnabled ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void ChangePassword_Click(object sender, RoutedEventArgs e)
    {
        if (NewPassword.Password != Repeat.Password)
        {
            Show("The two new passwords don't match.", error: true);
            return;
        }
        try
        {
            await App.Client.ChangePasswordAsync(Current.Password, NewPassword.Password);
            Current.Clear();
            NewPassword.Clear();
            Repeat.Clear();
            Show("Your password has been changed.", error: false);
        }
        catch (OpenholdApiException ex)
        {
            Show(ex.Message, error: true);
        }
    }

    private async void SetUp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var setup = await App.Client.BeginTwoFactorAsync();
            using var generator = new QRCoder.QRCodeGenerator();
            using var data = generator.CreateQrCode(setup.SetupUri, QRCoder.QRCodeGenerator.ECCLevel.M);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(new QRCoder.PngByteQRCode(data).GetGraphic(8));
            image.EndInit();
            QrCode.Source = image;
            SecretText.Text = string.Join(" ", setup.Secret.Chunk(4).Select(c => new string(c)));
            SetUpPanel.Visibility = Visibility.Visible;
            SetUpButton.Visibility = Visibility.Collapsed;
            Code.Focus();
        }
        catch (OpenholdApiException ex)
        {
            Show(ex.Message, error: true);
        }
    }

    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await App.Client.ConfirmTwoFactorAsync(Code.Text);
            SetUpPanel.Visibility = Visibility.Collapsed;
            Show("Two-factor sign-in is on. Next time you sign in, Openhold will ask for a code from the app.", error: false);
            await LoadAsync();
        }
        catch (OpenholdApiException ex)
        {
            Show(ex.Message, error: true);
        }
    }

    private void Show(string text, bool error)
    {
        Message.Text = text;
        Message.Foreground = (Brush)FindResource(error ? "DangerText" : "Brand");
        Message.Visibility = Visibility.Visible;
    }
}
