using GraphBridge.EntityFrameworkCore.Cypher;
using GraphBridge.EntityFrameworkCore.Diagnostics;
using GraphBridge.EntityFrameworkCore.Metadata;
using GraphBridge.EntityFrameworkCore.Transport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;
using Neo4j.Driver;

namespace GraphBridge.EntityFrameworkCore.Update;

internal sealed class Neo4jDatabase(DatabaseDependencies dependencies, GraphBindingCompiler bindings,
    Neo4jTransactionManager transactions) : Database(dependencies)
{
    public override int SaveChanges(IList<IUpdateEntry> entries)
        => throw new NotSupportedException("Synchronous Neo4j persistence is unsupported. Use SaveChangesAsync.");

    public override Task<int> SaveChangesAsync(IList<IUpdateEntry> entries, CancellationToken cancellationToken = default)
        => GraphBridgeDiagnostics.OperationAsync(GraphBridgeDiagnostics.SaveChangesActivityName,
            () => SaveChangesCoreAsync(entries, cancellationToken));

    private async Task<int> SaveChangesCoreAsync(IList<IUpdateEntry> entries, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Neo4jTransactionManager.RejectAmbientTransaction();
        GraphMutationPlan plan;
        try { plan = new GraphMutationPlanner(bindings).Plan(entries); }
        catch
        {
            if (transactions.CurrentTransaction is { } active)
            {
                try { await active.DisposeAsync().ConfigureAwait(false); }
                catch { /* Preserve validation failure while aborting the explicit transaction. */ }
            }
            throw;
        }
        if (plan.ExpectedStateEntries == 0)
            return 0;
        var ownsTransaction = transactions.CurrentTransaction is null;
        var contextTransaction = transactions.CurrentTransaction
            ?? await transactions.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var commitStarted = false;
        try
        {
            var transaction = transactions.CurrentDriverTransaction!;
            await VerifyConstraintsAsync(transaction, plan).ConfigureAwait(false);
            foreach (var mutation in plan.Operations)
            {
                foreach (var command in mutation.LockCommands)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await RunMutationAsync(transaction, command, mutation.Entry).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                await RunMutationAsync(transaction, mutation.Command, mutation.Entry).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (ownsTransaction)
            {
                commitStarted = true;
                await contextTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            foreach (var generated in plan.GeneratedValues)
                generated.Entry.SetStoreGeneratedValue(generated.Property, generated.Value);
            return plan.ExpectedStateEntries;
        }
        catch (Exception error)
        {
            try { await contextTransaction.DisposeAsync().ConfigureAwait(false); }
            catch { /* Keep the original save failure after attempting full transaction cleanup. */ }
            if (error is Neo4jException)
            {
                var message = contextTransaction is Neo4jContextTransaction { IsCommitted: true }
                    ? "The Neo4j transaction committed, but finalization failed. Do not retry automatically."
                    : commitStarted
                        ? "Neo4j commit failed; the commit outcome may be unknown. Do not retry automatically."
                        : "Neo4j rejected the tracked graph mutation; rollback and resource cleanup were attempted.";
                throw new DbUpdateException(message, error, plan.Operations.Select(p => p.Entry)
                    .Distinct<IUpdateEntry>(ReferenceEqualityComparer.Instance).ToArray());
            }
            throw;
        }
        finally
        {
            if (ownsTransaction)
                await contextTransaction.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task RunMutationAsync(IAsyncTransaction transaction, CypherMutationCommand command, IUpdateEntry entry)
    {
        var cursor = await GraphBridgeDiagnostics.RunAsync(transaction, command.Text, command.Parameters, kind: GraphCommandKind.Mutation).ConfigureAwait(false);
        var records = await cursor.ToListAsync().ConfigureAwait(false);
        // ADR 0050: the summary arrived with the last record; consuming it records the write counters without a round trip.
        await cursor.ConsumeAsync().ConfigureAwait(false);
        if (records.Count != 1 || records[0]["count"].As<long>() != 1)
            throw new DbUpdateConcurrencyException("The graph mutation did not affect exactly one graph element; an endpoint, application key, original concurrency token, or association cardinality is missing, changed, or ambiguous.", new[] { entry });
    }

    private static async Task VerifyConstraintsAsync(IAsyncTransaction transaction, GraphMutationPlan plan)
    {
        var cursor = await GraphBridgeDiagnostics.RunAsync(transaction,
            "SHOW CONSTRAINTS YIELD type, entityType, labelsOrTypes, properties RETURN type, entityType, labelsOrTypes, properties", null, schema: true).ConfigureAwait(false);
        var records = await cursor.ToListAsync().ConfigureAwait(false);
        var constraints = records.Select(c =>
            new GraphSchemaConstraint(c["entityType"].As<string>(), c["type"].As<string>(),
                c["labelsOrTypes"].As<List<string>>(), c["properties"].As<List<string>>())).ToArray();
        GraphKeyConstraintValidator.Validate(plan.RequiredNodeBindings, constraints);
        GraphKeyConstraintValidator.ValidateIdentifiedEdges(plan.RequiredIdentifiedEdges, constraints);
    }
}
