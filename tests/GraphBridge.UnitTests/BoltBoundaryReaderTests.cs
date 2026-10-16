using GraphBridge.Testing;

namespace GraphBridge.UnitTests;

public sealed class BoltBoundaryReaderTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(64)]
    public async Task Commit_spanning_chunks_and_fragmented_reads_is_recognized(int fragment)
    {
        using var stream = new FragmentedStream([0, 0, 0, 1, 0xB0, 0, 1, 0x12, 0, 0], fragment);
        var message = await BoltBoundaryReader.ReadAsync(stream, default);
        Assert.True(message.IsCommit);
        Assert.False(message.IsSuccess);
        Assert.Equal(2, message.PayloadBytes);
        Assert.Null(message.FirstFieldMarker);
    }

    [Fact]
    public async Task Success_header_is_recognized_without_retaining_its_metadata()
    {
        using var stream = new FragmentedStream([0, 3, 0xB1, 0x70, 0xA0, 0, 0], 1);
        var message = await BoltBoundaryReader.ReadAsync(stream, default);
        Assert.True(message.IsSuccess);
        Assert.False(message.IsCommit);
        Assert.Equal(3, message.PayloadBytes);
    }

    [Fact]
    public async Task Reader_stops_at_the_boundary_and_preserves_following_message()
    {
        using var stream = new MemoryStream([0, 2, 0xB0, 0x12, 0, 0, 0, 3, 0xB1, 0x70, 0xA0, 0, 0]);
        Assert.True((await BoltBoundaryReader.ReadAsync(stream, default)).IsCommit);
        Assert.Equal(6, stream.Position);
        Assert.True((await BoltBoundaryReader.ReadAsync(stream, default)).IsSuccess);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Theory]
    [InlineData(0x7F)]
    [InlineData(0x7E)]
    [InlineData(0x71)]
    public async Task Failure_ignored_and_record_are_not_success(int signature)
    {
        using var stream = new MemoryStream([0, 3, 0xB1, (byte)signature, 0xA0, 0, 0]);
        var message = await BoltBoundaryReader.ReadAsync(stream, default);
        Assert.False(message.IsSuccess);
        Assert.False(message.IsCommit);
    }

    [Fact]
    public async Task Wrong_field_count_or_metadata_marker_is_not_commit_or_success()
    {
        foreach (byte[] bytes in new byte[][]
        {
            [0, 3, 0xB1, 0x12, 0xA0, 0, 0],
            [0, 2, 0xB0, 0x70, 0, 0],
            [0, 3, 0xB1, 0x70, 0x80, 0, 0]
        })
        {
            using var stream = new MemoryStream(bytes);
            var message = await BoltBoundaryReader.ReadAsync(stream, default);
            Assert.False(message.IsCommit);
            Assert.False(message.IsSuccess);
        }
    }

    [Fact]
    public async Task Truncation_and_nonstructure_payload_fail_closed()
    {
        foreach (byte[] bytes in new byte[][] { [0], [0, 2, 0xB0], [0, 2, 0xB0, 0x12, 0] })
        {
            using var stream = new MemoryStream(bytes);
            await Assert.ThrowsAsync<EndOfStreamException>(() => BoltBoundaryReader.ReadAsync(stream, default));
        }
        using var malformed = new MemoryStream([0, 2, 0x80, 0x12, 0, 0]);
        await Assert.ThrowsAsync<InvalidDataException>(() => BoltBoundaryReader.ReadAsync(malformed, default));
    }

    [Fact]
    public async Task Excessive_payload_and_keep_alive_chunks_are_bounded()
    {
        using var payload = new MemoryStream();
        payload.Write([0xFF, 0xFF]);
        payload.Write(new byte[65_535]);
        payload.Write([0, 2]); // Rejected before reading bytes beyond the aggregate bound.
        payload.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => BoltBoundaryReader.ReadAsync(payload, default));
        using var noops = new MemoryStream(new byte[BoltBoundaryReader.MaximumChunks * 2]);
        await Assert.ThrowsAsync<InvalidDataException>(() => BoltBoundaryReader.ReadAsync(noops, default));
    }

    [Fact]
    public async Task Pre_cancelled_read_does_not_consume_the_stream()
    {
        using var stream = new MemoryStream([0, 2, 0xB0, 0x12, 0, 0]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BoltBoundaryReader.ReadAsync(stream, cancellation.Token));
        Assert.Equal(0, stream.Position);
    }

    private sealed class FragmentedStream(byte[] bytes, int maximumRead) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, maximumRead)], cancellationToken);
    }
}
