using System.Buffers.Binary;

namespace Openhold.Core.Content;

/// <summary>
/// Compact binary encoding of a <see cref="ChunkManifest"/> (36 bytes per chunk), used on the wire
/// and in the database. Layout: version(1) fileHash(32) length(8) count(4) then count × [hash(32) length(4)].
/// </summary>
public static class ManifestCodec
{
    private const byte FormatVersion = 1;
    private const int HeaderSize = 1 + ContentHash.Size + 8 + 4;
    private const int EntrySize = ContentHash.Size + 4;

    public static byte[] Encode(ChunkManifest manifest)
    {
        var buffer = new byte[HeaderSize + manifest.Chunks.Count * EntrySize];
        var span = buffer.AsSpan();
        span[0] = FormatVersion;
        manifest.FileHash.Bytes.CopyTo(span[1..]);
        BinaryPrimitives.WriteInt64LittleEndian(span[33..], manifest.Length);
        BinaryPrimitives.WriteInt32LittleEndian(span[41..], manifest.Chunks.Count);

        var offset = HeaderSize;
        foreach (var chunk in manifest.Chunks)
        {
            chunk.Hash.Bytes.CopyTo(span[offset..]);
            BinaryPrimitives.WriteInt32LittleEndian(span[(offset + ContentHash.Size)..], chunk.Length);
            offset += EntrySize;
        }
        return buffer;
    }

    public static ChunkManifest Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || data[0] != FormatVersion)
            throw new FormatException("Not an Openhold chunk manifest, or an unsupported manifest version.");

        var fileHash = ContentHash.FromBytes(data.Slice(1, ContentHash.Size));
        var length = BinaryPrimitives.ReadInt64LittleEndian(data[33..]);
        var count = BinaryPrimitives.ReadInt32LittleEndian(data[41..]);
        if (count < 0 || data.Length != HeaderSize + (long)count * EntrySize)
            throw new FormatException("Chunk manifest is truncated or has trailing data.");

        var chunks = new ChunkRef[count];
        long total = 0;
        for (var i = 0; i < count; i++)
        {
            var entry = data.Slice(HeaderSize + i * EntrySize, EntrySize);
            var chunkLength = BinaryPrimitives.ReadInt32LittleEndian(entry[ContentHash.Size..]);
            if (chunkLength <= 0)
                throw new FormatException($"Chunk {i} has invalid length {chunkLength}.");
            chunks[i] = new ChunkRef(ContentHash.FromBytes(entry[..ContentHash.Size]), chunkLength);
            total += chunkLength;
        }

        if (total != length)
            throw new FormatException($"Chunk lengths sum to {total} but manifest declares {length}.");
        return new ChunkManifest(fileHash, length, chunks);
    }
}
