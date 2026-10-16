using System.Collections;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.EntityFrameworkCore.Diagnostics;

internal enum GraphCommandKind { Linq, SchemaPreflight, Mutation, Native, Explain, Migration, Schema, Connectivity }

/// <summary>Names for opt-in GraphBridge tracing. No exporter is installed by the provider.</summary>
public static class GraphBridgeDiagnostics
{
    public const string ActivitySourceName = "GraphBridge.EntityFrameworkCore";
    public const string QueryActivityName = "GraphBridge.Query";
    public const string SaveChangesActivityName = "GraphBridge.SaveChanges";
    public const string BulkActivityName = "GraphBridge.Bulk";
    public const string NativeQueryActivityName = "GraphBridge.NativeQuery";
    public const string SearchActivityName = "GraphBridge.Search";
    public const string PathActivityName = "GraphBridge.Path";
    public const string ExplainActivityName = "GraphBridge.Explain";
    public const string SchemaActivityName = "GraphBridge.SchemaInspect";
    public const string MigrationActivityName = "GraphBridge.Migration";
    public const string ConnectivityActivityName = "GraphBridge.CanConnect";
    public const string BeginTransactionActivityName = "GraphBridge.Transaction.Begin";
    public const string CommitActivityName = "GraphBridge.Transaction.Commit";
    public const string RollbackActivityName = "GraphBridge.Transaction.Rollback";
    public const string StatementActivityName = "GraphBridge.Statement";
    public const string CommandActivityName = "GraphBridge.Command.Run";
    public const string CommandStartingEventName = "GraphBridge.Command.Starting";
    public const string CommandSubmittedEventName = "GraphBridge.Command.Submitted";
    public const string CommandCompletedEventName = "GraphBridge.Command.Completed";
    public const string CommandFailedEventName = "GraphBridge.Command.Failed";
    private static readonly ActivitySource Source = new(ActivitySourceName);

    internal static Activity? StartOperation(string name) => Source.StartActivity(name, ActivityKind.Internal);

    internal static async Task<T> OperationAsync<T>(string name, Func<Task<T>> operation)
    {
        using var activity = StartOperation(name);
        try { return await operation().ConfigureAwait(false); }
        catch (Exception error) { Fail(activity, error); throw; }
    }

    internal static async Task<T> DriverCallAsync<T>(string name, Func<Task<T>> operation)
    {
        using var activity = Source.StartActivity(name, ActivityKind.Client);
        if (activity?.IsAllDataRequested == true) activity.SetTag("db.system.name", "neo4j");
        try
        {
            var result = await operation().ConfigureAwait(false);
            if (name == CommitActivityName && activity?.IsAllDataRequested == true)
                activity.SetTag("graphbridge.commit.outcome", "acknowledged");
            return result;
        }
        catch (Exception error)
        {
            Fail(activity, error, driver: true);
            // A failed await does not prove whether the server committed. Never suggest a retry.
            if (name == CommitActivityName && activity?.IsAllDataRequested == true)
                activity.SetTag("graphbridge.commit.outcome", "unconfirmed");
            throw;
        }
    }

    internal static Task DriverCallAsync(string name, Func<Task> operation)
        => DriverCallAsync(name, async () => { await operation().ConfigureAwait(false); return true; });

    internal static void Fail(Activity? activity, Exception error, bool driver = false)
    {
        // Cleanup can fail after an operation failure. Keep the primary error category.
        if (activity is null || activity.Status == ActivityStatusCode.Error) return;
        var cause = error is DbUpdateException { InnerException: { } inner } ? inner : error;
        activity.SetStatus(ActivityStatusCode.Error);
        if (activity.IsAllDataRequested)
            activity.SetTag("error.type", error is DbUpdateConcurrencyException ? "concurrency" : cause switch
            {
                OperationCanceledException => "canceled", TransientException => "transient",
                Neo4jException => "driver_error", _ => driver ? "driver_error" : "operation_error"
            });
    }

    internal static void FailCursor(IResultCursor? cursor, Exception error)
    {
        if (cursor is TracedResultCursor traced) traced.Fail(error);
    }

    internal static void CompleteCursor(IResultCursor? cursor)
    {
        if (cursor is TracedResultCursor traced) traced.Complete();
    }

    // Run is submission latency. Its statement parent lasts through cursor completion,
    // including consumer pauses; neither duration claims pure server execution time.
    internal static async Task<IResultCursor> RunAsync(
        IAsyncQueryRunner runner, string command, IDictionary<string, object>? parameters, bool schema = false,
        GraphCommandKind kind = GraphCommandKind.Linq)
    {
        if (schema) kind = GraphCommandKind.SchemaPreflight;
        var commandKind = kind switch
        {
            GraphCommandKind.Linq => "linq", GraphCommandKind.SchemaPreflight => "schema_preflight",
            GraphCommandKind.Mutation => "mutation", GraphCommandKind.Native => "native",
            GraphCommandKind.Explain => "explain", GraphCommandKind.Migration => "migration",
            GraphCommandKind.Schema => "schema", GraphCommandKind.Connectivity => "connectivity",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var statement = Source.StartActivity(StatementActivityName, ActivityKind.Internal);
        try
        {
            if (statement?.IsAllDataRequested == true)
            {
                statement.SetTag("db.system.name", "neo4j");
                statement.SetTag("graphbridge.command.kind", commandKind);
                statement.SetTag("graphbridge.parameter.count", parameters?.Count ?? 0);
                // Caller-authored text may contain sensitive literals: never fingerprint it.
                if (kind is not (GraphCommandKind.Native or GraphCommandKind.Explain or GraphCommandKind.Migration))
                    statement.SetTag("graphbridge.statement.fingerprint", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(command))));
                if (parameters is not null)
                {
                    var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (var value in parameters.Values)
                    {
                        var category = value switch
                        {
                            null => "null", bool => "boolean", string => "string",
                            byte or sbyte or short or ushort or int or uint or long or ulong => "integer",
                            float or double or decimal => "floating", IDictionary => "map", IEnumerable => "collection", _ => "other"
                        };
                        counts[category] = counts.GetValueOrDefault(category) + 1;
                    }
                    foreach (var pair in counts) statement.SetTag("graphbridge.parameter." + pair.Key + ".count", pair.Value);
                }
                statement.AddEvent(new(CommandStartingEventName));
            }
            IResultCursor cursor;
            using (var activity = Source.StartActivity(CommandActivityName, ActivityKind.Client))
            {
                if (activity?.IsAllDataRequested == true)
                {
                    activity.SetTag("db.system.name", "neo4j");
                    activity.SetTag("graphbridge.command.kind", commandKind);
                    activity.SetTag("graphbridge.command.stage", "run");
                }
                try
                {
                    cursor = parameters is null ? await runner.RunAsync(command).ConfigureAwait(false)
                        : await runner.RunAsync(command, parameters).ConfigureAwait(false);
                }
                catch (Exception error) { Fail(activity, error, driver: true); throw; }
            }
            if (statement is null) return cursor;
            if (statement.IsAllDataRequested) statement.AddEvent(new(CommandSubmittedEventName));
            return new TracedResultCursor(cursor, statement);
        }
        catch (Exception error)
        {
            Fail(statement, error, driver: true);
            if (statement?.IsAllDataRequested == true) statement.AddEvent(new(CommandFailedEventName));
            statement?.Dispose();
            throw;
        }
    }
}
