using System.Reflection;
using Neo4j.Driver;

namespace GraphBridge.Benchmarks;

/// <summary>
/// An untimed, single-connection observer for the exact Neo4j.Driver 6.3.0 logging contract.
/// Counts completed serial Bolt batch exchanges, never TCP packets, physical RTT latency or API calls.
/// Install directly with WithLogger; Begin only after connection/authentication/warmup is fully drained.
/// The logger inspects only templates and argument runtime types. It never formats, reads payload fields,
/// retains arguments, hex buffers, exceptions, query text, credentials or response data.
/// </summary>
/// <remarks>
/// Pinned source f8a4020c589489a92c969856d6a624619080d276: SocketClient.SendAsync emits one Debug C event
/// per request; ChunkWriter.SendAsync emits one Trace C event before its network write. ResponsePipeline
/// emits Debug S events before dispatch; SUCCESS/FAILURE/IGNORED each dequeue one request handler,
/// RECORD only peeks. PrefixNeo4JLogger preserves the argument objects and prefixes connection identity.
/// This is a version-sensitive benchmark observation, not a supported driver protocol instrumentation API.
/// Source: https://github.com/neo4j/neo4j-dotnet-driver/blob/f8a4020c589489a92c969856d6a624619080d276/Neo4j.Driver/Neo4j.Driver/Internal/Connector/SocketClient.cs
/// Source: https://github.com/neo4j/neo4j-dotnet-driver/blob/f8a4020c589489a92c969856d6a624619080d276/Neo4j.Driver/Neo4j.Driver/Internal/IO/ChunkWriter.cs
/// Source: https://github.com/neo4j/neo4j-dotnet-driver/blob/f8a4020c589489a92c969856d6a624619080d276/Neo4j.Driver/Neo4j.Driver/Internal/MessageHandling/ResponsePipeline.cs
/// Source: https://github.com/neo4j/neo4j-dotnet-driver/blob/f8a4020c589489a92c969856d6a624619080d276/Neo4j.Driver/Neo4j.Driver/Internal/Logging/ReformattedNeo4JLogger.cs
/// </remarks>
public sealed class BoltProtocolObservation : INeo4jLogger
{
    private const string Messaging = "Neo4j.Driver.Internal.Messaging.";
    private const long MaximumEvents = 10_000_000;
    private const int MaximumBatchRequests = 65_536;
    private readonly object _gate = new();
    private readonly Queue<RequestKind> _requests = new();
    private readonly long[] _requestCounts = new long[Enum.GetValues<RequestKind>().Length];
    private bool _active;
    private bool _flushed;
    private string? _connection;
    private string? _invalidReason;
    private long _events, _requestMessages, _flushes, _successes, _failures, _ignored, _records, _completed;

    /// <summary>
    /// Begins a fresh untimed scope. Caller must have awaited a consumed warmup on the same dedicated driver,
    /// with no outstanding results or concurrent operations. Connection changes and overlapping batches fail closed.
    /// </summary>
    public void Begin()
    {
        lock (_gate)
        {
            if (_active) throw new InvalidOperationException("A Bolt observation scope is already active.");
            _requests.Clear();
            Array.Clear(_requestCounts);
            _flushed = false;
            _connection = null;
            // This NuGet release's assembly version is 6.3.11.0; pin the actual package/source identity.
            _invalidReason = typeof(IDriver).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                == "6.3.0+f8a4020c589489a92c969856d6a624619080d276"
                ? null : "Only the pinned Neo4j.Driver 6.3.0 protocol logging contract is supported.";
            _events = _requestMessages = _flushes = _successes = _failures = _ignored = _records = _completed = 0;
            _active = true;
        }
    }

    /// <summary>
    /// Stops observation after the caller has consumed every result and awaited commit/rollback/session cleanup.
    /// Invalid scopes return null CompletedSerialExchanges; partial counters never become round-trip estimates.
    /// </summary>
    public BoltProtocolMeasurement Complete(bool operationSucceeded)
    {
        lock (_gate)
        {
            if (!_active) return InvalidWithoutScope();
            _active = false;
            if (!operationSucceeded) Invalidate("Observed operation did not complete successfully.");
            if (_requests.Count != 0 || _flushed) Invalidate("Scope ended with unflushed requests or unmatched terminal responses.");
            if (_completed == 0) Invalidate("No complete Bolt request/response exchange was observed.");
            var counts = Enum.GetValues<RequestKind>().Select(kind => new KeyValuePair<string, long>(kind.ToString(), _requestCounts[(int)kind]))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var result = new BoltProtocolMeasurement(_invalidReason is null, _invalidReason,
                _invalidReason is null ? _completed : null, _requestMessages, _flushes, _successes, _failures, _ignored, _records,
                new System.Collections.ObjectModel.ReadOnlyDictionary<string, long>(counts));
            _requests.Clear();
            _connection = null;
            return result;
        }
    }

    public bool IsDebugEnabled() { lock (_gate) return _active && _invalidReason is null; }
    public bool IsTraceEnabled() { lock (_gate) return _active && _invalidReason is null; }

