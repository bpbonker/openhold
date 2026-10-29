using System.Buffers.Binary;

namespace Openhold.Core.Content;

/// <summary>
/// Packs many chunks into one stream as <c>[hash(32)][length(4)][data]</c> frames, so a batch of small
/// chunks costs one request instead of one each. At ~150 ms between sites, per-request round trips,
/// not bandwidth, would otherwise cap throughput.
/// </summary>
public static class ChunkFrames
{
    private const int HeaderSize = ContentHash.Size + 4;

    public static async Task WriteAsync(Stream output, ContentHash hash, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var header = new byte[HeaderSize];
        hash.Bytes.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(ContentHash.Size), data.Length);
        await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads frames until the end of the stream. Each frame's data is verified against its hash.
    /// </summary>
    /// <exception cref="ChunkIntegrityException">A frame's data does not match its hash.</exception>
    /// <exception cref="FormatException">The stream is truncated or a frame exceeds <paramref name="maxChunkSize"/>.</exception>
    public static async IAsyncEnumerable<(ContentHash Hash, byte[] Data)> ReadAsync(
        Stream input, int maxChunkSize, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var header = new byte[HeaderSize];
        while (true)
        {
            var read = await ReadFullyAsync(input, header, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                yield break;
            if (read < HeaderSize)
                throw new FormatException("Chunk stream ended inside a frame header.");

            var hash = ContentHash.FromBytes(header.AsSpan(0, ContentHash.Size));
            var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(ContentHash.Size));
            if (length <= 0 || length > maxChunkSize)
                throw new FormatException($"Chunk frame length {length} is outside 1..{maxChunkSize}.");

            var data = new byte[length];
            if (await ReadFullyAsync(input, data, cancellationToken).ConfigureAwait(false) < length)
                throw new FormatException("Chunk stream ended inside a frame.");

            var actual = ContentHash.Compute(data);
            if (actual != hash)
                throw new ChunkIntegrityException(hash, $"frame data hashes to {actual}.");
            yield return (hash, data);
        }
    }

    private static async Task<int> ReadFullyAsync(Stream input, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await input.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
        }
        return total;
    }
}
