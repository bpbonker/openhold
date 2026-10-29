using Dapper;
using Microsoft.Extensions.Options;
using Openhold.Server.Data;

namespace Openhold.Server.Identity;

/// <summary>Who is making a request, from which site and which workstation.</summary>
public sealed record Actor(Guid UserId, string Username, string SiteId, string Machine);

/// <summary>
/// Maps the authenticated principal to a vault user. Identity always comes
/// from the authentication layer, never from anything the client asserts. The machine name is only
/// used to bind a lock to the workstation that holds the checked-out copy.
/// </summary>
public sealed class ActorResolver(VaultInstance vault, IOptions<OpenholdOptions> options)
{
    public const string UserHeader = "X-Openhold-User";
    public const string MachineHeader = "X-Openhold-Machine";

    public async Task<Actor> ResolveAsync(HttpContext context)
    {
        var username = NormalizeUsername(context.User.Identity is { IsAuthenticated: true, Name: { } name } ? name : null)
            ?? throw new OpenholdException(StatusCodes.Status401Unauthorized, "unauthenticated", "Sign-in required.");

        var machine = context.Request.Headers[MachineHeader].ToString().Trim();
        if (machine.Length is 0 or > 64)
            machine = "unknown";

        if (!vault.UserIds.TryGetValue(username, out var userId))
        {
            // Cached after first sight so a remote site does not cross the WAN for every request. Someone already
            // known is found in the local copy, so a site cut off from the authority still knows who they are.
            await using (var local = await vault.Db.Local.OpenConnectionAsync(context.RequestAborted))
            {
                if (await local.ExecuteScalarAsync<Guid?>("SELECT id FROM users WHERE lower(username) = lower(@username)", new { username }) is { } known)
                {
                    vault.UserIds[username] = known;
                    return new Actor(known, username, options.Value.SiteId, machine);
                }
            }
            await using var connection = await vault.Db.Primary.OpenConnectionAsync(context.RequestAborted);
            userId = await connection.ExecuteScalarAsync<Guid>("""
                INSERT INTO users (id, username, display_name, home_site)
                VALUES (@id, @username, @username, @site)
                ON CONFLICT (lower(username)) DO UPDATE SET username = users.username
                RETURNING id
                """, new { id = Guid.NewGuid(), username, site = options.Value.SiteId });
            vault.UserIds[username] = userId;
        }

        return new Actor(userId, username, options.Value.SiteId, machine);
    }

    /// <summary>
    /// Domains whose people are known by their plain username (see <see cref="AuthenticationOptions.HomeDomains"/>).
    /// Empty: every domain is dropped, as for a single organisation.
    /// </summary>
    public static IReadOnlySet<string> HomeDomains { get; set; } = new HashSet<string>();

    /// <summary>
    /// "DOMAIN\jsmith" and "jsmith@domain" both become "jsmith" when the domain is a home domain (or none are set), so a
    /// person has one account across sites and sign-in methods. Someone from another domain stays "jsmith@domain",
    /// so two people with the same name in different companies are never mixed up.
    /// </summary>
    public static string? NormalizeUsername(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var trimmed = name.Trim();
        string? domain = null;
        var backslash = trimmed.LastIndexOf('\\');
        if (backslash >= 0)
        {
            domain = trimmed[..backslash];
            trimmed = trimmed[(backslash + 1)..];
        }
        var at = trimmed.IndexOf('@');
        if (at > 0)
        {
            domain = trimmed[(at + 1)..];
            trimmed = trimmed[..at];
        }
        if (trimmed.Length == 0)
            return null;
        var keepDomain = domain is { Length: > 0 } && HomeDomains.Count > 0 && !HomeDomains.Contains(domain);
        return (keepDomain ? $"{trimmed}@{domain}" : trimmed).ToLowerInvariant();
    }
}
