using Openhold.Core.Content;

namespace Openhold.Core.Tests;

public class ChunkFramesTests
{
    private static byte[] RandomBytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    [Fact]
    public async Task Frames_round_trip_in_order()
    {
        var chunks = Enumerable.Range(0, 5).Select(i => RandomBytes(1000 + i * 37, i)).ToList();
        var stream = new MemoryStream();
        foreach (var chunk in chunks)
            await ChunkFrames.WriteAsync(stream, ContentHash.Compute(chunk), chunk);
        stream.Position = 0;

        var read = new List<byte[]>();
        await foreach (var (hash, data) in ChunkFrames.ReadAsync(stream, 4096))
        {
            Assert.Equal(ContentHash.Compute(data), hash);
            read.Add(data);
        }

        Assert.Equal(chunks, read);
    }

    [Fact]
    public async Task Tampered_frame_is_rejected()
    {
        var chunk = RandomBytes(500, 1);
        var stream = new MemoryStream();
        await ChunkFrames.WriteAsync(stream, ContentHash.Compute(chunk), chunk);
        stream.GetBuffer()[ContentHash.Size + 4 + 10] ^= 0xFF;
        stream.Position = 0;

        await Assert.ThrowsAsync<ChunkIntegrityException>(async () =>
        {
            await foreach (var _ in ChunkFrames.ReadAsync(stream, 4096)) { }
        });
    }

    [Fact]
    public async Task Truncated_or_oversized_frames_are_rejected()
    {
        var chunk = RandomBytes(500, 2);
        var stream = new MemoryStream();
        await ChunkFrames.WriteAsync(stream, ContentHash.Compute(chunk), chunk);
        var truncated = new MemoryStream(stream.ToArray()[..^10]);

        await Assert.ThrowsAsync<FormatException>(async () =>
        {
            await foreach (var _ in ChunkFrames.ReadAsync(truncated, 4096)) { }
        });
        stream.Position = 0;
        await Assert.ThrowsAsync<FormatException>(async () =>
        {
            await foreach (var _ in ChunkFrames.ReadAsync(stream, maxChunkSize: 100)) { }
        });
    }
}
