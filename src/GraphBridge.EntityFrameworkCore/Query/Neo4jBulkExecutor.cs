using System.Collections;
using GraphBridge.EntityFrameworkCore.Cypher;
using GraphBridge.EntityFrameworkCore.Diagnostics;
using GraphBridge.EntityFrameworkCore.Metadata;
using GraphBridge.EntityFrameworkCore.Transport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Neo4j.Driver;

namespace GraphBridge.EntityFrameworkCore.Query;

/// <summary>Bounded untracked batches. Physical IDs are transaction-local locators, never EF identity.</summary>
internal static class Neo4jBulkExecutor
{
    public static Task<int> ExecuteAsync(Neo4jQueryContext context, Neo4jBulkExpression bulk, IReadOnlyList<string> tags)
        => GraphBridgeDiagnostics.OperationAsync(GraphBridgeDiagnostics.BulkActivityName, () => ExecuteCoreAsync(context, bulk, tags));

    private static async Task<int> ExecuteCoreAsync(Neo4jQueryContext context, Neo4jBulkExpression bulk, IReadOnlyList<string> tags)
    {
        using var concurrency = context.ConcurrencyDetector.EnterCriticalSection();
        using var operation = context.Transactions.EnterNativeOperation();
        var cancellation = context.CancellationToken;
        cancellation.ThrowIfCancellationRequested();
        Neo4jTransactionManager.RejectAmbientTransaction();
        var parameters = bulk.Source.Parameters.ToDictionary(p => p.Name, p => p.Resolve(context.Parameters)!);
        // Null predicates are valid, but setting a required property to null is not.
        foreach (var assignment in bulk.Assignments ?? [])
            if (assignment.Kind == Neo4jBulkValueKind.Constant && parameters[assignment.Parameter!] is null)
                assignment.Target.ModelProperty.ToStore(null);
        var limitName = bulk.Source.Plan.LimitParameter ?? "__gb_bulk_limit";
        parameters[limitName] = Math.Min(bulk.Source.Plan.LimitParameter is null ? long.MaxValue
            : Convert.ToInt64(parameters[limitName]), Neo4jBulkExpression.MaxRows + 1L);
        var selection = CypherQueryRenderer.RenderBulkSelection(bulk.Source.Plan with { LimitParameter = limitName });
        var ownsTransaction = context.Transactions.CurrentTransaction is null;
        var transaction = context.Transactions.CurrentTransaction
            ?? await context.Transactions.BeginTransactionAsync(cancellation).ConfigureAwait(false);
        var commitStarted = false;
        try
        {
            var runner = context.Transactions.CurrentDriverTransaction!;
            var schema = await Run("SHOW CONSTRAINTS YIELD type, entityType, labelsOrTypes, properties RETURN type, entityType, labelsOrTypes, properties",
                null, GraphCommandKind.SchemaPreflight).ConfigureAwait(false);
            var constraints = new List<GraphSchemaConstraint>();
            while (await schema.FetchAsync().ConfigureAwait(false))
            {
                cancellation.ThrowIfCancellationRequested();
                if (constraints.Count == 10_000) throw new InvalidOperationException("Bulk schema preflight exceeded 10000 rows.");
                var record = schema.Current;
                constraints.Add(new(record["entityType"].As<string>(), record["type"].As<string>(),
                    record["labelsOrTypes"].As<List<string>>(), record["properties"].As<List<string>>()));
            }
            cancellation.ThrowIfCancellationRequested();
            GraphKeyConstraintValidator.Validate(bulk.RequiredNodes, constraints);
            GraphKeyConstraintValidator.ValidateIdentifiedEdges(bulk.Source.Binding.RequiredUniqueIdentifiedEdges, constraints);
            var selected = await Read(await Run(selection, parameters, GraphCommandKind.Linq).ConfigureAwait(false)).ConfigureAwait(false);
            if (selected.Count != 0)
            {
                // Acquire locks in physical-ID order. Re-read after the lock, and abort on any mapped-value
                // difference. No stale selection or changed predicate is silently updated or omitted.
                var identityRows = selected.Keys.Order(StringComparer.Ordinal).Select(id => new Dictionary<string, object> { ["id"] = id }).ToArray();
                var rowParameters = new Dictionary<string, object> { ["__gb_bulk_rows"] = identityRows };
                var root = bulk.Source.Plan.Root;
                var element = CypherQueryRenderer.ElementAlias(root);
                var match = $"UNWIND $__gb_bulk_rows AS row\nWITH row ORDER BY row.id\n{CypherQueryRenderer.RenderRootMatch(root)} WHERE elementId({element}) = row.id";
                Dictionary<string, Snapshot> locked;
                if (root is CypherNodeRoot)
                {
                    var columns = string.Join(", ", bulk.Source.Binding.Properties.Select(p =>
                        $"n.{CypherQueryRenderer.Identifier(p.ModelProperty.Name)} AS {CypherQueryRenderer.Identifier(p.Column.Alias)}"));
                    locked = await Read(await Run($"{match}\n{CypherMutationRenderer.LockWrite("n")}\nRETURN elementId(n) AS {Neo4jBulkExpression.IdentityAlias}, {columns}",
                        rowParameters, GraphCommandKind.Mutation).ConfigureAwait(false)).ConfigureAwait(false);
                }
                else
                {
                    // Preserve all graph identity/membership diagnostics in a second bounded read after
                    // locks. Endpoint/payload projections alone cannot establish relationship validity.
                    var locks = await Run($"{match}\n{LockClause(bulk)}\nRETURN count({element}) AS locked",
                        rowParameters, GraphCommandKind.Mutation).ConfigureAwait(false);
                    await RequireCount(locks, "locked", selected.Count).ConfigureAwait(false);
                    var recheck = bulk.Source.Binding.CreatePlan() with
                    {
                        Predicate = new CypherPhysicalIdentitySet("__gb_bulk_ids"), LimitParameter = "__gb_bulk_limit"
                    };
                    locked = await Read(await Run(CypherQueryRenderer.RenderBulkSelection(recheck),
                        new Dictionary<string, object> { ["__gb_bulk_ids"] = selected.Keys.ToArray(),
                            ["__gb_bulk_limit"] = Neo4jBulkExpression.MaxRows + 1L }, GraphCommandKind.Linq).ConfigureAwait(false)).ConfigureAwait(false);
                }
                if (locked.Count != selected.Count || locked.Any(pair => !selected.TryGetValue(pair.Key, out var original)
                    || bulk.Source.Binding.Properties.Any(p => !SameStoreValue(original.Store[p.Column.Alias], pair.Value.Store[p.Column.Alias]))))
                    throw new DbUpdateConcurrencyException("Bulk candidates changed after selection. The transaction was aborted; re-evaluate explicitly.");

                var mutationRows = locked.OrderBy(p => p.Key, StringComparer.Ordinal).Select(pair =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    return new Dictionary<string, object?> { ["id"] = pair.Key,
                        ["changes"] = bulk.Assignments is null ? null : Changes(bulk, pair.Value.Values, parameters) };
                }).ToArray();
                var mutation = bulk.Assignments is not null ? $"SET {element} += row.changes"
                    : root is CypherNodeReferenceRoot reference
                        ? $"OPTIONAL MATCH ()-[__gb_link:{CypherQueryRenderer.Identifier(reference.RelationshipType)}]->(n)\nDELETE __gb_link, n"
                        : $"DELETE {element}";
                var cursor = await Run($"{match}\n{mutation}\nRETURN count({element}) AS affected",
                    new Dictionary<string, object> { ["__gb_bulk_rows"] = mutationRows }, GraphCommandKind.Mutation).ConfigureAwait(false);
                await RequireCount(cursor, "affected", selected.Count).ConfigureAwait(false);
                // ADR 0050: only the mutation's summary is consumed, so the operation's counters exclude the no-op lock writes.
                await cursor.ConsumeAsync().ConfigureAwait(false);
            }
            cancellation.ThrowIfCancellationRequested();
            if (ownsTransaction)
            {
                commitStarted = true;
                await transaction.CommitAsync(cancellation).ConfigureAwait(false);
            }
            return selected.Count;

            async Task RequireCount(IResultCursor cursor, string name, int expected)
            {
                if (!await cursor.FetchAsync().ConfigureAwait(false)) throw new InvalidOperationException("Bulk operation returned no count.");
                cancellation.ThrowIfCancellationRequested();
                var actual = cursor.Current[name].As<long>();
                if (await cursor.FetchAsync().ConfigureAwait(false) || actual != expected)
                    throw new DbUpdateConcurrencyException("Bulk operation did not affect exactly the selected candidates. The transaction was aborted.");
                cancellation.ThrowIfCancellationRequested();
            }

            async Task<IResultCursor> Run(string command, IDictionary<string, object>? values, GraphCommandKind kind)
            {
                cancellation.ThrowIfCancellationRequested();
                // ADR 0050: the query's tags mark the statements it causes, not the internal schema preflight.
                if (kind != GraphCommandKind.SchemaPreflight) command = CypherComments.Prepend(tags, command);
                context.CommandLogger.Logger.LogInformation("Executing GraphBridge bulk Cypher: {Cypher}", command);
                var cursor = await GraphBridgeDiagnostics.RunAsync(runner, command, values,
                    schema: kind == GraphCommandKind.SchemaPreflight, kind: kind).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                return cursor;
            }

            async Task<Dictionary<string, Snapshot>> Read(IResultCursor cursor)
            {
                var rows = new Dictionary<string, Snapshot>(StringComparer.Ordinal);
                try
                {
                    while (await cursor.FetchAsync().ConfigureAwait(false))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (rows.Count == Neo4jBulkExpression.MaxRows)
                            throw new InvalidOperationException("Bulk mutations support at most 10000 selected rows per atomic call. Use explicit bounded query batches; no rows were committed.");
                        var record = cursor.Current;
                        var store = record.Values.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.Ordinal);
                        var values = bulk.Source.Binding.ReadRow(store);
                        if (!rows.TryAdd(record[Neo4jBulkExpression.IdentityAlias].As<string>(), new(store, values)))
                            throw new InvalidOperationException("Bulk selection returned a duplicate physical candidate.");
                    }
                    cancellation.ThrowIfCancellationRequested();
                    return rows;
                }
                catch (Exception error) { GraphBridgeDiagnostics.FailCursor(cursor, error); throw; }
            }
        }
        catch (Exception error)
        {
            try { await transaction.DisposeAsync().ConfigureAwait(false); }
            catch { /* Preserve primary failure after attempting full transaction cleanup. */ }
            if (error is Neo4jException)
                throw new DbUpdateException(transaction is Neo4jContextTransaction { IsCommitted: true }
                    ? "The bulk transaction committed, but finalization failed. Do not retry automatically."
                    : commitStarted ? "Bulk commit failed; the commit outcome may be unknown. Do not retry automatically."
                    : "Neo4j rejected the bulk mutation; rollback and resource cleanup were attempted.", error);
            throw;
        }
        finally
        {
            if (ownsTransaction) await transaction.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Two reads of one element: a list (a vector, ADR 0054) arrives as a new instance each time, so its items are compared.
    internal static bool SameStoreValue(object? left, object? right)
        => left is IList first && right is IList second
            ? first.Count == second.Count && Enumerable.Range(0, first.Count).All(i => SameStoreValue(first[i], second[i]))
            : Equals(left, right);

    private static string LockClause(Neo4jBulkExpression bulk)
    {
        if (bulk.Target.Reference is { } reference)
            return $"{CypherMutationRenderer.LockWrite("n")}\nWITH row, n\nOPTIONAL MATCH (s:{CypherQueryRenderer.Labels(reference.Principal.Labels)})-[:{CypherQueryRenderer.Identifier(reference.RelationshipType)}]->(n)"
                + $"\nFOREACH (parent IN CASE WHEN s IS NULL THEN [] ELSE [s] END | {CypherMutationRenderer.LockWrite("parent")})";
        // A relationship with its own key or a required payload property is locked too, as the endpoints are.
        return bulk.Target.RelationshipLock is null ? CypherMutationRenderer.LockWrite("s", "t") : CypherMutationRenderer.LockWrite("s", "t", "r");
    }

    private static Dictionary<string, object?> Changes(Neo4jBulkExpression bulk, ValueBuffer row, IReadOnlyDictionary<string, object> parameters)
    {
        var changes = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var assignment in bulk.Assignments!)
        {
            object? store;
            if (assignment.Kind == Neo4jBulkValueKind.Constant) store = parameters[assignment.Parameter!];
            else
            {
                var model = row[assignment.Source!.Index];
                if (assignment.Kind is Neo4jBulkValueKind.Add or Neo4jBulkValueKind.Subtract)
                {
                    var delta = parameters[assignment.Parameter!];
                    if (model is null || delta is null) model = null;
                    else
                    {
                        var initial = Convert.ToInt64(model);
                        var number = Convert.ToInt64(delta);
                        var result = assignment.Kind == Neo4jBulkValueKind.Add ? checked(initial + number) : checked(initial - number);
                        var type = Nullable.GetUnderlyingType(assignment.Target.ModelProperty.Property.ClrType)
                            ?? assignment.Target.ModelProperty.Property.ClrType;
                        model = type == typeof(int) ? (object)checked((int)result) : result;
                    }
                }
                store = assignment.Target.ModelProperty.ToStore(model);
            }
            changes.Add(assignment.Target.ModelProperty.Name, store);
        }
        foreach (var property in bulk.Source.Binding.Properties.Where(p => p.ModelProperty.Property.IsConcurrencyToken
            && p.ModelProperty.Property.ValueGenerated == ValueGenerated.OnAddOrUpdate))
            changes.Add(property.ModelProperty.Name, checked((long)row[property.Index]! + 1));
        return changes;
    }

    private sealed record Snapshot(IReadOnlyDictionary<string, object?> Store, ValueBuffer Values);
}
