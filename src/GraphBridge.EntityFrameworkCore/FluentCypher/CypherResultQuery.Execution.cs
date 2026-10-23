using Microsoft.EntityFrameworkCore;

namespace GraphBridge.EntityFrameworkCore.FluentCypher;

public sealed partial class CypherResultQuery<TResult>
{
    /// <summary>Renders parameterized Cypher without opening a driver session or exposing parameter values.</summary>
    public string ToQueryString() => Compile().Command.Text;

    /// <summary>Returns an explicit command snapshot. Its parameters may contain sensitive application data.</summary>
    public CypherCommand ToCommand() => Compile().Command;

    /// <summary>Compiles once with a typed pattern/clause snapshot; performs no I/O.</summary>
    /// <remarks>Like ToCommand, this deliberately exposes sensitive parameters and mapped names. Snapshot source
    /// lines refer to the returned command; incomplete snapshots explicitly omit all diagram clauses.</remarks>
    public CypherCommand Inspect() => Compile(captureSemantics: true).Command;

    /// <summary>Compiles once for explicit inspection and later execution with the same command, parameters and reader.</summary>
    /// <remarks>Requires an explicit LIMIT. Owns no transport or context lifetime; results remain untracked.</remarks>
    public PreparedCypherQuery<TResult> Prepare() => Prepare(captureSemantics: false);

    /// <summary>Optionally retains a typed semantics snapshot with the exact prepared command/reader.</summary>
    public PreparedCypherQuery<TResult> Prepare(bool captureSemantics)
    {
        if (LimitCount is not { } limit)
            throw new InvalidOperationException("Specify Limit(1..10000) before preparing an executable fluent Cypher query.");
        return new(Query.Context, Compile(captureSemantics), limit, Timeout);
    }

    /// <summary>Buffers at most the explicit LIMIT using the context's native transaction and diagnostics pipeline.</summary>
    public Task<IReadOnlyList<TResult>> ToListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (LimitCount is not { } limit)
            throw new InvalidOperationException("Specify Limit(1..10000) before executing a fluent Cypher query.");
        var compiled = Compile();
        return Neo4jDatabaseFacadeExtensions.QueryCypherWithTimeoutAsync(Query.Context.Database, compiled.Command.Text,
            compiled.Command.Parameters, record => compiled.Read(record.Values), limit, Timeout, cancellationToken);
    }
    internal CompiledCypher<TResult> Compile(bool captureSemantics = false) => new CypherCompiler(Query.Context).Compile(this, captureSemantics);
}
