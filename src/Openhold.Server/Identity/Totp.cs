using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Openhold.Server.Identity;

/// <summary>
/// Time-based one-time codes (RFC 6238), the six-digit codes from Microsoft Authenticator, Google Authenticator and
/// similar apps: 30-second steps, HMAC-SHA1, one step either side allowed for clock drift.
/// </summary>
public static class Totp
{
    private const int StepSeconds = 30;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    /// <summary>What an authenticator app scans, as a QR code, to add the account.</summary>
    public static string SetupUri(byte[] secret, string issuer, string account) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}" +
        $"?secret={Base32(secret)}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits=6&period={StepSeconds}";

    public static string Base32(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var builder = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                builder.Append(alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0)
            builder.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return builder.ToString();
    }

    public static string Code(byte[] secret, DateTimeOffset at) => Code(secret, at.ToUnixTimeSeconds() / StepSeconds);

    public static bool Verify(byte[] secret, string? code, DateTimeOffset now)
    {
        code = code?.Replace(" ", "").Trim();
        if (code is not { Length: 6 } || !code.All(char.IsAsciiDigit))
            return false;
        var step = now.ToUnixTimeSeconds() / StepSeconds;
        var match = false;
        for (var drift = -1; drift <= 1; drift++)
            match |= CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, step + drift)), Encoding.ASCII.GetBytes(code));
        return match;
    }

    private static string Code(byte[] secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        var hash = HMACSHA1.HashData(secret, counter);
        var offset = hash[^1] & 0x0F;
        var value = (BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset)) & 0x7FFFFFFF) % 1_000_000;
        return value.ToString("000000", System.Globalization.CultureInfo.InvariantCulture);
    }
}
