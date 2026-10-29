using System.Diagnostics.CodeAnalysis;

namespace Openhold.Core.Content;

/// <summary>
/// A 32-byte BLAKE3 digest identifying a chunk or a whole file's content.
/// Rendered as 64 lowercase hex characters on the wire and on disk.
/// </summary>
public readonly record struct ContentHash : IComparable<ContentHash>
{
    public const int Size = 32;

    private readonly byte[] _bytes;

    private ContentHash(byte[] bytes) => _bytes = bytes;

    public ReadOnlySpan<byte> Bytes => _bytes;

    public static ContentHash FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
            throw new ArgumentException($"Content hash must be {Size} bytes, got {bytes.Length}.", nameof(bytes));
        return new ContentHash(bytes.ToArray());
    }

    public static ContentHash Compute(ReadOnlySpan<byte> data)
    {
        var hash = Blake3.Hasher.Hash(data);
        return new ContentHash(hash.AsSpan().ToArray());
    }

    public static ContentHash Parse(string hex)
    {
        if (!TryParse(hex, out var hash))
            throw new FormatException($"'{hex}' is not a valid {Size * 2}-character hex content hash.");
        return hash;
    }

    public static bool TryParse([NotNullWhen(true)] string? hex, out ContentHash hash)
    {
        hash = default;
        if (hex is null || hex.Length != Size * 2)
            return false;
        try
        {
            hash = new ContentHash(Convert.FromHexString(hex));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public bool Equals(ContentHash other) => Bytes.SequenceEqual(other.Bytes);

    public override int GetHashCode() => _bytes is null ? 0 : BitConverter.ToInt32(_bytes, 0);

    public int CompareTo(ContentHash other) => Bytes.SequenceCompareTo(other.Bytes);

    public override string ToString() => Convert.ToHexString(_bytes).ToLowerInvariant();
}
