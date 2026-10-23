using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using GraphBridge.EntityFrameworkCore.Cypher;
using GraphBridge.EntityFrameworkCore.Diagnostics;
using GraphBridge.EntityFrameworkCore.Metadata;
using GraphBridge.EntityFrameworkCore.Query;
using GraphBridge.EntityFrameworkCore.Transport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using Neo4j.Driver;

namespace GraphBridge.EntityFrameworkCore.Update;

internal sealed record GraphUpsertCommand(NodeBinding Node, string Text, IDictionary<string, object> Parameters, int Rows);

/// <summary>Typed batch upserts (ADR 0051): one constrained MERGE over at most 10,000 rows, in one transaction, untracked.</summary>
internal static class Neo4jUpsertExecutor
{
    internal const string RowsParameter = "__gb_upsert_rows";

    public static Task<GraphUpsertResult> ExecuteAsync(DbContext context, IEntityType entityType, IEnumerable<object> entities,
        LambdaExpression? updateProperties, CancellationToken cancellationToken)
        => GraphBridgeDiagnostics.OperationAsync(GraphBridgeDiagnostics.UpsertActivityName,
            () => ExecuteCoreAsync(context, entityType, entities, updateProperties, cancellationToken));

    /// <summary>Validates the entity type and every row, and renders the statement, before anything is sent.</summary>
    internal static GraphUpsertCommand Prepare(GraphBindingCompiler compiler, IEntityType entityType, IEnumerable<object> entities,
        LambdaExpression? updateProperties)
    {
        if (compiler.GetStorageKind(entityType) != GraphStorageKind.Node)
            throw new NotSupportedException($"Upserts support node entities only; '{entityType.DisplayName()}' is stored as a relationship.");
        var node = compiler.GetNode(entityType);
        if (entityType.GetForeignKeys().Any(GraphModelValidation.IsNodeReference))
            throw new NotSupportedException($"Upserts don't support '{entityType.DisplayName()}': its native-reference foreign keys are stored as relationships, which an upsert would not write.");
        if (node.StoredProperties.FirstOrDefault(p => p.Property.IsConcurrencyToken) is { } token)
            throw new NotSupportedException($"Upserts don't support concurrency tokens yet ('{entityType.DisplayName()}.{token.Property.Name}').");
        if (node.StoredProperties.FirstOrDefault(p => p.Property.IsShadowProperty()) is { } shadow)
            throw new NotSupportedException($"Upserts read values from the entities passed in; shadow property '{entityType.DisplayName()}.{shadow.Property.Name}' has none.");
        var keys = node.KeyProperties.Select(key => node.StoredProperties.Single(p => p.Property == key)).ToArray();
        var update = UpdateSet(node, entityType, updateProperties);

        var rows = new List<Dictionary<string, object?>>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entity in entities)
        {
            if (entity is null) throw new ArgumentException("Upsert entities cannot be null.", nameof(entities));
            if (rows.Count == Neo4jBulkExpression.MaxRows)
                throw new InvalidOperationException($"Upserts support at most {Neo4jBulkExpression.MaxRows} entities per atomic call; nothing was sent. Split the input into several calls.");
            var key = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in keys)
            {
                var value = Value(property, entity);
                if (Equals(value, property.Property.Sentinel))
                    throw new InvalidOperationException($"Upsert key '{entityType.DisplayName()}.{property.Property.Name}' has its default value. Upserts need client-supplied keys.");
                key[property.Name] = property.ToStore(value);
            }
            // Positions only: key values stay out of exception messages and logs.
            if (!seen.TryAdd(Identity(key), rows.Count))
                throw new InvalidOperationException($"Entities {seen[Identity(key)]} and {rows.Count} (zero-based) in one upsert have the same key; nothing was sent. Remove the duplicate: an upsert never chooses between them.");
            var create = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in node.StoredProperties)
                if (property.ToStore(Value(property, entity)) is { } stored)
                    create[property.Name] = stored;
            var row = new Dictionary<string, object?>(StringComparer.Ordinal) { ["key"] = key, ["create"] = create };
            if (update.Count != 0)
            {
                // A null removes the property, as an EF null does on update.
                var changes = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var property in update) changes[property.Name] = property.ToStore(Value(property, entity));
                row["update"] = changes;
            }
            rows.Add(row);
        }

        var text = new StringBuilder("UNWIND $").Append(RowsParameter).Append(" AS row\nMERGE (n:").Append(CypherQueryRenderer.Identifier(node.Label))
            .Append(" {").Append(string.Join(", ", keys.Select(k => CypherQueryRenderer.Identifier(k.Name) + ": row.key." + CypherQueryRenderer.Identifier(k.Name))))
            .Append("})\nON CREATE SET n += row.create");
        if (update.Count != 0) text.Append("\nON MATCH SET n += row.update");
        text.Append("\nRETURN count(n) AS affected");
        return new(node, text.ToString(), new Dictionary<string, object> { [RowsParameter] = rows.ToArray() }, rows.Count);
    }

    private static async Task<GraphUpsertResult> ExecuteCoreAsync(DbContext context, IEntityType entityType, IEnumerable<object> entities,
        LambdaExpression? updateProperties, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var command = Prepare(context.GetService<GraphBindingCompiler>(), entityType, entities, updateProperties);
        if (command.Rows == 0) return new(0, 0, GraphWriteCounters.None);
        using var concurrency = context.GetService<IConcurrencyDetector>().EnterCriticalSection();
        var manager = context.GetService<Neo4jTransactionManager>();
        using var operation = manager.EnterNativeOperation();
        Neo4jTransactionManager.RejectAmbientTransaction();
        var logger = context.GetService<IDiagnosticsLogger<DbLoggerCategory.Database.Command>>().Logger;
        var ownsTransaction = manager.CurrentTransaction is null;
        var transaction = manager.CurrentTransaction ?? await manager.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var commitStarted = false;
        try
        {
            var runner = manager.CurrentDriverTransaction!;
            const string preflight = "SHOW CONSTRAINTS YIELD type, entityType, labelsOrTypes, properties RETURN type, entityType, labelsOrTypes, properties";
            var schema = await GraphBridgeDiagnostics.RunAsync(runner, preflight, null, schema: true, kind: GraphCommandKind.SchemaPreflight).ConfigureAwait(false);
            var constraints = new List<GraphSchemaConstraint>();
            while (await schema.FetchAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (constraints.Count == 10_000) throw new InvalidOperationException("Upsert schema preflight exceeded 10000 rows.");
                var record = schema.Current;
                constraints.Add(new(record["entityType"].As<string>(), record["type"].As<string>(),
                    record["labelsOrTypes"].As<List<string>>(), record["properties"].As<List<string>>()));
            }
            GraphKeyConstraintValidator.Validate([command.Node], constraints);
            cancellationToken.ThrowIfCancellationRequested();

            logger.LogInformation("Executing GraphBridge upsert Cypher: {Cypher}", command.Text);
            var cursor = await GraphBridgeDiagnostics.RunAsync(runner, command.Text, command.Parameters, kind: GraphCommandKind.Mutation).ConfigureAwait(false);
            if (!await cursor.FetchAsync().ConfigureAwait(false) || cursor.Current["affected"].As<long>() != command.Rows
                || await cursor.FetchAsync().ConfigureAwait(false))
                throw new DbUpdateException("The upsert did not affect exactly one node per entity. The transaction was aborted.");
            // ADR 0050: the summary arrived with the last record; its counters give the created count without a round trip.
            var summary = await cursor.ConsumeAsync().ConfigureAwait(false);
            var counters = summary?.Counters is { } server ? GraphWriteCounters.From(server) : GraphWriteCounters.None;
            cancellationToken.ThrowIfCancellationRequested();
            if (ownsTransaction)
            {
                commitStarted = true;
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            return new(counters.NodesCreated, command.Rows - counters.NodesCreated, counters);
        }
        catch (Exception error)
        {
            try { await transaction.DisposeAsync().ConfigureAwait(false); }
            catch { /* Preserve the primary failure after attempting full transaction cleanup. */ }
            if (error is Neo4jException)
                throw new DbUpdateException(transaction is Neo4jContextTransaction { IsCommitted: true }
                    ? "The upsert transaction committed, but finalization failed."
                    : commitStarted ? "The upsert commit failed and its outcome is unknown. Repeating the same upsert is safe: it writes the same values."
                    : "Neo4j rejected the upsert; rollback and resource cleanup were attempted.", error);
            throw;
        }
        finally
        {
            if (ownsTransaction) await transaction.DisposeAsync().ConfigureAwait(false);
        }
    }

    // The properties an existing node receives: every stored non-key property, or those the selector names.
    private static IReadOnlyList<GraphPropertyBinding> UpdateSet(NodeBinding node, IEntityType entityType, LambdaExpression? updateProperties)
    {
        var updatable = node.StoredProperties.Where(p => !node.KeyProperties.Contains(p.Property)).ToArray();
        if (updateProperties is null) return updatable;
        var body = Unwrap(updateProperties.Body);
        IEnumerable<Expression> members = body switch
        {
            NewExpression created when created.Type.Name.Contains("AnonymousType", StringComparison.Ordinal) => created.Arguments,
            MemberExpression => [body],
            _ => throw Refused()
        };
        var selected = new List<GraphPropertyBinding>();
        foreach (var argument in members)
        {
            if (Unwrap(argument) is not MemberExpression { Expression: ParameterExpression parameter, Member: var member }
                || parameter != updateProperties.Parameters[0])
                throw Refused();
            if (node.KeyProperties.Any(k => k.Name == member.Name))
                throw new ArgumentException($"'{entityType.DisplayName()}.{member.Name}' is part of the key, which identifies the node and is never updated.", nameof(updateProperties));
            var property = updatable.FirstOrDefault(p => p.Property.Name == member.Name)
                ?? throw new ArgumentException($"'{entityType.DisplayName()}.{member.Name}' is not a stored property an upsert can update.", nameof(updateProperties));
            if (!selected.Contains(property)) selected.Add(property);
        }
        return selected;

        static Expression Unwrap(Expression expression)
            => expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert ? convert.Operand : expression;

        ArgumentException Refused() => new("updateProperties must name properties of the entity, as 'p => p.Name' or 'p => new { p.Name, p.Email }'.", nameof(updateProperties));
    }

    private static object? Value(GraphPropertyBinding property, object entity)
        => property.Property.GetGetter().GetClrValueUsingContainingEntity(entity);

    // Collision-free text for a key map: each value is length-prefixed with its type.
    private static string Identity(Dictionary<string, object?> key)
    {
        var text = new StringBuilder();
        foreach (var value in key.Values)
        {
            var part = value is null ? "null" : value.GetType().FullName + "=" + Convert.ToString(value, CultureInfo.InvariantCulture);
            text.Append(part.Length).Append(':').Append(part);
        }
        return text.ToString();
    }

}
