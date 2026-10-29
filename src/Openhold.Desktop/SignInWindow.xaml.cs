using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Openhold.Client.Sdk;

namespace Openhold.Desktop;

/// <summary>
/// Signs in with an Openhold account, and walks a person through what their account needs first: changing a temporary
/// password, then setting up two-factor sign-in when it's required. The session is saved for every Openhold program on
/// this PC.
/// </summary>
public partial class SignInWindow : Window
{
    private enum Step { Credentials, ChangePassword, SetUpTwoFactor }

    private readonly ClientSettings _settings;
    private readonly OpenholdClient _client;
    private Step _step = Step.Credentials;

    public SignInWindow(ClientSettings settings, string? reason = null)
    {
        InitializeComponent();
        _settings = settings;
        _client = settings.CreateSignInClient();
        Server.Text = reason ?? $"Server: {settings.ServerUri.GetLeftPart(UriPartial.Authority)}";
        Username.Text = SessionStore.Load(settings.ServerUri)?.Username ?? "";
        Loaded += (_, _) => (Username.Text.Length > 0 ? (UIElement)Password : Username).Focus();
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        ShowProblem(null);
        IsEnabled = false;
        try
        {
            switch (_step)
            {
                case Step.Credentials:
                    await SignInAsync(CodePanel.IsVisible ? Code.Text : null);
                    break;
                case Step.ChangePassword:
                    if (NewPassword.Password != RepeatPassword.Password)
                    {
                        ShowProblem("The two new passwords don't match.");
                        return;
                    }
                    await _client.ChangePasswordAsync(Password.Password, NewPassword.Password);
                    Password.Password = NewPassword.Password;
                    await SignInAsync(null);
                    break;
                case Step.SetUpTwoFactor:
                    await _client.ConfirmTwoFactorAsync(SetupCode.Text);
                    await SignInAsync(SetupCode.Text);
                    break;
            }
        }
        catch (OpenholdApiException ex) when (ex.Code == "two_factor_code_needed")
        {
            Show(Step.Credentials);
            CodePanel.Visibility = Visibility.Visible;
            Code.Focus();
            ShowProblem(ex.Message);
        }
        catch (OpenholdApiException ex)
        {
            ShowProblem(ex.Message);
        }
        catch (System.Net.Http.HttpRequestException)
        {
            ShowProblem($"Couldn't reach the Openhold server at {_settings.ServerUri.GetLeftPart(UriPartial.Authority)}. Check your network and try again.");
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async Task SignInAsync(string? code)
    {
        var result = await _client.SignInAsync(Username.Text.Trim(), Password.Password, code);
        _client.UseAccessToken(result.AccessToken, result.Username);
        if (result.MustChangePassword)
        {
            Show(Step.ChangePassword);
            Heading.Text = "Choose your password";
            NewPassword.Focus();
            return;
        }
        if (result.MustSetUpTwoFactor)
        {
            var setup = await _client.BeginTwoFactorAsync();
            QrCode.Source = QrImage(setup.SetupUri);
            SecretText.Text = string.Join(" ", setup.Secret.Chunk(4).Select(c => new string(c)));
            Show(Step.SetUpTwoFactor);
            Heading.Text = "Set up two-factor sign-in";
            SetupCode.Focus();
            return;
        }
        SessionStore.Save(_settings.ServerUri, SessionStore.From(result));
        DialogResult = true;
    }

    private void Show(Step step)
    {
        _step = step;
        CredentialsStep.Visibility = step == Step.Credentials ? Visibility.Visible : Visibility.Collapsed;
        PasswordStep.Visibility = step == Step.ChangePassword ? Visibility.Visible : Visibility.Collapsed;
        TwoFactorStep.Visibility = step == Step.SetUpTwoFactor ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = step == Step.Credentials ? "_Sign In" : "_Continue";
    }

    private static BitmapImage QrImage(string text)
    {
        using var generator = new QRCoder.QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
        var png = new QRCoder.PngByteQRCode(data).GetGraphic(8);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void ShowProblem(string? text)
    {
        Problem.Text = text ?? "";
        Problem.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
