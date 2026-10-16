namespace GraphBridge.Testing;

/// <summary>Test-only bounded message framing after an explicitly armed, drained transaction boundary.</summary>
internal static class BoltBoundaryReader
{
    internal const int MaximumPayloadBytes = 65_536;
    internal const int MaximumChunks = 256;

    // Never return payloads: callers need only the tiny-structure marker and message signature.
    internal sealed record Message(byte Structure, byte Signature, byte? FirstFieldMarker, int PayloadBytes)
    {
        public bool IsCommit => Structure == 0xB0 && Signature == 0x12 && PayloadBytes == 2;
        public bool IsSuccess => Structure == 0xB1 && Signature == 0x70 && PayloadBytes >= 3
            && FirstFieldMarker is >= 0xA0 and <= 0xAF or 0xD8 or 0xD9 or 0xDA;
    }

    public static async Task<Message> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[2];
        var buffer = new byte[4096];
        var prefix = new byte[3];
        var payloadBytes = 0;
        try
        {
            for (var chunks = 0; chunks < MaximumChunks; chunks++)
            {
                await stream.ReadExactlyAsync(header, cancellationToken);
                var length = (header[0] << 8) | header[1];
                if (length == 0)
                {
                    if (payloadBytes == 0) continue; // Bolt keep-alive NOOP before a message.
                    if (payloadBytes < 2 || (prefix[0] & 0xF0) != 0xB0)
                        throw new InvalidDataException("Fault harness expected a Bolt tiny-structure message.");
                    return new(prefix[0], prefix[1], payloadBytes >= 3 ? prefix[2] : null, payloadBytes);
                }
                if (length > MaximumPayloadBytes - payloadBytes)
                    throw new InvalidDataException("Fault harness Bolt message exceeds its payload bound.");
                while (length > 0)
                {
                    var size = Math.Min(length, buffer.Length);
                    await stream.ReadExactlyAsync(buffer.AsMemory(0, size), cancellationToken);
                    for (var i = 0; i < size && payloadBytes + i < prefix.Length; i++)
                        prefix[payloadBytes + i] = buffer[i];
                    payloadBytes += size;
                    length -= size;
                }
            }
            throw new InvalidDataException("Fault harness Bolt message exceeds its chunk bound.");
        }
        finally { Array.Clear(buffer); }
    }
}
