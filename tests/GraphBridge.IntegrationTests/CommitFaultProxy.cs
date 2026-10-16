using System.Net;
using System.Net.Sockets;
using GraphBridge.Testing;

namespace GraphBridge.IntegrationTests;

internal enum CommitCut { BeforeForwardingCommit, AfterServerSuccess }
internal sealed record CommitCutObservation(bool CommitForwarded, bool ServerSuccessObserved);

/// <summary>One connection, fixed owned upstream, no payload logging, no database/process creation.</summary>
internal sealed class CommitFaultProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _lifetime = new(TimeSpan.FromSeconds(90));
    private readonly TaskCompletionSource<CommitCutObservation> _cut = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _forwarded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _worker;
    private TcpClient? _client;
    private TcpClient? _server;
    private int _armed;
    private int _armCalls;
    private int _disposed;
    private CommitCut _mode;

    public CommitFaultProxy(DisposableNeo4jFixture fixture)
    {
        // Fixture initialization has already checked exact endpoint, credentials and server marker.
        if (fixture.Driver is null || fixture.ServerEdition != "community" || fixture.Database != "neo4j")
            throw new InvalidOperationException("Fault proxy requires the initialized owned Community fixture.");
        _listener.Start(1);
        Uri = new Uri($"bolt://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
        _worker = RelayAsync();
    }

    public Uri Uri { get; }
    public int CommitCalls => Volatile.Read(ref _armCalls);
    public Task<CommitCutObservation> ObserveCutAsync() => _cut.Task.WaitAsync(TimeSpan.FromSeconds(15));

    public void Arm(CommitCut mode)
    {
        if (Interlocked.Increment(ref _armCalls) != 1)
            throw new InvalidOperationException("Fault harness saw an unexpected repeated commit attempt.");
        _mode = mode;
        Volatile.Write(ref _armed, 1);
    }

    private async Task RelayAsync()
    {
        try
        {
            _client = await _listener.AcceptTcpClientAsync(_lifetime.Token);
            _listener.Stop(); // A retry cannot silently acquire a new unobserved connection.
            _server = new TcpClient();
            await _server.ConnectAsync(IPAddress.Loopback, 17687, _lifetime.Token);
            var outbound = PumpAsync(_client.GetStream(), _server.GetStream(), fromClient: true);
            var inbound = PumpAsync(_server.GetStream(), _client.GetStream(), fromClient: false);
            await Task.WhenAll(outbound, inbound);
            _cut.TrySetException(new InvalidOperationException("Connection ended without the required verified commit cut."));
        }
        catch (Exception)
        {
            // Never retain raw transport data/exceptions in artifacts. ObserveCutAsync must fail
            // unless the expected boundary was independently validated before closing sockets.
            _cut.TrySetException(new InvalidOperationException("Fault harness ended before its verified commit cut."));
        }
        finally { CloseSockets(); }
    }

    private async Task PumpAsync(NetworkStream source, NetworkStream destination, bool fromClient)
    {
        var bytes = new byte[16_384];
        try
        {
            while (true)
            {
                var count = await source.ReadAsync(bytes, _lifetime.Token);
                if (count == 0) throw new EndOfStreamException();
                if (Volatile.Read(ref _armed) == 0)
                {
                    await destination.WriteAsync(bytes.AsMemory(0, count), _lifetime.Token);
                    continue;
                }

                // All prior result messages are consumed before the wrapper arms at CommitAsync.
                // Prefix handles a frame already partially obtained by the transparent relay.
                using var framed = new PrefixedStream(bytes.AsMemory(0, count), source);
                var message = await BoltBoundaryReader.ReadAsync(framed, _lifetime.Token);
                if (framed.PrefixRemaining != 0)
                    throw new InvalidDataException("Unexpected pipelined data at the commit boundary.");
                if (fromClient)
                {
                    if (!message.IsCommit) throw new InvalidDataException("Expected the exact COMMIT request.");
                    if (_mode == CommitCut.BeforeForwardingCommit)
                    {
                        _cut.TrySetResult(new(false, false));
                        CloseSockets();
                        return;
                    }
                    // The verified zero-field COMMIT has one canonical frame. Nothing else is replayed.
                    await destination.WriteAsync(new byte[] { 0, 2, 0xB0, 0x12, 0, 0 }, _lifetime.Token);
                    _forwarded.TrySetResult();
                    return;
                }
                await _forwarded.Task.WaitAsync(_lifetime.Token);
                if (_mode != CommitCut.AfterServerSuccess || !message.IsSuccess)
                    throw new InvalidDataException("Expected the complete SUCCESS response for COMMIT.");
                _cut.TrySetResult(new(true, true));
                CloseSockets(); // The acknowledgement is deliberately never forwarded.
                return;
            }
        }
        catch
        {
            // Stop the other pump too; Task.WhenAll must never wait on an orphaned read.
            CloseSockets();
            throw;
        }
        finally { Array.Clear(bytes); }
    }

    private void CloseSockets()
    {
        _listener.Stop();
        try { if (_client?.Client is { } socket) socket.LingerState = new LingerOption(true, 0); }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
        _client?.Dispose();
        _server?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _lifetime.CancelAsync();
        CloseSockets();
        await _worker;
        if (_cut.Task.IsFaulted) _ = _cut.Task.Exception;
        _lifetime.Dispose();
    }

    private sealed class PrefixedStream(ReadOnlyMemory<byte> prefix, Stream remainder) : Stream
    {
        private int _position;
        public int PrefixRemaining => prefix.Length - _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_position == prefix.Length) return remainder.ReadAsync(buffer, cancellationToken);
            var length = Math.Min(buffer.Length, PrefixRemaining);
            prefix.Slice(_position, length).CopyTo(buffer);
            _position += length;
            return ValueTask.FromResult(length);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
