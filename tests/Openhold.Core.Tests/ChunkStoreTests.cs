using Openhold.Core.Content;

namespace Openhold.Core.Tests;

public sealed class ChunkStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "openhold-tests", Guid.NewGuid().ToString("N"));
    private readonly FileSystemChunkStore _store;

    public ChunkStoreTests() => _store = new FileSystemChunkStore(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static byte[] RandomBytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    [Fact]
    public async Task File_round_trips_through_store()
    {
        var data = RandomBytes(9 * 1024 * 1024 + 77, seed: 10);

        var manifest = await ChunkedContent.StoreAsync(new MemoryStream(data), _store);
        var output = new MemoryStream();
        await ChunkedContent.WriteAsync(manifest, _store, output);

        Assert.Equal(data, output.ToArray());
        Assert.Equal(data.Length, manifest.Length);
        Assert.Equal(ContentHash.Compute(data), manifest.FileHash);
    }

    [Fact]
    public async Task Storing_same_content_twice_adds_no_new_chunks()
    {
        var data = RandomBytes(6 * 1024 * 1024, seed: 11);
        var manifest = await ChunkedContent.StoreAsync(new MemoryStream(data), _store);
        var filesBefore = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Length;

        await ChunkedContent.StoreAsync(new MemoryStream(data), _store);

        Assert.Equal(filesBefore, Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Length);
        Assert.Empty(await _store.FindMissingAsync(manifest.DistinctHashes()));
    }

    [Fact]
    public async Task Put_rejects_data_that_does_not_match_hash()
    {
        var wrongHash = ContentHash.Compute([1, 2, 3]);
        await Assert.ThrowsAsync<ChunkIntegrityException>(() => _store.PutAsync(wrongHash, new byte[] { 9, 9, 9 }));
        Assert.False(await _store.ContainsAsync(wrongHash));
    }

    [Fact]
    public async Task Get_detects_on_disk_corruption()
    {
        var data = RandomBytes(4096, seed: 12);
        var hash = ContentHash.Compute(data);
        await _store.PutAsync(hash, data);

        var path = Directory.GetFiles(_root, hash.ToString(), SearchOption.AllDirectories).Single();
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[^5] ^= 0xFF;
        await File.WriteAllBytesAsync(path, bytes);

        await Assert.ThrowsAsync<ChunkIntegrityException>(() => _store.GetAsync(hash));
    }

    [Fact]
    public async Task Get_missing_chunk_throws_not_found()
    {
        await Assert.ThrowsAsync<ChunkNotFoundException>(() => _store.GetAsync(ContentHash.Compute([42])));
    }

    [Fact]
    public async Task FindMissing_returns_only_absent_hashes()
    {
        var present = RandomBytes(100, seed: 13);
        var presentHash = ContentHash.Compute(present);
        var absentHash = ContentHash.Compute(RandomBytes(100, seed: 14));
        await _store.PutAsync(presentHash, present);

        var missing = await _store.FindMissingAsync([presentHash, absentHash, absentHash]);

        Assert.Equal([absentHash], missing);
    }

    [Fact]
    public void Hash_parses_and_formats_round_trip()
    {
        var hash = ContentHash.Compute([1, 2, 3]);
        Assert.Equal(hash, ContentHash.Parse(hash.ToString()));
        Assert.False(ContentHash.TryParse("not-hex", out _));
    }
}
