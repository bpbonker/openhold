namespace Openhold.Core.Content;

/// <summary>Converts between whole files and chunk manifests backed by an <see cref="IChunkStore"/>.</summary>
public static class ChunkedContent
{
    /// <summary>
    /// Chunks <paramref name="input"/> and writes every chunk the store does not already have.
    /// Returns the manifest describing the file.
    /// </summary>
    public static async Task<ChunkManifest> StoreAsync(Stream input, IChunkStore store, FastCdc? chunker = null, CancellationToken cancellationToken = default)
    {
        chunker ??= new FastCdc();
        var chunks = new List<ChunkRef>();
        var fileHasher = Blake3.Hasher.New();
        try
        {
            long length = 0;

            await chunker.SplitAsync(input, async chunk =>
            {
                fileHasher.Update(chunk.Span);
                length += chunk.Length;
                var hash = ContentHash.Compute(chunk.Span);
                chunks.Add(new ChunkRef(hash, chunk.Length));
                if (!await store.ContainsAsync(hash, cancellationToken).ConfigureAwait(false))
                    await store.PutAsync(hash, chunk, cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);

            return new ChunkManifest(ContentHash.FromBytes(fileHasher.Finalize().AsSpan()), length, chunks);
        }
        finally
        {
            fileHasher.Dispose();
        }
    }

    /// <summary>
    /// Reassembles a file from its manifest, verifying each chunk and the whole-file hash.
    /// </summary>
    /// <exception cref="ChunkIntegrityException">The reassembled content does not match the manifest.</exception>
    public static async Task WriteAsync(ChunkManifest manifest, IChunkStore store, Stream output, CancellationToken cancellationToken = default)
    {
        var fileHasher = Blake3.Hasher.New();
        try
        {
            long length = 0;

            foreach (var chunk in manifest.Chunks)
            {
                var data = await store.GetAsync(chunk.Hash, cancellationToken).ConfigureAwait(false);
                if (data.Length != chunk.Length)
                    throw new ChunkIntegrityException(chunk.Hash, $"expected {chunk.Length} bytes, got {data.Length}.");
                fileHasher.Update(data);
                length += data.Length;
                await output.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            }

            var fileHash = ContentHash.FromBytes(fileHasher.Finalize().AsSpan());
            if (length != manifest.Length || fileHash != manifest.FileHash)
                throw new ChunkIntegrityException(manifest.FileHash, $"reassembled file is {length} bytes hashing to {fileHash}.");
        }
        finally
        {
            fileHasher.Dispose();
        }
    }
}
