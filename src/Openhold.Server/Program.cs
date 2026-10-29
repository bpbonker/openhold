using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using Openhold.Core.Contracts;
using Openhold.Server;
using Openhold.Server.Api;
using Openhold.Server.Browsing;
using Openhold.Server.CheckIn;
using Openhold.Server.Configuration;
using Openhold.Server.Data;
using Openhold.Server.Files;
using Openhold.Server.Identity;
using Openhold.Server.Lifecycles;
using Openhold.Server.Locking;
using Openhold.Server.Numbering;

// The container image's health check: is the server on this machine ready? (The image has no curl.)
if (args is ["--health-check"])
{
    var port = (Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") ?? "8080").Split(';', ',')[0];
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    try
    {
        Environment.Exit((await http.GetAsync($"http://127.0.0.1:{port}/health/ready")).IsSuccessStatusCode ? 0 : 1);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        Environment.Exit(1);
    }
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // As a Windows service the working folder is System32; settings and relative paths are found next to the program.
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
});
// Runs as a Windows service when started by the service manager (see tools\install-server.ps1); otherwise as a console app.
builder.Host.UseWindowsService(options => options.ServiceName = "Openhold Server");

builder.Services.Configure<OpenholdOptions>(builder.Configuration.GetSection(OpenholdOptions.Section));
var openhold = builder.Configuration.GetSection(OpenholdOptions.Section).Get<OpenholdOptions>() ?? new OpenholdOptions();
SecurityGuards.Validate(openhold, builder.Environment);
// Daily log files beside the server (kept LogRetentionDays, included in backups); as a Windows service the
// Windows event log gets warnings and errors too.
builder.Logging.AddProvider(new Openhold.Server.Logging.FileLoggerProvider(
    Path.Combine(builder.Environment.ContentRootPath, openhold.LogFolder), openhold.LogRetentionDays));

// Every API call needs an authenticated user: Windows sign-in, an Openhold account or an organisation account (see
// AuthenticationSetup). The header scheme exists only for local development and tests.
var devAuth = openhold.AuthMode.Equals("Dev", StringComparison.OrdinalIgnoreCase);
builder.AddOpenholdAuthentication(openhold, devAuth);
builder.Services.AddAuthorization(options =>
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

// Each request works against one vault (its own database and file store); see VaultRegistry.
var vaults = VaultRegistry.FromConfiguration(builder.Configuration, openhold);
builder.Services.AddSingleton(vaults);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped(sp => vaults.For(sp.GetRequiredService<IHttpContextAccessor>().HttpContext!));
builder.Services.AddScoped(sp => sp.GetRequiredService<VaultInstance>().Db);
builder.Services.AddScoped(sp => sp.GetRequiredService<VaultInstance>().Chunks);

builder.Services.AddScoped<ActorResolver>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SigningKeys>();
builder.Services.AddSingleton<AccountService>();
// Sign-in attempts per address, on top of each account's lockout, to slow down password guessing.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(AuthApi.SignInLimit, context => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = openhold.Authentication.SignInAttemptsPerMinute, Window = TimeSpan.FromMinutes(1) }));
});
builder.Services.AddScoped<LockService>();
builder.Services.AddScoped<CheckInService>();
builder.Services.AddScoped<NumberingService>();
builder.Services.AddScoped<VaultBrowser>();
builder.Services.AddScoped<VaultSearch>();
builder.Services.AddScoped<FolderGuard>();
builder.Services.AddScoped<Openhold.Server.Replication.SyncingStatus>();
builder.Services.AddSingleton<Openhold.Server.Replication.PeerHandlerFactory>();
// Each background loop retries on its own; a bug that escapes one must never take the lock authority down with it.
builder.Services.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
builder.Services.AddSingleton<Openhold.Server.Replication.ContentReplicator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Openhold.Server.Replication.ContentReplicator>());
builder.Services.AddScoped<Openhold.Server.Items.ItemService>();
builder.Services.AddHttpClient("erp", c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddSingleton<Openhold.Server.Erp.IErpConnector, Openhold.Server.Erp.CsvErpConnector>();
builder.Services.AddSingleton<Openhold.Server.Erp.IErpConnector, Openhold.Server.Erp.SimproErpConnector>();
builder.Services.AddSingleton<Openhold.Server.Identity.SettingsSecrets>();
builder.Services.AddSingleton<Openhold.Server.Erp.ErpConnections>();
builder.Services.AddSingleton<Openhold.Server.Erp.ErpSyncService>();
builder.Services.AddScoped<FileManagementService>();
builder.Services.AddScoped<ConfigurationService>();
builder.Services.AddScoped<LifecycleService>();
builder.Services.AddScoped<Openhold.Server.Audit.AuditService>();
builder.Services.AddScoped<Openhold.Server.Jobs.JobService>();
builder.Services.AddSingleton<RequireWritableServer>();
if (openhold.Role == ServerRole.Authority)
{
    builder.Services.AddHostedService<ReservationExpiryService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<Openhold.Server.Erp.ErpSyncService>());
}

builder.Services.AddProblemDetails();
builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(o =>
    o.Limits.MaxRequestBodySize = 64 * 1024 * 1024);

