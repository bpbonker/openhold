using System.Collections.Concurrent;
using System.Security.Cryptography;
using Dapper;
using Microsoft.IdentityModel.Tokens;
using Openhold.Server.Data;

namespace Openhold.Server.Identity;

/// <summary>
/// The keys that sign and verify Openhold's access tokens. The authority holds the private key in a file (see
/// <see cref="AuthenticationOptions.SigningKeyPath"/>) and publishes the public key in the identity database, so every
/// site's server can verify tokens without the private key ever leaving the authority.
/// </summary>
public sealed class SigningKeys(VaultRegistry vaults)
{
    private readonly ConcurrentDictionary<string, SecurityKey> _public = new();
    private RsaSecurityKey? _private;

    /// <summary>The key new tokens are signed with; only on the authority.</summary>
    public SigningCredentials Signing =>
        _private is { } key ? new SigningCredentials(key, SecurityAlgorithms.RsaSha256) : throw new InvalidOperationException("Only the authority signs tokens.");

    public bool CanSign => _private is not null;

    /// <summary>
    /// Authority start-up: loads the private key, creating it on first run (readable only by the account the server
    /// runs as), and makes sure its public half is published.
    /// </summary>
    public async Task InitializeAuthorityAsync(string path, CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(path);
        var rsa = RSA.Create();
        if (File.Exists(full))
        {
            rsa.ImportFromPem(await File.ReadAllTextAsync(full, cancellationToken));
        }
        else
        {
            rsa.KeySize = 3072;
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, rsa.ExportPkcs8PrivateKeyPem(), cancellationToken);
            RestrictToCurrentAccount(full);
        }

        var publicPem = rsa.ExportSubjectPublicKeyInfoPem();
        var kid = Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()))[..16].ToLowerInvariant();
        _private = new RsaSecurityKey(rsa) { KeyId = kid };

        await using var connection = await vaults.Default.Db.Primary.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(
            "INSERT INTO signing_keys (kid, public_key) VALUES (@kid, @publicPem) ON CONFLICT (kid) DO NOTHING", new { kid, publicPem });
        _public[kid] = _private;
    }

    /// <summary>Verification keys for a token's key id, read from the identity database the first time each is seen.</summary>
    public IEnumerable<SecurityKey> Resolve(string? kid)
    {
        if (kid is null)
            return [];
        if (_public.TryGetValue(kid, out var cached))
            return [cached];

        using var connection = vaults.Default.Db.Local.OpenConnection();
        var pem = connection.ExecuteScalar<string?>("SELECT public_key FROM signing_keys WHERE kid = @kid AND retired_at IS NULL", new { kid });
        if (pem is null)
            return [];
        var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        var key = new RsaSecurityKey(rsa) { KeyId = kid };
        _public[kid] = key;
        return [key];
    }

    /// <summary>Makes a key file readable only by the account the server runs as (and, on Windows, administrators).</summary>
    internal static void RestrictToCurrentAccount(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return;
        }
        var info = new FileInfo(path);
        var security = new System.Security.AccessControl.FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var self = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            self, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
        var administrators = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            administrators, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
        info.SetAccessControl(security);
    }
}
