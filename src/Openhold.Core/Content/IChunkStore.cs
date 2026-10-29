namespace Openhold.Core.Content;

/// <summary>
/// Immutable content-addressed chunk storage. Chunks are never modified once written,
/// so replicas can copy them between sites without any conflict handling.
/// </summary>
public interface IChunkStore
{
    Task<bool> ContainsAsync(ContentHash hash, CancellationToken cancellationToken = default);

    /// <summary>Returns the subset of <paramref name="hashes"/> not present in this store.</summary>
    Task<IReadOnlyList<ContentHash>> FindMissingAsync(IEnumerable<ContentHash> hashes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a chunk after verifying that <paramref name="data"/> hashes to <paramref name="hash"/>.
    /// Idempotent: storing a chunk that already exists is a no-op.
    /// </summary>
    /// <exception cref="ChunkIntegrityException">The data does not match the hash.</exception>
    Task PutAsync(ContentHash hash, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>Reads and verifies a chunk.</summary>
    /// <exception cref="ChunkNotFoundException">The chunk is not in this store.</exception>
    /// <exception cref="ChunkIntegrityException">The stored data is corrupt.</exception>
    Task<byte[]> GetAsync(ContentHash hash, CancellationToken cancellationToken = default);
}

public sealed class ChunkNotFoundException(ContentHash hash)
    : Exception($"Chunk {hash} is not in the store.")
{
    public ContentHash Hash { get; } = hash;
}

public sealed class ChunkIntegrityException(ContentHash expected, string detail)
    : Exception($"Chunk {expected} failed integrity check: {detail}")
{
    public ContentHash Expected { get; } = expected;
}
