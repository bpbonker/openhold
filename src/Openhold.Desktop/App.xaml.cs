using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Openhold.Client.Sdk;
using Openhold.Client.Sdk.Workspace;

namespace Openhold.Desktop;

public partial class App : Application
{
    public static ClientSettings Settings { get; private set; } = null!;

    public static OpenholdClient Client { get; private set; } = null!;

    public static OpenholdWorkspace Workspace { get; private set; } = null!;

    /// <summary>The vault's categories, lifecycles and revision schemes; refreshed with the file list.</summary>
    public static Openhold.Core.Configuration.VaultConfiguration Configuration { get; set; } = Openhold.Core.Configuration.VaultConfiguration.Empty;

    /// <summary>
    /// Set by tools that draw windows off-screen (the UI preview): windows close without asking about unsaved changes,
    /// because nobody is there to answer.
    /// </summary>
    public static bool Unattended { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        UseContrastColours(SystemParameters.HighContrast);
        SystemParameters.StaticPropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SystemParameters.HighContrast))
                UseContrastColours(SystemParameters.HighContrast);
        };

        if (!ClientSettings.Exists())
        {
            // First run on this PC: ask where the server is before anything tries to connect.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (new SettingsWindow(new ClientSettings()).ShowDialog() != true)
            {
                Shutdown();
                return;
            }
            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }

        Settings = ClientSettings.Load();
        // An Openhold account signs in once here; AutoCAD and Inventor then use the saved session.
        if (Settings.UsesOpenholdAccount && SessionStore.Load(Settings.ServerUri) is null)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (new SignInWindow(Settings).ShowDialog() != true)
            {
                Shutdown();
                return;
            }
            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }
        Client = Settings.CreateClient();
        Workspace = new OpenholdWorkspace(Settings.WorkspaceRoot, Client);

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    // ---- High contrast ----

    /// <summary>Each colour token in App.xaml and the Windows colour it becomes under a high-contrast theme.</summary>
    private static readonly Dictionary<string, Func<Brush>> ContrastColours = new()
    {
        ["Brand"] = () => SystemColors.HighlightBrush,
        ["BrandHover"] = () => SystemColors.HighlightBrush,
        ["BrandPressed"] = () => SystemColors.HighlightBrush,
        ["OnBrand"] = () => SystemColors.HighlightTextBrush,
        ["Surface"] = () => SystemColors.WindowBrush,
        ["SurfaceSubtle"] = () => SystemColors.WindowBrush,
        ["Chrome"] = () => SystemColors.WindowBrush,
        ["SurfaceStripe"] = () => SystemColors.WindowBrush,
        ["TextPrimary"] = () => SystemColors.WindowTextBrush,
        ["Subtle"] = () => SystemColors.WindowTextBrush,
        ["TextDisabled"] = () => SystemColors.GrayTextBrush,
        ["IconMuted"] = () => SystemColors.GrayTextBrush,
        ["FolderIcon"] = () => SystemColors.WindowTextBrush,
        ["Divider"] = () => SystemColors.WindowTextBrush,
        ["ControlBorder"] = () => SystemColors.WindowTextBrush,
        ["ControlBorderStrong"] = () => SystemColors.WindowTextBrush,
        ["PopupBorder"] = () => SystemColors.WindowTextBrush,
        ["FocusRing"] = () => SystemColors.WindowTextBrush,
        ["HoverBg"] = () => SystemColors.HighlightBrush,
        ["PressedBg"] = () => SystemColors.HighlightBrush,
        ["HoverText"] = () => SystemColors.HighlightTextBrush,
        ["SelectionBg"] = () => SystemColors.HighlightBrush,
        ["SelectionInactiveBg"] = () => SystemColors.HighlightBrush,
        ["SelectionText"] = () => SystemColors.HighlightTextBrush,
        ["CellFocus"] = () => SystemColors.WindowTextBrush,
        ["ButtonBg"] = () => SystemColors.ControlBrush,
        ["ButtonHoverBg"] = () => SystemColors.HighlightBrush,
        ["ButtonPressedBg"] = () => SystemColors.HighlightBrush,
        ["ButtonText"] = () => SystemColors.ControlTextBrush,
        ["ButtonBorder"] = () => SystemColors.ControlTextBrush,
        ["InputBg"] = () => SystemColors.WindowBrush,
        ["InputReadOnlyBg"] = () => SystemColors.WindowBrush,
        ["DisabledBg"] = () => SystemColors.WindowBrush,
        ["ScrollThumb"] = () => SystemColors.GrayTextBrush,
        ["ScrollThumbHover"] = () => SystemColors.WindowTextBrush,
        ["SuccessText"] = () => SystemColors.WindowTextBrush,
        ["SuccessBg"] = () => SystemColors.WindowBrush,
        ["SuccessBorder"] = () => SystemColors.WindowTextBrush,
        ["WarningText"] = () => SystemColors.WindowTextBrush,
        ["WarningBg"] = () => SystemColors.WindowBrush,
        ["WarningBorder"] = () => SystemColors.WindowTextBrush,
        ["DangerText"] = () => SystemColors.WindowTextBrush,
        ["DangerBg"] = () => SystemColors.WindowBrush,
        ["DangerBorder"] = () => SystemColors.WindowTextBrush,
        ["InfoText"] = () => SystemColors.WindowTextBrush,
        ["InfoBg"] = () => SystemColors.WindowBrush,
        ["InfoBorder"] = () => SystemColors.WindowTextBrush,
        ["NeutralBg"] = () => SystemColors.WindowBrush,
        ["NeutralText"] = () => SystemColors.WindowTextBrush,
    };

    private Dictionary<string, object>? _designColours;

    /// <summary>
    /// Under a Windows high-contrast theme, Openhold's colours give way to the theme's own, so every window follows it.
    /// Styles use the tokens through DynamicResource, so switching the theme while Openhold is open takes effect at once.
    /// </summary>
    internal void UseContrastColours(bool highContrast)
    {
        _designColours ??= ContrastColours.Keys.Where(Resources.Contains).ToDictionary(key => key, key => Resources[key]);
        foreach (var (key, systemColour) in ContrastColours)
            Resources[key] = highContrast ? systemColour() : _designColours[key];
    }

    /// <summary>Never crash on an unexpected error: say what happened and keep the app usable.</summary>
    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (e.Exception is SignInRequiredException signIn)
        {
            // The session ended (expired, signed out elsewhere, or disabled): sign in again and carry on.
            if (new SignInWindow(Settings, signIn.Message) { Owner = Current.MainWindow }.ShowDialog() == true)
                MessageBox.Show(Current.MainWindow!, "You're signed in again. Try that again.", "Openhold", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var message = e.Exception is System.Net.Http.HttpRequestException
            ? $"Couldn't reach the Openhold server at {Settings.ServerUrl}. Nothing was changed. Check your network and try again."
            : $"Something went wrong and nothing was changed:\n\n{e.Exception.Message}";
        MessageBox.Show(Current.MainWindow!, message, "Openhold", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
