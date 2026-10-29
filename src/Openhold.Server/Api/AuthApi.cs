using Microsoft.Extensions.Options;
using Openhold.Core.Configuration;
using Openhold.Core.Contracts;
using Openhold.Server.Configuration;
using Openhold.Server.Identity;

namespace Openhold.Server.Api;

/// <summary>Sign-in, your own account, and (with the Manage Users privilege) everyone's accounts.</summary>
public static class AuthApi
{
    /// <summary>Rate limit for sign-in attempts from one address, on top of per-account lockout.</summary>
    public const string SignInLimit = "sign-in";

    public static void MapAuthApi(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth");
        var writes = auth.MapGroup("").AddEndpointFilter<RequireWritableServer>();

        auth.MapGet("/options", (IOptions<OpenholdOptions> options) =>
        {
            var settings = options.Value.Authentication;
            return new SignInOptions(settings.Windows, settings.LocalAccounts,
                settings.OpenIdConnect.Select(p => new SignInProvider(p.Name, p.Authority, p.ClientId, p.Scopes)).ToList());
        }).AllowAnonymous();

        writes.MapPost("/sign-in", (SignInRequest body, HttpContext http, AccountService accounts, CancellationToken ct) =>
            accounts.SignInAsync(body, http.Connection.RemoteIpAddress?.ToString(), ct)).AllowAnonymous().RequireRateLimiting(SignInLimit);
        writes.MapPost("/refresh", (RefreshRequest body, AccountService accounts, CancellationToken ct) =>
            accounts.RefreshAsync(body, ct)).AllowAnonymous().RequireRateLimiting(SignInLimit);
        writes.MapPost("/sign-out", async (SignOutRequest body, HttpContext http, AccountService accounts, CancellationToken ct) =>
        {
            await accounts.SignOutAsync(Username(http), body, ct);
            return Results.NoContent();
        });
        writes.MapPost("/password", async (ChangePasswordRequest body, HttpContext http, AccountService accounts, CancellationToken ct) =>
        {
            await accounts.ChangePasswordAsync(Username(http), body, ct);
            return Results.NoContent();
        }).RequireRateLimiting(SignInLimit);
        writes.MapPost("/two-factor/setup", (HttpContext http, AccountService accounts, CancellationToken ct) =>
            accounts.BeginTwoFactorAsync(Username(http), ct));
        writes.MapPost("/two-factor/confirm", async (TwoFactorConfirmRequest body, HttpContext http, AccountService accounts, CancellationToken ct) =>
        {
            await accounts.ConfirmTwoFactorAsync(Username(http), body, ct);
            return Results.NoContent();
        }).RequireRateLimiting(SignInLimit);

        // Who am I, and what may I do in this vault? For showing the right menus.
        auth.MapGet("/me", async (HttpContext http, AccountService accounts, ConfigurationService configuration, CancellationToken ct) =>
        {
            var username = Username(http);
            var account = await accounts.FindAsync(username, ct);
            var current = (await configuration.GetAsync(ct)).Configuration;
            return new WhoAmI(username, account?.DisplayName ?? username, account?.Kind ?? AuthenticationSetup.KindOf(http),
                current.PrivilegesOf(username), account?.TwoFactorEnabled == true);
        });

        // Audit log and access review, for auditors (View Audit) and account managers.
        app.MapGet("/api/admin/audit", async (DateTime? from, DateTime? to, string? username, string? action, int? limit,
            HttpContext http, ConfigurationService configuration, Openhold.Server.Audit.AuditService audit, CancellationToken ct) =>
        {
            await RequireAsync(http, configuration, ct, Privileges.ViewAudit);
            return await audit.ListAsync(from, to, username, action, limit ?? 1000, ct);
        });
        app.MapGet("/api/admin/access-review", async (HttpContext http, ConfigurationService configuration, Openhold.Server.Audit.AuditService audit, CancellationToken ct) =>
        {
            await RequireAsync(http, configuration, ct, Privileges.ViewAudit, Privileges.ManageUsers);
            return await audit.AccessReviewAsync(ct);
        });

        // Account administration, for people with the Manage Users privilege in the identity vault.
        var admin = app.MapGroup("/api/admin/accounts").AddEndpointFilter<RequireWritableServer>();
        admin.MapGet("", async (HttpContext http, AccountService accounts, CancellationToken ct) =>
        {
            await RequireManageUsersAsync(http, accounts, ct);
            return await accounts.ListAsync(ct);
        });
        admin.MapPost("", async (CreateAccountRequest body, HttpContext http, ActorResolver actors, AccountService accounts, CancellationToken ct) =>
            await accounts.CreateAsync(await RequireManageUsersAsync(http, accounts, ct, actors), body, ct));
        admin.MapPatch("/{username}", async (string username, UpdateAccountRequest body, HttpContext http, ActorResolver actors, AccountService accounts, CancellationToken ct) =>
            await accounts.UpdateAsync(await RequireManageUsersAsync(http, accounts, ct, actors), username, body, ct));
        admin.MapPost("/{username}/reset-password", async (string username, HttpContext http, ActorResolver actors, AccountService accounts, CancellationToken ct) =>
            await accounts.ResetPasswordAsync(await RequireManageUsersAsync(http, accounts, ct, actors), username, ct));
        admin.MapPost("/{username}/reset-two-factor", async (string username, HttpContext http, ActorResolver actors, AccountService accounts, CancellationToken ct) =>
            await accounts.ResetTwoFactorAsync(await RequireManageUsersAsync(http, accounts, ct, actors), username, ct));
        admin.MapPost("/{username}/unlock", async (string username, HttpContext http, ActorResolver actors, AccountService accounts, CancellationToken ct) =>
            await accounts.UnlockAsync(await RequireManageUsersAsync(http, accounts, ct, actors), username, ct));
        admin.MapPost("/{username}/end-sessions", async (string username, HttpContext http, ActorResolver actors, AccountService accounts, CancellationToken ct) =>
            await accounts.EndSessionsAsync(await RequireManageUsersAsync(http, accounts, ct, actors), username, ct));
    }

    private static string Username(HttpContext http) =>
        ActorResolver.NormalizeUsername(http.User.Identity?.Name) ?? throw new OpenholdException(StatusCodes.Status401Unauthorized, "unauthenticated", "Sign-in required.");

    /// <summary>Refuses unless the caller has one of the privileges in this vault.</summary>
    private static async Task RequireAsync(HttpContext http, ConfigurationService configuration, CancellationToken ct, params string[] privileges)
    {
        var current = (await configuration.GetAsync(ct)).Configuration;
        if (!privileges.Any(p => current.HasPrivilege(Username(http), p)))
            throw new OpenholdException(StatusCodes.Status403Forbidden, "not_allowed", $"This needs the {string.Join(" or ", privileges)} privilege.");
    }

    private static async Task<Actor> RequireManageUsersAsync(HttpContext http, AccountService accounts, CancellationToken ct, ActorResolver? actors = null)
    {
        var configuration = await accounts.IdentityConfigurationAsync(ct);
        if (!configuration.HasPrivilege(Username(http), Privileges.ManageUsers))
            throw new OpenholdException(StatusCodes.Status403Forbidden, "not_allowed", "Managing accounts needs the Manage Users privilege.");
        return actors is null ? new Actor(Guid.Empty, Username(http), "", "") : await actors.ResolveAsync(http);
    }
}
