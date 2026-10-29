using ZstdSharp;

namespace Openhold.Core.Content;

/// <summary>
/// Stores chunks as zstd-compressed files under <c>root/ab/abcdef…</c>.
/// Writes go to a temp file and are renamed into place, so a crash never leaves a partial chunk
/// visible under its final name. Every read is re-hashed, so on-disk corruption is detected, not served.
/// </summary>
public sealed class FileSystemChunkStore : IChunkStore
{
    private const int CompressionLevel = 3;

    private readonly string _root;

    public FileSystemChunkStore(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public Task<bool> ContainsAsync(ContentHash hash, CancellationToken cancellationToken = default) =>
        Task.FromResult(File.Exists(PathFor(hash)));

    public Task<IReadOnlyList<ContentHash>> FindMissingAsync(IEnumerable<ContentHash> hashes, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ContentHash> missing = hashes.Distinct().Where(h => !File.Exists(PathFor(h))).ToList();
        return Task.FromResult(missing);
    }

    public async Task PutAsync(ContentHash hash, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var actual = ContentHash.Compute(data.Span);
        if (actual != hash)
            throw new ChunkIntegrityException(hash, $"data hashes to {actual}.");

        var finalPath = PathFor(hash);
        if (File.Exists(finalPath))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        var tempPath = $"{finalPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var compressor = new Compressor(CompressionLevel))
            {
                var compressed = compressor.Wrap(data.Span);
                await using var file = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
                await file.WriteAsync(compressed.ToArray(), cancellationToken).ConfigureAwait(false);
            }

            try
            {
                File.Move(tempPath, finalPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                // Another writer stored the same chunk first. Content is identical by definition.
            }
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    public async Task<byte[]> GetAsync(ContentHash hash, CancellationToken cancellationToken = default)
    {
        var path = PathFor(hash);
        byte[] compressed;
        try
        {
            compressed = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new ChunkNotFoundException(hash);
        }

        byte[] data;
        try
        {
            using var decompressor = new Decompressor();
            data = decompressor.Unwrap(compressed).ToArray();
        }
        catch (ZstdException ex)
        {
            throw new ChunkIntegrityException(hash, $"stored data is not valid zstd ({ex.Message}).");
        }

        var actual = ContentHash.Compute(data);
        if (actual != hash)
            throw new ChunkIntegrityException(hash, $"stored data hashes to {actual}.");
        return data;
    }

    private string PathFor(ContentHash hash)
    {
        var hex = hash.ToString();
        return Path.Combine(_root, hex[..2], hex);
    }
}
