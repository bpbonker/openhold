using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Openhold.Client.Sdk;
using Openhold.Core.Contracts;
using Testcontainers.PostgreSql;

namespace Openhold.Server.Tests;

/// <summary>
/// One Postgres container and one in-process authority server shared by all tests. The server hosts two
/// vaults, Production (the default) and Sandbox, each with its own database and file store.
/// </summary>
public sealed class VaultFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly string _chunkRoot = Path.Combine(Path.GetTempPath(), "openhold-server-tests", Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<Program>? _factory;

    /// <summary>A user the server treats as an administrator of every vault (Openhold:Administrators).</summary>
    public const string Administrator = "admin";

    /// <summary>The file the test CSV ERP connection keeps released items in.</summary>
    public string ErpCsvPath => Path.Combine(_chunkRoot, "erp", "items.csv");

    /// <summary>The Production (default) vault's database.</summary>
    public string ConnectionString => _postgres.GetConnectionString();

    private string SandboxConnectionString => new Npgsql.NpgsqlConnectionStringBuilder(ConnectionString) { Database = "openhold_sandbox" }.ConnectionString;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using (var connection = new Npgsql.NpgsqlConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var create = new Npgsql.NpgsqlCommand("CREATE DATABASE openhold_sandbox", connection);
            await create.ExecuteNonQueryAsync();
        }

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            ConfigureVaults(builder);
            builder.UseSetting("Openhold:SiteId", "AU");
            builder.UseSetting("Openhold:SiteName", "Australia");
            builder.UseSetting("Openhold:Administrators:0", Administrator);
            builder.UseSetting("Openhold:Authentication:SigningKeyPath", Path.Combine(_chunkRoot, "token-signing-key.pem"));
            builder.UseSetting("Openhold:LogFolder", Path.Combine(_chunkRoot, "logs"));
            builder.UseSetting("Openhold:Authentication:SignInAttemptsPerMinute", "10000"); // every test signs in from one address
            builder.UseSetting("Openhold:PublicUrl", AuthorityUrl);
            ConfigureReplication(builder);
            builder.UseSetting("Openhold:ErpConnections:0:Id", "test-csv");
            builder.UseSetting("Openhold:ErpConnections:0:Name", "Test CSV");
            builder.UseSetting("Openhold:ErpConnections:0:Kind", "csv");
            builder.UseSetting("Openhold:ErpConnections:0:Settings:Path", ErpCsvPath);
            // Refused before any network call (simPRO must use https), to exercise failures and retries.
            builder.UseSetting("Openhold:ErpConnections:1:Id", "broken-simpro");
            builder.UseSetting("Openhold:ErpConnections:1:Name", "Broken simPRO");
            builder.UseSetting("Openhold:ErpConnections:1:Kind", "simpro");
            builder.UseSetting("Openhold:ErpConnections:1:Settings:BaseUrl", "http://simpro.invalid");
            // An ERP that stops answering: its calls end in HttpClient's timeout (a TaskCanceledException).
            builder.UseSetting("Openhold:ErpConnections:2:Id", "stalled-erp");
            builder.UseSetting("Openhold:ErpConnections:2:Name", "Stalled ERP");
            builder.UseSetting("Openhold:ErpConnections:2:Kind", StalledErp.KindName);
            builder.ConfigureTestServices(services => services.AddSingleton<Openhold.Server.Erp.IErpConnector, StalledErp>());
        });
        _ = _factory.Server; // start the host so migrations run once, up front
        await SetUpNumberingAsync();
    }

    /// <summary>The scheme the tests' catalogue numbers items by (P-100000-x).</summary>
    public const string CatalogueScheme = "test-parts";

    /// <summary>The scheme of the tests' manual "buyout" library category (BO-00001).</summary>
    public const string BuyoutScheme = "test-buyout";

    /// <summary>
    /// Openhold starts with no numbering, so the tests' vault is set up the way an administrator would: a catalogue
    /// scheme, and a manual library category for bought-out parts with its own scheme.
    /// </summary>
    private async Task SetUpNumberingAsync()
    {
        var admin = Client(Administrator);
        await admin.SaveNumberingSchemeAsync(new NumberingScheme(CatalogueScheme, "Part numbers", "P-{seq:000000}-{check}", false, TimeSpan.FromHours(24), true) { StartAt = 100000 });
        await admin.SaveNumberingSchemeAsync(new NumberingScheme(BuyoutScheme, "Buyout", "BO-{seq:00000}", false, TimeSpan.FromHours(24), true));
        var current = await admin.GetConfigurationAsync();
        var categories = current.Configuration.Categories.ToList();
        categories.Insert(1, new Openhold.Core.Configuration.CategoryDefinition("buyout", "Buyout", LifecycleId: "library", NumberingSchemeId: BuyoutScheme,
            Manual: true, Library: true, Properties: ["Description", "Manufacturer", "Manufacturer Part Number"]));
        await admin.SaveConfigurationAsync(new SaveConfigurationRequest(current.Version,
            current.Configuration with { CatalogueNumberingSchemeId = CatalogueScheme, Categories = categories }, "Numbering for the tests"));
    }

    private void ConfigureVaults(IWebHostBuilder builder)
    {
        builder.UseSetting("Openhold:Vaults:0:Name", "Production");
        builder.UseSetting("Openhold:Vaults:0:Primary", ConnectionString);
        builder.UseSetting("Openhold:Vaults:0:ChunkStorePath", Path.Combine(_chunkRoot, "production"));
        builder.UseSetting("Openhold:Vaults:1:Name", "Sandbox");
        builder.UseSetting("Openhold:Vaults:1:Primary", SandboxConnectionString);
        builder.UseSetting("Openhold:Vaults:1:ChunkStorePath", Path.Combine(_chunkRoot, "sandbox"));
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
            await _factory.DisposeAsync();
        if (_backup is not null)
            await _backup.DisposeAsync();
        if (_site is not null)
            await _site.DisposeAsync();
        if (_cutOff is not null)
            await _cutOff.DisposeAsync();
        await _postgres.DisposeAsync();
        if (Directory.Exists(_chunkRoot))
            Directory.Delete(_chunkRoot, recursive: true);
    }

    /// <summary>A client of the Production vault, or of <paramref name="vault"/> when given.</summary>
    public OpenholdClient Client(string username, string machine = "PC1", string? vault = null)
    {
        var http = _factory!.CreateClient();
        if (vault is not null)
            http.BaseAddress = new Uri(http.BaseAddress!, $"vaults/{vault}/");
        return new OpenholdClient(http, username, machine);
    }

    /// <summary>A Production client whose requests pass through <paramref name="handler"/>, e.g. to drop a response.</summary>
    public OpenholdClient Client(string username, DelegatingHandler handler) =>
        new(_factory!.CreateDefaultClient(handler), username, "PC1");

    /// <summary>An HTTP client that presents no identity.</summary>
    public HttpClient AnonymousHttpClient() => _factory!.CreateClient();

    private WebApplicationFactory<Program>? _backup;

    /// <summary>A client of a second server in the Backup role, reading the same vault.</summary>
    public OpenholdClient BackupClient(string username)
    {
        _backup ??= new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            ConfigureVaults(builder);
            builder.UseSetting("Openhold:Role", "Backup");
            builder.UseSetting("Openhold:Authentication:SigningKeyPath", Path.Combine(_chunkRoot, "token-signing-key.pem"));
            builder.UseSetting("Openhold:LogFolder", Path.Combine(_chunkRoot, "logs"));
            builder.UseSetting("Openhold:SiteName", "AU Offsite");
        });
        return new OpenholdClient(_backup.CreateClient(), username, "PC1");
    }

    public const string AuthorityUrl = "http://au.openhold.test";

    public const string SiteUrl = "http://in.openhold.test";

    private WebApplicationFactory<Program>? _site;

    /// <summary>
    /// A client of a second site's server ("IN", India) with its own file store. It shares the authority's database,
    /// standing in for the read replica a real site has. Both servers register with each other on first use.
    /// </summary>
    public async Task<OpenholdClient> SiteClientAsync(string username)
    {
        if (_site is null)
        {
            _site = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Openhold:Vaults:0:Name", "Production");
                builder.UseSetting("Openhold:Vaults:0:Primary", ConnectionString);
                builder.UseSetting("Openhold:Vaults:0:ChunkStorePath", Path.Combine(_chunkRoot, "site-in", "production"));
                builder.UseSetting("Openhold:Vaults:1:Name", "Sandbox");
                builder.UseSetting("Openhold:Vaults:1:Primary", SandboxConnectionString);
                builder.UseSetting("Openhold:Vaults:1:ChunkStorePath", Path.Combine(_chunkRoot, "site-in", "sandbox"));
                builder.UseSetting("Openhold:Role", "Site");
                builder.UseSetting("Openhold:SiteId", "IN");
                builder.UseSetting("Openhold:SiteName", "India");
                builder.UseSetting("Openhold:PublicUrl", SiteUrl);
                builder.UseSetting("Openhold:Administrators:0", Administrator);
                builder.UseSetting("Openhold:Authentication:SigningKeyPath", Path.Combine(_chunkRoot, "token-signing-key.pem"));
                builder.UseSetting("Openhold:LogFolder", Path.Combine(_chunkRoot, "logs-in"));
                ConfigureReplication(builder);
            });
            foreach (var server in new[] { _factory!, _site })
            {
                var replicator = server.Services.GetRequiredService<Openhold.Server.Replication.ContentReplicator>();
                foreach (var vault in server.Services.GetRequiredService<Openhold.Server.Data.VaultRegistry>().Vaults)
                    await replicator.RegisterAsync(vault, CancellationToken.None);
            }
        }
        return new OpenholdClient(_site.CreateClient(), username, "PC-IN");
    }

    private WebApplicationFactory<Program>? _cutOff;

    /// <summary>
    /// A client of a site server whose link to the authority is down: its local copy of the database works, but the
    /// authority's (where every change is written) can't be reached.
    /// </summary>
    public OpenholdClient CutOffSiteClient(string username)
    {
        _cutOff ??= new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Openhold:Vaults:0:Name", "Production");
            builder.UseSetting("Openhold:Vaults:0:Primary", "Host=127.0.0.1;Port=1;Database=openhold;Username=openhold;Password=openhold-dev;Timeout=2");
            builder.UseSetting("Openhold:Vaults:0:Local", ConnectionString);
            builder.UseSetting("Openhold:Vaults:0:ChunkStorePath", Path.Combine(_chunkRoot, "site-cut-off"));
            // Both vaults, so none is left over from the developer's appsettings (a Sandbox on their own database).
            builder.UseSetting("Openhold:Vaults:1:Name", "Sandbox");
            builder.UseSetting("Openhold:Vaults:1:Primary", "Host=127.0.0.1;Port=1;Database=openhold_sandbox;Username=openhold;Password=openhold-dev;Timeout=2");
            builder.UseSetting("Openhold:Vaults:1:Local", SandboxConnectionString);
            builder.UseSetting("Openhold:Vaults:1:ChunkStorePath", Path.Combine(_chunkRoot, "site-cut-off-sandbox"));
            builder.UseSetting("Openhold:Role", "Site");
            builder.UseSetting("Openhold:SiteId", "NZ");
            builder.UseSetting("Openhold:SiteName", "Cut off");
            builder.UseSetting("Openhold:Administrators:0", Administrator);
            builder.UseSetting("Openhold:Authentication:SigningKeyPath", Path.Combine(_chunkRoot, "token-signing-key.pem"));
            builder.UseSetting("Openhold:LogFolder", Path.Combine(_chunkRoot, "logs-cut-off"));
            ConfigureReplication(builder);
        });
        return new OpenholdClient(_cutOff.CreateClient(), username, "PC-NZ");
    }

    /// <summary>
    /// Servers reach each other in process; replication runs when a test asks (catch-up), not on a timer, so tests
    /// see exactly what they did.
    /// </summary>
    private void ConfigureReplication(IWebHostBuilder builder)
    {
        builder.UseSetting("Openhold:Replication:Username", Administrator);
        builder.UseSetting("Openhold:Replication:Interval", "01:00:00");
        builder.ConfigureTestServices(services => services.AddSingleton<Openhold.Server.Replication.PeerHandlerFactory>(new InProcessPeers(this)));
    }

    /// <summary>Fails every call the way HttpClient does when the ERP doesn't answer within the timeout.</summary>
    private sealed class StalledErp : Openhold.Server.Erp.IErpConnector
    {
        public const string KindName = "stalled";
        public string Kind => KindName;
        public ErpKindInfo Describe() => new(KindName, "Stalled (test)", "Never answers.", []);
        public Task<string> PushAsync(ErpConnectionOptions connection, ItemInfo item, string? externalId, CancellationToken cancellationToken) =>
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 60 seconds elapsing.", new TimeoutException());
        public Task TestAsync(ErpConnectionOptions connection, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class InProcessPeers(VaultFixture fixture) : Openhold.Server.Replication.PeerHandlerFactory
    {
        public override HttpMessageHandler Create(Uri baseUrl, bool useWindowsSignIn) => baseUrl.Host switch
        {
            "au.openhold.test" => fixture._factory!.Server.CreateHandler(),
            "in.openhold.test" => fixture._site!.Server.CreateHandler(),
            _ => throw new InvalidOperationException($"No test server at {baseUrl}."),
        };
    }

    /// <summary>A unique folder per test so tests never interfere.</summary>
    public static string UniqueFolder(string name) => $"$/Tests/{name}-{Guid.NewGuid():N}";

    public static byte[] Content(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>Adds new files to a folder in one changeset and returns their ids in order.</summary>
    public static async Task<Guid[]> AddFilesAsync(OpenholdClient client, string folderPath, params string[] names)
    {
        var folder = await client.EnsureFolderAsync(folderPath);
        var files = new List<CheckInFile>();
        for (var i = 0; i < names.Length; i++)
        {
            var manifest = await client.UploadContentAsync(new MemoryStream(Content(2000 + i, seed: names[i].GetHashCode())));
            files.Add(new CheckInFile(null, folder.Id, names[i], manifest, null, null));
        }
        var result = await client.CheckInAsync(new CheckInRequest(Guid.NewGuid(), "add", files));
        return names.Select(n => result.Versions.Single(v => v.Name == n).FileId).ToArray();
    }
}

[CollectionDefinition(Name)]
public sealed class VaultCollection : ICollectionFixture<VaultFixture>
{
    public const string Name = "vault";
}
