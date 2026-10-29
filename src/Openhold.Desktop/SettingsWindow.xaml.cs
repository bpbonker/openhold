using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Openhold.Client.Sdk;

namespace Openhold.Desktop;

/// <summary>
/// Connection setup: which server this PC uses and where the workspace lives. Shown automatically the
/// first time Openhold runs on a PC, and from the toolbar afterwards.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly ClientSettings _settings;

    public SettingsWindow(ClientSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        ServerBox.Text = settings.ServerUrl;
        WorkspaceBox.Text = settings.WorkspaceRoot;
        VaultBox.Text = settings.Vault ?? "";
        SignInOpenhold.IsChecked = settings.UsesOpenholdAccount;
        SignInWindows.IsChecked = !settings.UsesOpenholdAccount;
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (ServerAddress() is not { } address)
            return;
        TestButton.IsEnabled = false;
        TestResult.Foreground = (Brush)FindResource("Subtle");
        TestResult.Text = "Connecting…";
        try
        {
            var server = new ClientSettings { ServerUrl = address.ToString() }.CreateSignInClient();
            var health = await server.GetHealthAsync();
            var vaults = await server.ListVaultsAsync();
            var chosen = VaultBox.Text;
            VaultBox.ItemsSource = vaults;
            VaultBox.Text = vaults.FirstOrDefault(v => string.Equals(v, chosen, StringComparison.OrdinalIgnoreCase)) ?? vaults.FirstOrDefault() ?? "";
            TestResult.Foreground = (Brush)FindResource("SuccessText");
            var signIn = await server.GetSignInOptionsAsync();
            var ways = new List<string>();
            if (signIn.Windows)
                ways.Add("Windows account");
            if (signIn.LocalAccounts)
                ways.Add("Openhold account");
            ways.AddRange(signIn.Providers.Select(p => p.Name));
            TestResult.Text = $"Connected to {health.SiteName ?? health.Site} ({health.Site}). Vaults: {string.Join(", ", vaults)}. Sign in with: {string.Join(", ", ways)}.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OpenholdApiException)
        {
            TestResult.Foreground = (Brush)FindResource("DangerText");
            TestResult.Text = $"Couldn't reach an Openhold server at {address}. Check the address and your network connection.";
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the Openhold workspace folder", InitialDirectory = Directory.Exists(WorkspaceBox.Text) ? WorkspaceBox.Text : "" };
        if (dialog.ShowDialog(this) == true)
            WorkspaceBox.Text = dialog.FolderName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (ServerAddress() is not { } address)
            return;
        var workspace = WorkspaceBox.Text.Trim();
        if (workspace.Length == 0 || !Path.IsPathFullyQualified(workspace))
        {
            Warn("Choose a full folder path for the workspace, for example C:\\Openhold\\Workspace.");
            return;
        }

        _settings.ServerUrl = address.ToString();
        _settings.WorkspaceRoot = workspace;
        _settings.Vault = string.IsNullOrWhiteSpace(VaultBox.Text) ? null : VaultBox.Text.Trim();
        _settings.SignIn = SignInOpenhold.IsChecked == true ? "Openhold" : "Windows";
        Directory.CreateDirectory(workspace);
        _settings.Save();
        DialogResult = true;
    }

    /// <summary>The server address as entered, completed with http:// and a trailing slash, or null with a message.</summary>
    private Uri? ServerAddress()
    {
        var text = ServerBox.Text.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "http://" + text;
        if (!text.EndsWith('/'))
            text += "/";
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0)
            return uri;
        Warn("Enter the server address, for example openhold-server:5080.");
        return null;
    }

    private void Warn(string message) =>
        MessageBox.Show(this, message, "Openhold", MessageBoxButton.OK, MessageBoxImage.Information);
}
