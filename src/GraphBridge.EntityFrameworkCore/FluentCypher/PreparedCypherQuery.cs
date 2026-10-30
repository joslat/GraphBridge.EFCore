using Microsoft.EntityFrameworkCore;

namespace GraphBridge.EntityFrameworkCore.FluentCypher;

/// <summary>A context-bound compiled command and its matching untracked result reader.</summary>
/// <remarks>Preparation performs no I/O. This object retains its original context and parameter snapshot; it is not a
/// cross-context cache or thread-safe execution handle. The caller owns context/driver lifetime and must not mutate
/// objects held in parameter values. Command parameters are sensitive, just as with ToCommand().</remarks>
public sealed class PreparedCypherQuery<TResult>
{
    private readonly DbContext context;
    private readonly CompiledCypher<TResult> compiled;
    private readonly TimeSpan? timeout;
    internal PreparedCypherQuery(DbContext context, CompiledCypher<TResult> compiled, int maxRows, TimeSpan? timeout = null)
        => (this.context, this.compiled, MaxRows, this.timeout) = (context, compiled, maxRows, timeout);
    public CypherCommand Command => compiled.Command;
    public int MaxRows { get; }

    /// <summary>Executes this exact compiled command/reader through the original context's native transaction pipeline.</summary>
    public Task<IReadOnlyList<TResult>> ToListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Neo4jDatabaseFacadeExtensions.QueryCypherWithTimeoutAsync(context.Database, Command.Text, Command.Parameters,
            record => compiled.Read(record.Values), MaxRows, timeout, cancellationToken, compiled.Preflight);
    }
}
