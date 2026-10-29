using System.Security.Cryptography;

namespace Openhold.Server.Identity;

/// <summary>
/// Password hashing for Openhold accounts: PBKDF2 with SHA-512 at OWASP's recommended work factor, a random salt per
/// password, and the parameters stored with the hash so the work factor can be raised later without breaking old ones.
/// </summary>
public static class Passwords
{
    public const int MinimumLength = 12;

    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>"pbkdf2-sha512$210000$&lt;salt&gt;$&lt;hash&gt;", both base64.</summary>
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA512, HashBytes);
        return $"pbkdf2-sha512${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string? stored)
    {
        var parts = stored?.Split('$');
        if (parts is not ["pbkdf2-sha512", var iterations, var salt, var hash] || !int.TryParse(iterations, out var count))
        {
            // Spend the same time as a real check, so timing doesn't tell which usernames exist.
            Rfc2898DeriveBytes.Pbkdf2(password, new byte[SaltBytes], Iterations, HashAlgorithmName.SHA512, HashBytes);
            return false;
        }
        var expected = Convert.FromBase64String(hash);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(salt), count, HashAlgorithmName.SHA512, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Why a new password isn't acceptable, or null if it is.</summary>
    public static string? Weakness(string password, string username)
    {
        if (password.Length < MinimumLength)
            return $"Use at least {MinimumLength} characters. A short sentence is easy to remember and hard to guess.";
        if (password.Contains(username, StringComparison.OrdinalIgnoreCase))
            return "Don't include your username in your password.";
        if (password.Distinct().Count() < 5)
            return "Use a wider mix of characters.";
        return null;
    }

    /// <summary>A random temporary password an administrator gives a person, who must change it when they first sign in.</summary>
    public static string Temporary()
    {
        // No look-alike characters (0/O, 1/l/I), grouped so it can be read out over the phone.
        const string alphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var chars = Enumerable.Range(0, 16).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray();
        return $"{new string(chars, 0, 4)}-{new string(chars, 4, 4)}-{new string(chars, 8, 4)}-{new string(chars, 12, 4)}";
    }
}
