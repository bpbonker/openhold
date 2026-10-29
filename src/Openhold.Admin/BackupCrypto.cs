using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Openhold.Admin;

/// <summary>
/// Encryption for backups. A passphrase (kept off the server, e.g. in the company's password manager) is stretched
/// with PBKDF2-SHA256 into a 256-bit key; files are encrypted with AES-256-GCM in 1 MB segments, each with its own
/// nonce and authentication tag, so any tampering or corruption is detected and large dumps never sit whole in
/// memory. Segment order and the end of the file are authenticated too, so segments can't be dropped or reordered.
/// </summary>
public sealed class BackupCrypto
{
    private const int SegmentSize = 1 << 20;
    private const int Iterations = 600_000;
    private static readonly byte[] Magic = "OPENHOLD-BACKUP-1\n"u8.ToArray();

    // Backups made during the pilot, when Openhold was called Strata, used these markers. They still restore.
    private static readonly byte[] PilotMagic = "STRATA-BACKUP-1\n"u8.ToArray();
    private const string PilotKeyCheck = "strata backup key check";

    private readonly byte[] _key;

    private BackupCrypto(byte[] key) => _key = key;

    /// <summary>
    /// Opens (or, the first time, sets up) the backup destination's key settings: <c>backup-key.json</c> holds the
    /// salt and a check value, never the key, so a wrong passphrase is reported at once instead of producing garbage.
    /// </summary>
    public static BackupCrypto Open(string destination, string passphrase)
    {
        if (passphrase.Length < 16)
            throw new BackupException("The backup passphrase must be at least 16 characters. Keep it in a password manager, not on the server.");
        var path = Path.Combine(destination, "backup-key.json");
        KeySettings settings;
        if (File.Exists(path))
        {
            settings = JsonSerializer.Deserialize<KeySettings>(File.ReadAllText(path)) ?? throw new BackupException($"{path} is unreadable.");
        }
        else
        {
            Directory.CreateDirectory(destination);
            settings = new KeySettings(Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)), Iterations, "");
        }

        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, Convert.FromBase64String(settings.Salt), settings.Iterations, HashAlgorithmName.SHA256, 32);
        var check = Convert.ToBase64String(HMACSHA256.HashData(key, "openhold backup key check"u8));
        var pilotCheck = Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(PilotKeyCheck)));
        bool Matches(string value) => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(value), Encoding.ASCII.GetBytes(settings.Check));
        if (settings.Check.Length == 0)
            File.WriteAllText(path, JsonSerializer.Serialize(settings with { Check = check }, new JsonSerializerOptions { WriteIndented = true }));
        else if (!Matches(check) && !Matches(pilotCheck))
            throw new BackupException("That passphrase doesn't match the one these backups were made with.");
        return new BackupCrypto(key);
    }

    public void EncryptFile(string source, string destination)
    {
        using var input = File.OpenRead(source);
        Encrypt(input, destination);
    }

    public void Encrypt(Stream input, string destination)
    {
        var temp = destination + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using (var output = File.Create(temp))
        {
            output.Write(Magic);
            var fileNonce = RandomNumberGenerator.GetBytes(8);
            output.Write(fileNonce);
            using var aes = new AesGcm(_key, 16);
            var plain = new byte[SegmentSize];
            var cipher = new byte[SegmentSize];
            var tag = new byte[16];
            var header = new byte[5];
            for (uint index = 0; ; index++)
            {
                var read = ReadFull(input, plain);
                var last = read < SegmentSize;
                var nonce = Nonce(fileNonce, index);
                aes.Encrypt(nonce, plain.AsSpan(0, read), cipher.AsSpan(0, read), tag, AssociatedData(index, last));
                BinaryPrimitives.WriteInt32BigEndian(header, read);
                header[4] = last ? (byte)1 : (byte)0;
                output.Write(header);
                output.Write(tag);
                output.Write(cipher, 0, read);
                if (last)
                    break;
            }
        }
        File.Move(temp, destination, overwrite: true);
    }

    public void DecryptFile(string source, string destination)
    {
        var temp = destination + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        using (var output = File.Create(temp))
            Decrypt(source, output);
        File.Move(temp, destination, overwrite: true);
    }

    /// <summary>Decrypts and checks every segment; throws <see cref="BackupException"/> if anything was changed or cut short.</summary>
    public void Decrypt(string source, Stream output)
    {
        using var input = File.OpenRead(source);
        var magic = new byte[Magic.Length];
        var read = ReadFull(input, magic.AsSpan(0, PilotMagic.Length));
        var known = read == PilotMagic.Length && magic.AsSpan(0, read).SequenceEqual(PilotMagic)
            || read + ReadFull(input, magic.AsSpan(read)) == Magic.Length && magic.AsSpan().SequenceEqual(Magic);
        if (!known)
            throw new BackupException($"{Path.GetFileName(source)} isn't an Openhold backup file.");
        var fileNonce = new byte[8];
        ReadFull(input, fileNonce);
        using var aes = new AesGcm(_key, 16);
        var header = new byte[5];
        var tag = new byte[16];
        var cipher = new byte[SegmentSize];
        var plain = new byte[SegmentSize];
        for (uint index = 0; ; index++)
        {
            if (ReadFull(input, header) != 5 || ReadFull(input, tag) != 16)
                throw new BackupException($"{Path.GetFileName(source)} is cut short.");
            var length = BinaryPrimitives.ReadInt32BigEndian(header);
            var last = header[4] == 1;
            if (length is < 0 or > SegmentSize || ReadFull(input, cipher.AsSpan(0, length)) != length)
                throw new BackupException($"{Path.GetFileName(source)} is damaged.");
            try
            {
                aes.Decrypt(Nonce(fileNonce, index), cipher.AsSpan(0, length), tag, plain.AsSpan(0, length), AssociatedData(index, last));
            }
            catch (AuthenticationTagMismatchException)
            {
                throw new BackupException($"{Path.GetFileName(source)} has been changed or damaged since it was backed up.");
            }
            output.Write(plain, 0, length);
            if (last)
                return;
        }
    }

    private static byte[] Nonce(byte[] fileNonce, uint index)
    {
        var nonce = new byte[12];
        fileNonce.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8), index);
        return nonce;
    }

    private static byte[] AssociatedData(uint index, bool last)
    {
        var data = new byte[5];
        BinaryPrimitives.WriteUInt32BigEndian(data, index);
        data[4] = last ? (byte)1 : (byte)0;
        return data;
    }

    private static int ReadFull(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read == 0)
                break;
            total += read;
        }
        return total;
    }

    private sealed record KeySettings(string Salt, int Iterations, string Check);
}

/// <summary>A problem the administrator can act on; reported without a stack trace.</summary>
public sealed class BackupException(string message) : Exception(message);
