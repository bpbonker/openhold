using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Openhold.Server.Identity;

/// <summary>
/// Encrypts secrets administrators enter in Openhold (e.g. an ERP's API key) before they are stored in the database,
/// with AES-256-GCM under a key kept in a file beside the token-signing key: readable only by the service account and
/// administrators, and included in backups, so a restore onto new hardware can still read them. The database alone
/// (or a copy of it) never reveals a secret.
/// </summary>
public sealed class SettingsSecrets(IOptions<OpenholdOptions> options, IHostEnvironment environment)
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly Lock _gate = new();
    private byte[]? _key;

    /// <summary>The key file: <c>settings-secrets.key</c> in the same folder as the token-signing key.</summary>
    public string KeyPath =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Path.Combine(environment.ContentRootPath, options.Value.Authentication.SigningKeyPath)))!,
            "settings-secrets.key");

    public byte[] Protect(string secret)
    {
        var plain = Encoding.UTF8.GetBytes(secret);
        var result = new byte[NonceSize + TagSize + plain.Length];
        var nonce = result.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(Key(create: true), TagSize);
        aes.Encrypt(nonce, plain, result.AsSpan(NonceSize + TagSize), result.AsSpan(NonceSize, TagSize));
        return result;
    }

    public string Unprotect(byte[] protectedSecret)
    {
        if (protectedSecret.Length < NonceSize + TagSize)
            throw new CryptographicException("That isn't a protected secret.");
        var plain = new byte[protectedSecret.Length - NonceSize - TagSize];
        using var aes = new AesGcm(Key(create: false), TagSize);
        aes.Decrypt(protectedSecret.AsSpan(0, NonceSize), protectedSecret.AsSpan(NonceSize + TagSize), protectedSecret.AsSpan(NonceSize, TagSize), plain);
        return Encoding.UTF8.GetString(plain);
    }

    private byte[] Key(bool create)
    {
        lock (_gate)
        {
            if (_key is not null)
                return _key;
            var path = KeyPath;
            if (!File.Exists(path))
            {
                if (!create)
                    throw new CryptographicException($"The key for stored secrets ({path}) is missing. Restore it from a backup, or enter the secrets again.");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(32));
                SigningKeys.RestrictToCurrentAccount(path);
            }
            return _key = File.ReadAllBytes(path);
        }
    }
}