    public void Debug(string message, params object[] args)
    {
        lock (_gate)
        {
            if (!_active || _invalidReason is not null) return;
            if (!TrySplit(message, out var connection, out var body))
            {
                if (message.Contains("C:", StringComparison.Ordinal) || message.Contains("S:", StringComparison.Ordinal))
                    Invalidate("Protocol debug event lacks the pinned connection prefix.");
                return; // Driver/pool informational debugging does not describe protocol traffic.
            }
            if (body.SequenceEqual("C: {0}"))
            {
                if (!Event(connection)) return;
                if (_flushed) { Invalidate("A request overlapped an incompletely drained Bolt batch."); return; }
                if (!TryRequest(args, out var kind)) { Invalidate("Unsupported request type; authentication, routing and lifecycle traffic are outside warmed observations."); return; }
                if (_requests.Count >= MaximumBatchRequests) { Invalidate("Observed Bolt batch exceeds the bounded request limit."); return; }
                _requests.Enqueue(kind);
                _requestCounts[(int)kind]++;
                _requestMessages++;
            }
            else if (body.SequenceEqual("S: {0}"))
            {
                if (!Event(connection)) return;
                if (!_flushed || _requests.Count == 0) { Invalidate("A response has no matching flushed request."); return; }
                var type = ExactMessageType(args);
                if (type == Messaging + "RecordMessage")
                {
                    if (_requests.Peek() is not (RequestKind.Pull or RequestKind.PullAll))
                    { Invalidate("A RECORD response does not match the current PULL request."); return; }
                    _records++;
                    return;
                }
                if (type == Messaging + "SuccessMessage") _successes++;
                else if (type == Messaging + "FailureMessage") _failures++;
                else if (type == Messaging + "IgnoredMessage") _ignored++;
                else { Invalidate("Unsupported response type in the pinned protocol logging contract."); return; }
                _requests.Dequeue();
                if (_requests.Count == 0) { _completed++; _flushed = false; }
            }
            else if (body.StartsWith("C:", StringComparison.Ordinal) || body.StartsWith("S:", StringComparison.Ordinal)
                || body.StartsWith("~~ [CONNECT]", StringComparison.Ordinal) || body.StartsWith("Connection '", StringComparison.Ordinal))
                Invalidate("Connection lifecycle or an unsupported protocol debug event occurred inside the warmed scope.");
        }
    }

    public void Trace(string message, params object[] args)
    {
        lock (_gate)
        {
            if (!_active || _invalidReason is not null) return;
            // Deliberately do not inspect args: the driver has already made a hex buffer which may contain secrets.
            if (!TrySplit(message, out var connection, out var body))
            { Invalidate("Trace event lacks the pinned connection prefix."); return; }
            if (!body.SequenceEqual("C: {0}"))
            { Invalidate("Unsupported trace event; handshake and unknown framing cannot be measured."); return; }
            if (!Event(connection)) return;
            if (_flushed || _requests.Count == 0)
            { Invalidate("A flush overlapped pending responses or had no observed request messages."); return; }
            _flushes++;
            _flushed = true;
        }
    }

    public void Error(Exception cause, string message, params object[] args)
    { lock (_gate) { if (_active) Invalidate("Driver error occurred during the protocol observation."); } }
    public void Warn(Exception cause, string message, params object[] args)
    { lock (_gate) { if (_active) Invalidate("Driver warning occurred during the protocol observation."); } }
    public void Info(string message, params object[] args) { }

    private bool Event(ReadOnlySpan<char> connection)
    {
        if (++_events > MaximumEvents) { Invalidate("Protocol observation exceeds the bounded event limit."); return false; }
        if (_connection is null) _connection = connection.ToString();
        else if (!connection.SequenceEqual(_connection))
        { Invalidate("More than one connection identity occurred in the observation scope."); return false; }
        return true;
    }
    private void Invalidate(string reason) => _invalidReason ??= reason;

    private static bool TrySplit(string message, out ReadOnlySpan<char> connection, out ReadOnlySpan<char> body)
    {
        connection = default; body = default;
        if (message.Length < 5 || message[0] != '[') return false;
        var end = message.IndexOf(']');
        if (end is < 2 or > 256 || end + 1 >= message.Length || message[end + 1] != ' ') return false;
        connection = message.AsSpan(1, end - 1);
        body = message.AsSpan(end + 2);
        return true;
    }
    private static string? ExactMessageType(object[] args)
    {
        if (args.Length != 1 || args[0] is null) return null;
        var type = args[0].GetType(); // Metadata only. Never invoke message.ToString or any instance member.
        return type.Assembly == typeof(IDriver).Assembly ? type.FullName : null;
    }
    private static bool TryRequest(object[] args, out RequestKind kind)
    {
        kind = ExactMessageType(args) switch
        {
            Messaging + "RunWithMetadataMessage" => RequestKind.Run,
            Messaging + "PullMessage" => RequestKind.Pull,
            Messaging + "PullAllMessage" => RequestKind.PullAll,
            Messaging + "DiscardMessage" => RequestKind.Discard,
            Messaging + "BeginMessage" => RequestKind.Begin,
            Messaging + "CommitMessage" => RequestKind.Commit,
            Messaging + "RollbackMessage" => RequestKind.Rollback,
            Messaging + "ResetMessage" => RequestKind.Reset,
            Messaging + "TelemetryMessage" => RequestKind.Telemetry,
            _ => (RequestKind)(-1)
        };
        return (int)kind >= 0;
    }
    private static BoltProtocolMeasurement InvalidWithoutScope()
        => new(false, "No active Bolt observation scope.", null, 0, 0, 0, 0, 0, 0,
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, long>(new Dictionary<string, long>()));
    private enum RequestKind { Run, Pull, PullAll, Discard, Begin, Commit, Rollback, Reset, Telemetry }
}

public sealed record BoltProtocolMeasurement(bool IsValid, string? InvalidReason, long? CompletedSerialExchanges,
    long RequestMessages, long Flushes, long SuccessResponses, long FailureResponses, long IgnoredResponses,
    long RecordResponses, IReadOnlyDictionary<string, long> RequestsByKind);