if (!builder.Environment.IsDevelopment())
    builder.Services.AddHsts(options => options.MaxAge = TimeSpan.FromDays(365));

var app = builder.Build();

// AllowStatusCode404Response: since .NET 8 the handler otherwise discards any 404 it writes, which would turn
// Openhold's "not found" explanations (no such folder, no such vault) into bare errors.
app.UseExceptionHandler(new ExceptionHandlerOptions { AllowStatusCode404Response = true, ExceptionHandler = async context =>
{
    // Written directly as JSON: the problem-details service skips writing when a client sends no Accept
    // header, and every Openhold client wants the explanation regardless.
    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    // A site whose link to the authority is down: say so plainly. Browsing and Get still work from the local copy.
    if (error is Npgsql.NpgsqlException { IsTransient: true } or TimeoutException or System.Net.Sockets.SocketException
        && openhold.Role != ServerRole.Authority)
    {
        error = new OpenholdException(StatusCodes.Status503ServiceUnavailable, "authority_unreachable",
            "The lock server can't be reached from this site right now, so check-out, check-in and other changes have to wait. " +
            "You can still browse and get files. Try again in a few minutes.");
    }
    var problem = error is OpenholdException openholdError
        ? new ProblemDetails
        {
            Status = openholdError.StatusCode,
            Title = openholdError.Message,
            Type = openholdError.Code,
            Extensions = { ["code"] = openholdError.Code, ["details"] = openholdError.Details },
        }
        : new ProblemDetails { Status = 500, Title = "Unexpected server error.", Type = "internal_error", Extensions = { ["code"] = "internal_error" } };
    context.Response.StatusCode = problem.Status!.Value;
    await context.Response.WriteAsJsonAsync(problem, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json");
} });

// Choose the vault before routing, so /vaults/<name>/api/... reaches the normal endpoints.
app.Use((context, next) => vaults.SelectVaultFromPath(context, next));
app.UseRouting();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    // Health checks stay answerable over plain HTTP for load balancers and the container's own check.
    app.UseWhen(context => !context.Request.Path.StartsWithSegments("/health"), branch => branch.UseHttpsRedirection());
}
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.CacheControl = "no-store";
    await next();
});
app.UseAuthentication();
app.UseMiddleware<AccountGate>();
app.UseRateLimiter();
app.UseAuthorization();

if (openhold.Role == ServerRole.Authority)
{
    foreach (var vault in vaults.Vaults)
    {
        await MigrationRunner.MigrateAsync(vault.Db.Primary, app.Logger);
        await ConfigurationService.InitializeAsync(vault.Db.Primary);
        await using var connection = await vault.Db.Primary.OpenConnectionAsync();
        await connection.ExecuteAsync("""
            INSERT INTO sites (id, name, is_authority) VALUES (@SiteId, @SiteName, true)
            ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name, is_authority = true
            """, openhold);
    }
    // The authority signs Openhold-account access tokens; the key file sits with the server's settings.
    var signingKey = Path.Combine(app.Environment.ContentRootPath, openhold.Authentication.SigningKeyPath);
    await app.Services.GetRequiredService<SigningKeys>().InitializeAuthorityAsync(signingKey);
    await app.Services.GetRequiredService<AccountService>().CreateFirstAdministratorsAsync(Path.GetDirectoryName(signingKey)!, app.Logger);
}

app.MapGet("/health", (IOptions<OpenholdOptions> options) => new ServerHealth("ok", options.Value.SiteId, options.Value.SiteName)).AllowAnonymous();
// For monitoring and load balancers. 503 when a vault's own database doesn't answer (nothing can be served); a site
// cut off from the authority still serves browsing and Get, so it stays ready and says so. Names vaults, never why.
app.MapGet("/health/ready", async (IOptions<OpenholdOptions> options, Openhold.Server.Data.VaultRegistry vaults, CancellationToken ct) =>
{
    async Task<bool> AnswersAsync(Npgsql.NpgsqlDataSource db)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await using var connection = await db.OpenConnectionAsync(timeout.Token);
            await using var command = new Npgsql.NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or OperationCanceledException or TimeoutException or System.Net.Sockets.SocketException)
        {
            return false;
        }
    }
    List<string> down = [], cutOff = [];
    foreach (var vault in vaults.Vaults)
    {
        if (!await AnswersAsync(vault.Db.Local))
            down.Add(vault.Name);
        else if (!ReferenceEquals(vault.Db.Primary, vault.Db.Local) && !await AnswersAsync(vault.Db.Primary))
            cutOff.Add(vault.Name);
    }
    var status = down.Count > 0 ? $"database unreachable: {string.Join(", ", down)}"
        : cutOff.Count > 0 ? $"authority unreachable: {string.Join(", ", cutOff)}" : "ok";
    var health = new ServerHealth(status, options.Value.SiteId, options.Value.SiteName);
    return down.Count == 0 ? Results.Ok(health) : Results.Json(health, statusCode: StatusCodes.Status503ServiceUnavailable);
}).AllowAnonymous();
app.MapVaultApi();
app.MapItemApi();
app.MapAuthApi();

app.Run();

public partial class Program;
