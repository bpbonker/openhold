using Openhold.Core.Content;

namespace Openhold.Core.Tests;

public class FastCdcTests
{
    private static byte[] RandomBytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static async Task<List<ContentHash>> ChunkHashesAsync(byte[] data, FastCdc chunker)
    {
        var hashes = new List<ContentHash>();
        await chunker.SplitAsync(new MemoryStream(data), chunk =>
        {
            hashes.Add(ContentHash.Compute(chunk.Span));
            return Task.CompletedTask;
        });
        return hashes;
    }

    [Fact]
    public async Task Chunking_is_deterministic()
    {
        var data = RandomBytes(20 * 1024 * 1024, seed: 1);
        var chunker = new FastCdc();

        Assert.Equal(await ChunkHashesAsync(data, chunker), await ChunkHashesAsync(data, chunker));
    }

    [Fact]
    public async Task Chunks_respect_size_bounds_and_cover_the_input()
    {
        var data = RandomBytes(30 * 1024 * 1024 + 12345, seed: 2);
        var chunker = new FastCdc();
        var lengths = new List<int>();

        await chunker.SplitAsync(new MemoryStream(data), chunk =>
        {
            lengths.Add(chunk.Length);
            return Task.CompletedTask;
        });

        Assert.Equal(data.Length, lengths.Sum());
        Assert.All(lengths[..^1], l => Assert.InRange(l, chunker.MinSize, chunker.MaxSize));
        Assert.InRange(lengths[^1], 1, chunker.MaxSize);
        // Normalized chunking should land the mean near the configured average.
        Assert.InRange(lengths.Average(), chunker.AverageSize * 0.6, chunker.AverageSize * 1.6);
    }

    [Fact]
    public async Task Small_edit_near_start_preserves_most_chunks()
    {
        // The property that makes re-saved CAD files cheap to replicate:
        // inserting bytes shifts every offset, but content-defined boundaries resynchronise.
        var original = RandomBytes(40 * 1024 * 1024, seed: 3);
        var edited = new byte[original.Length + 1000];
        Buffer.BlockCopy(original, 0, edited, 0, 500_000);
        new Random(4).NextBytes(edited.AsSpan(500_000, 1000));
        Buffer.BlockCopy(original, 500_000, edited, 501_000, original.Length - 500_000);

        var chunker = new FastCdc();
        var before = (await ChunkHashesAsync(original, chunker)).ToHashSet();
        var after = await ChunkHashesAsync(edited, chunker);

        var reused = after.Count(before.Contains);
        Assert.True(reused >= after.Count - 3, $"Only {reused}/{after.Count} chunks reused after a 1000-byte insert.");
    }

    [Fact]
    public async Task Empty_stream_produces_no_chunks()
    {
        var hashes = await ChunkHashesAsync([], new FastCdc());
        Assert.Empty(hashes);
    }

    [Fact]
    public void Rejects_invalid_sizes()
    {
        Assert.Throws<ArgumentException>(() => new FastCdc(minSize: 1024, averageSize: 3000, maxSize: 8192));
        Assert.Throws<ArgumentException>(() => new FastCdc(minSize: 4096, averageSize: 2048, maxSize: 8192));
    }
}
