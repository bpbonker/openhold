using System.Text.Json;

namespace Openhold.Client.Sdk;

/// <summary>How title-block attributes map to vault properties. Company specifics belong in the deployed settings file.</summary>
public sealed class TitleBlockSettings
{
    /// <summary>Regex matched case-insensitively against block names.</summary>
    public string BlockNamePattern { get; set; } = "title";

    public string SheetTitleAttribute { get; set; } = "TITLE";

    public Dictionary<string, string> Attributes { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["REVISION_NUMBER"] = "Revision",
        ["PART_NUMBER"] = "Part Number",
        ["DESCRIPTION"] = "Description",
        ["Sheet_Size"] = "Sheet Size",
        ["AUTHOR"] = "Author",
        ["CHECKED_BY"] = "Checked By",
        ["ENG_APPROVED_BY"] = "Engineering Approved By",
    };
}

/// <summary>
/// Per-user client settings, stored in <c>%APPDATA%\Openhold\client.json</c> and normally deployed by IT.
/// Users always talk to their own office's server.
/// </summary>
public sealed class ClientSettings
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string ServerUrl { get; set; } = "http://localhost:5080/";

    public string WorkspaceRoot { get; set; } = @"C:\Openhold\Workspace";

    /// <summary>Which vault on the server to use (e.g. "Production" or "Sandbox"); empty means the server's default.</summary>
    public string? Vault { get; set; }

    /// <summary>
    /// How this PC signs in: "Windows" (the Windows account, no prompt) or "Openhold" (an Openhold account with a password
    /// and two-factor code, signed in once in Openhold Explorer and shared by the CAD add-ins).
    /// </summary>
    public string SignIn { get; set; } = "Windows";

    public bool UsesOpenholdAccount => string.Equals(SignIn, "Openhold", StringComparison.OrdinalIgnoreCase);

    /// <summary>The server's address, ending in a slash.</summary>
    public Uri ServerUri => new(ServerUrl.EndsWith('/') ? ServerUrl : ServerUrl + "/");

    public TitleBlockSettings TitleBlock { get; set; } = new();

    /// <summary>Property columns shown in Openhold Explorer's file list, in order.</summary>
    public List<string> PropertyColumns { get; set; } = ["Part Number", "Description", "Revision"];

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Openhold", "client.json");

    /// <summary>True once this PC has been set up (a settings file exists).</summary>
    public static bool Exists(string? path = null)
    {
        CarryOverPilotSettings(path);
        return File.Exists(path ?? DefaultPath);
    }

    /// <summary>Saves settings for every Openhold program on this PC (Explorer, AutoCAD, Inventor).</summary>
    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>Loads settings, writing a default file on first run so IT and users can see what to change.</summary>
    public static ClientSettings Load(string? path = null)
    {
        CarryOverPilotSettings(path);
        path ??= DefaultPath;
        if (File.Exists(path))
            return JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(path)) ?? new ClientSettings();

        var defaults = new ClientSettings();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(defaults, Json));
        return defaults;
    }

    /// <summary>
    /// Openhold's pilot ran under the working name Strata. A PC set up then keeps its settings: they are copied once
    /// into the Openhold folder, with the old sign-in name updated.
    /// </summary>
    private static void CarryOverPilotSettings(string? path)
    {
        var pilot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Strata", "client.json");
        if (path is not null || File.Exists(DefaultPath) || !File.Exists(pilot))
            return;

        var settings = JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(pilot)) ?? new ClientSettings();
        if (string.Equals(settings.SignIn, "Strata", StringComparison.OrdinalIgnoreCase))
            settings.SignIn = "Openhold";
        settings.Save();
    }

    /// <summary>
    /// A client for this PC's server and vault. With Windows sign-in it uses the current Windows user (Kerberos);
    /// with an Openhold account it uses the session saved by Openhold Explorer's sign-in, renewing it as needed (requests
    /// throw <see cref="SignInRequiredException"/> when there's no session). The user header is only honoured by
    /// development servers; production servers use the signed-in identity.
    /// </summary>
    public OpenholdClient CreateClient()
    {
        var server = ServerUri;
        HttpMessageHandler handler = UsesOpenholdAccount
            ? new SessionHandler(server) { InnerHandler = new HttpClientHandler() }
            : new HttpClientHandler { UseDefaultCredentials = true };
        var address = string.IsNullOrWhiteSpace(Vault) ? server : new Uri(server, $"vaults/{Uri.EscapeDataString(Vault)}/");
        var http = new HttpClient(handler) { BaseAddress = address, Timeout = TimeSpan.FromMinutes(10) };
        var username = UsesOpenholdAccount && SessionStore.Load(server) is { } session ? session.Username : Environment.UserName.ToLowerInvariant();
        return new OpenholdClient(http, username, Environment.MachineName);
    }

    /// <summary>A client that signs in nobody: for asking the server how to sign in, and for signing in.</summary>
    public OpenholdClient CreateSignInClient() =>
        new(new HttpClient(new HttpClientHandler()) { BaseAddress = ServerUri, Timeout = TimeSpan.FromMinutes(1) }, "", Environment.MachineName);
}
