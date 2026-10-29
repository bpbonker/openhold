namespace Openhold.Core.Content;

public sealed record ChunkRef(ContentHash Hash, int Length);

/// <summary>
/// The content of one file version: its ordered chunks plus a hash of the whole file,
/// so reassembly can be verified end to end and not just chunk by chunk.
/// </summary>
public sealed record ChunkManifest(ContentHash FileHash, long Length, IReadOnlyList<ChunkRef> Chunks)
{
    public IEnumerable<ContentHash> DistinctHashes() => Chunks.Select(c => c.Hash).Distinct();
}
