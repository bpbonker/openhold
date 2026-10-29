using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Openhold.Server.Identity;

/// <summary>
/// Development-only authentication that trusts the <c>X-Openhold-User</c> header. It provides no security at
/// all, so <see cref="SecurityGuards"/> refuses to start the server with it outside the Development environment.
/// </summary>
public sealed class DevHeaderAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DevHeader";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var username = Request.Headers[ActorResolver.UserHeader].ToString().Trim();
        if (username.Length == 0)
            return Task.FromResult(AuthenticateResult.NoResult());

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, username)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

public static class SecurityGuards
{
    /// <summary>Fails closed: an insecure configuration stops the server instead of running exposed.</summary>
    public static void Validate(OpenholdOptions options, IHostEnvironment environment)
    {
        if (options.AuthMode.Equals("Dev", StringComparison.OrdinalIgnoreCase) && !environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "Openhold:AuthMode 'Dev' trusts an unauthenticated header and is only allowed in the Development environment. " +
                "Use 'Production' (sign-in methods under Openhold:Authentication) for any shared or production server.");
        }
        if (!options.AuthMode.Equals("Dev", StringComparison.OrdinalIgnoreCase)
            && !options.AuthMode.Equals("Production", StringComparison.OrdinalIgnoreCase)
            && !options.AuthMode.Equals("Windows", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unknown Openhold:AuthMode '{options.AuthMode}'. Use 'Production' (sign-in methods under Openhold:Authentication).");
        }
        var settings = options.Authentication;
        if (!options.AuthMode.Equals("Dev", StringComparison.OrdinalIgnoreCase) && !settings.Windows && !settings.LocalAccounts && settings.OpenIdConnect.Count == 0)
            throw new InvalidOperationException("No way to sign in is switched on: enable Windows, LocalAccounts or an OpenIdConnect provider under Openhold:Authentication.");
    }
}
