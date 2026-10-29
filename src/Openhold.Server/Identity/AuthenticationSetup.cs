using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Openhold.Server.Identity;

/// <summary>
/// Sets up sign-in. One request can carry Windows credentials (Negotiate), an Openhold access token, or an organisation
/// identity provider's token; a selector looks at the request and hands it to the right handler.
/// </summary>
public static class AuthenticationSetup
{
    public const string Selector = "Openhold";
    public const string OpenholdToken = "OpenholdToken";

    public static void AddOpenholdAuthentication(this WebApplicationBuilder builder, OpenholdOptions openhold, bool devAuth)
    {
        var settings = openhold.Authentication;
        var providers = settings.OpenIdConnect;
        ActorResolver.HomeDomains = settings.HomeDomains.Select(d => d.Trim()).Where(d => d.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var authentication = builder.Services.AddAuthentication(options =>
        {
            options.DefaultScheme = Selector;
            options.DefaultChallengeScheme = Selector;
        });

        authentication.AddPolicyScheme(Selector, "Openhold sign-in", options => options.ForwardDefaultSelector = context =>
        {
            var header = context.Request.Headers.Authorization.ToString();
            if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return SchemeForToken(header[7..].Trim(), providers);
            if (devAuth)
                return DevHeaderAuthenticationHandler.SchemeName;
            return settings.Windows ? NegotiateDefaults.AuthenticationScheme : OpenholdToken;
        });

        authentication.AddJwtBearer(OpenholdToken, options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = AccountService.Issuer,
                ValidAudience = AccountService.Audience,
                NameClaimType = "sub",
                ClockSkew = TimeSpan.FromMinutes(1),
            };
        });
        builder.Services.AddOptions<JwtBearerOptions>(OpenholdToken).Configure<SigningKeys>((options, keys) =>
            options.TokenValidationParameters.IssuerSigningKeyResolver = (_, _, kid, _) => keys.Resolve(kid));

        for (var i = 0; i < providers.Count; i++)
        {
            var provider = providers[i];
            authentication.AddJwtBearer(ProviderScheme(i), options =>
            {
                options.Authority = provider.Authority;
                options.Audience = provider.Audience ?? provider.ClientId;
                options.MapInboundClaims = false;
                options.TokenValidationParameters.NameClaimType = provider.UsernameClaim;
            });
        }

        if (devAuth)
            authentication.AddScheme<AuthenticationSchemeOptions, DevHeaderAuthenticationHandler>(DevHeaderAuthenticationHandler.SchemeName, null);
        else if (settings.Windows)
            authentication.AddNegotiate();
    }

    private static string ProviderScheme(int index) => $"oidc-{index}";

    /// <summary>Openhold's own tokens, or an organisation provider's, told apart by who issued them.</summary>
    private static string SchemeForToken(string token, List<OpenIdConnectProvider> providers)
    {
        try
        {
            var issuer = new JsonWebTokenHandler().ReadJsonWebToken(token).Issuer;
            var index = providers.FindIndex(p => string.Equals(p.Authority.TrimEnd('/'), issuer.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
            return index >= 0 ? ProviderScheme(index) : OpenholdToken;
        }
        catch (ArgumentException)
        {
            return OpenholdToken; // not a JWT at all; Openhold's handler refuses it
        }
    }

    /// <summary>The kind of account a signed-in request comes from.</summary>
    public static string KindOf(HttpContext context)
    {
        var type = context.User.Identity?.AuthenticationType ?? "";
        if (type.StartsWith("oidc-", StringComparison.Ordinal) || context.User.FindFirst("iss")?.Value is { } issuer && issuer != AccountService.Issuer)
            return "oidc";
        if (context.User.FindFirst("iss")?.Value == AccountService.Issuer)
            return "local";
        return "windows";
    }
}

/// <summary>
/// Runs on every signed-in request: refuses disabled accounts (whatever way they signed in), records Windows and
/// organisation accounts the first time, and holds an Openhold account to changing its password or setting up two-factor
/// sign-in before it can do anything else.
/// </summary>
public sealed class AccountGate(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, AccountService accounts)
    {
        if (context.User.Identity is { IsAuthenticated: true, Name: { } name } && ActorResolver.NormalizeUsername(name) is { } username)
        {
            await accounts.EnsureAllowedAsync(username, AuthenticationSetup.KindOf(context), context.User.FindFirst("name")?.Value, context.RequestAborted);
            if (context.User.FindFirst(AccountService.RestrictionClaim)?.Value is { } restriction && !context.Request.Path.StartsWithSegments("/api/auth"))
            {
                throw new OpenholdException(StatusCodes.Status403Forbidden, restriction == "change_password" ? "password_change_required" : "two_factor_setup_required",
                    restriction == "change_password" ? "Change your password before carrying on." : "Set up two-factor sign-in before carrying on.");
            }
        }
        await next(context);
    }
}
