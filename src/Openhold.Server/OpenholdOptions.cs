namespace Openhold.Server;

public enum ServerRole
{
    /// <summary>Holds the primary database: issues locks, numbers and commits. Also serves its own site.</summary>
    Authority,

    /// <summary>Serves one office from a read replica and local chunk store; forwards writes to the authority.</summary>
    Site,

    /// <summary>
    /// Offsite copy only: a full chunk store and a standby database, ready to be promoted if the authority is
    /// lost. Nobody checks in or out here; browsing and getting files is allowed, e.g. to recover from it.
    /// </summary>
    Backup,
}

public sealed class OpenholdOptions
{
    public const string Section = "Openhold";

    public ServerRole Role { get; set; } = ServerRole.Authority;

    /// <summary>Short site code, e.g. "AU". Appears in numbering and audit records.</summary>
    public string SiteId { get; set; } = "AU";

    public string SiteName { get; set; } = "Australia";

    /// <summary>Directory of the content-addressed chunk store for this site.</summary>
    public string ChunkStorePath { get; set; } = "data/chunks";

    /// <summary>
    /// "Dev" trusts the X-Openhold-User header (local development and tests only).
    /// "Production" (or its synonym "Windows") uses the sign-in methods switched on in <see cref="Authentication"/>.
    /// </summary>
    public string AuthMode { get; set; } = "Dev";

    /// <summary>
    /// Usernames that are always administrators of every vault on this server, whatever the vault's own
    /// configuration says: how the first administrator gets in, and a way back in if a vault's groups go wrong.
    /// </summary>
    public List<string> Administrators { get; set; } = [];

    /// <summary>
    /// The address people's phones reach this server at, e.g. https://openhold.example.com, used in the QR codes
    /// stamped on released PDFs. Defaults to the address the job worker used.
    /// </summary>
    public string? PublicUrl { get; set; }

    public AuthenticationOptions Authentication { get; set; } = new();

    /// <summary>Where the server writes its daily log files (included in backups).</summary>
    public string LogFolder { get; set; } = "data/logs";

    /// <summary>How many days of log files to keep.</summary>
    public int LogRetentionDays { get; set; } = 90;

    /// <summary>
    /// The ERPs released catalogue items are sent to, e.g. one per company. Secrets (client secrets, API keys) belong in
    /// environment variables such as <c>Openhold__ErpConnections__0__Settings__ClientSecret</c>, not in this file.
    /// </summary>
    public List<ErpConnectionOptions> ErpConnections { get; set; } = [];

    public ReplicationOptions Replication { get; set; } = new();
}

/// <summary>
/// How this server copies file contents from the other sites. Every server registers its <see cref="OpenholdOptions.PublicUrl"/>
/// with the authority, so servers find each other without a list here; each one fetches what it lacks from
/// whichever peers have it.
/// </summary>
public sealed class ReplicationOptions
{
    /// <summary>
    /// The Openhold account this server signs in to its peers with (in the "site-servers" group). Its password belongs
    /// in the environment (<c>Openhold__Replication__Password</c>). Empty: sign in as the service's Windows account.
    /// </summary>
    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>How many files' contents are fetched at once.</summary>
    public int Parallel { get; set; } = 4;

    /// <summary>How often to look for contents that haven't arrived yet.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>One ERP that receives released items, e.g. simPRO for one company, or a CSV file another system imports.</summary>
public sealed class ErpConnectionOptions
{
    /// <summary>A short id that never changes, e.g. "main-erp"; links between items and ERP records are keyed on it.</summary>
    public string Id { get; set; } = "";

    /// <summary>What people see, e.g. "simPRO (Company A)".</summary>
    public string Name { get; set; } = "";

    /// <summary>The connector: "csv" or "simpro".</summary>
    public string Kind { get; set; } = "";

    /// <summary>Switched off: nothing is sent, but the connection and what it sent are kept.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The vault whose items go to this ERP; the first (default) vault when empty.</summary>
    public string? Vault { get; set; }

    /// <summary>Connector settings, e.g. Path for CSV; BaseUrl, CompanyId, ClientId and ClientSecret for simPRO.</summary>
    public Dictionary<string, string> Settings { get; set; } = [];
}

/// <summary>
/// How people sign in. Windows, organisation accounts (OpenID Connect) and Openhold's own accounts can all be on at
/// once; each person uses whichever their administrator set them up with.
/// </summary>
public sealed class AuthenticationOptions
{
    /// <summary>Windows sign-in (Kerberos/NTLM) for people on the organisation's domain. No password prompt.</summary>
    public bool Windows { get; set; } = true;

    /// <summary>Openhold accounts: username and password (plus two-factor codes), managed in Administration.</summary>
    public bool LocalAccounts { get; set; } = true;

    /// <summary>
    /// Domains whose people are known by their plain username ("jsmith" rather than "jsmith@partner.com"), e.g. the
    /// organisation's Windows domain ("CORP"), its DNS name and its email domain. People from any other domain keep
    /// the domain in their username, so two people called jsmith in different companies never become one account.
    /// Empty means every domain is stripped (a single-organisation installation).
    /// </summary>
    public List<string> HomeDomains { get; set; } = [];

    /// <summary>Organisation identity providers (e.g. Microsoft Entra ID, Okta, Google) whose sign-ins Openhold accepts.</summary>
    public List<OpenIdConnectProvider> OpenIdConnect { get; set; } = [];

    /// <summary>
    /// Where the authority keeps the private key that signs access tokens. Protect this file like a password: back it
    /// up with the server's settings, readable only by the service account.
    /// </summary>
    public string SigningKeyPath { get; set; } = "data/token-signing-key.pem";

    /// <summary>How long an access token lasts. Short, so disabling someone or signing them out takes effect quickly.</summary>
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How long a sign-in lasts without being used before the person must sign in again.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromDays(14);

    /// <summary>Wrong passwords or codes in a row before an account is locked, and for how long.</summary>
    public int LockoutAttempts { get; set; } = 5;

    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Sign-in attempts allowed per minute from one network address, to slow password guessing.</summary>
    public int SignInAttemptsPerMinute { get; set; } = 20;
}

/// <summary>An organisation identity provider. Clients sign in through the browser and send its tokens to Openhold.</summary>
public sealed class OpenIdConnectProvider
{
    /// <summary>What people see, e.g. "Partner company (Microsoft 365)".</summary>
    public string Name { get; set; } = "";

    /// <summary>The provider's issuer, e.g. https://login.microsoftonline.com/&lt;tenant&gt;/v2.0.</summary>
    public string Authority { get; set; } = "";

    /// <summary>The application (client) id registered with the provider for Openhold's desktop clients.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>The audience Openhold's tokens carry; usually the client id or an API identifier.</summary>
    public string? Audience { get; set; }

    /// <summary>Scopes the client asks for; an API scope makes the provider issue a token meant for Openhold.</summary>
    public List<string> Scopes { get; set; } = ["openid", "profile", "email", "offline_access"];

    /// <summary>The token claim that holds the username, e.g. preferred_username or upn.</summary>
    public string UsernameClaim { get; set; } = "preferred_username";
}
