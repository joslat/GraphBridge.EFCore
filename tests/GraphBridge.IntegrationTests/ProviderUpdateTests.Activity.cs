using System.Diagnostics;
using GraphBridge.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace GraphBridge.IntegrationTests;

public sealed partial class ProviderUpdateTests
{
    [Neo4jIntegrationFact]
    public async Task Live_SaveChanges_traces_schema_mutation_and_acknowledged_commit_without_values()
    {
        var runId = NewRunId();
        try
        {
            await EnsureSchemaAsync();
            using var trace = new ActivityCapture();
            await using var db = new UpdateContext(new DbContextOptionsBuilder<UpdateContext>()
                .UseNeo4j(fixture.Driver, fixture.Database).EnableSensitiveDataLogging().Options);
            var person = Person(runId, "private-insert-name");
            db.Add(person);
            Assert.Equal(1, await db.SaveChangesAsync());
            var root = Assert.Single(trace.Spans, s => s.OperationName == GraphBridgeDiagnostics.SaveChangesActivityName);
            Assert.Equal(trace.Parent.SpanId, root.ParentSpanId);
            Assert.Equal(ActivityStatusCode.Unset, root.Status);
            var commit = Assert.Single(trace.Spans, s => s.OperationName == GraphBridgeDiagnostics.CommitActivityName);
            Assert.Equal("acknowledged", commit.GetTagItem("graphbridge.commit.outcome"));
            Assert.Equal(root.SpanId, commit.ParentSpanId);
            var statements = trace.Spans.Where(s => s.OperationName == GraphBridgeDiagnostics.StatementActivityName).ToArray();
            Assert.Contains(statements, s => Equals("schema_preflight", s.GetTagItem("graphbridge.command.kind")));
            Assert.Contains(statements, s => Equals("mutation", s.GetTagItem("graphbridge.command.kind")));
            Assert.All(statements, s => Assert.Equal(root.SpanId, s.ParentSpanId));
            Assert.All(trace.Spans.Where(s => s.OperationName == GraphBridgeDiagnostics.CommandActivityName),
                run => Assert.Contains(statements, s => s.SpanId == run.ParentSpanId));
            trace.AssertCompleteAndPrivate(runId, person.Id.ToString("D"), "private-insert-name");
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Live_concurrency_failure_is_classified_and_rollback_has_no_commit_span()
    {
        var runId = NewRunId();
        try
        {
            await EnsureSchemaAsync();
            var person = await InsertAsync(runId, "private-existing");
            await using var stale = CreateContext();
            var stalePerson = await stale.People.SingleAsync(p => p.RunId == runId);
            await using (var winner = CreateContext())
            {
                var fresh = await winner.People.SingleAsync(p => p.RunId == runId);
                fresh.Name = "private-winner";
                await winner.SaveChangesAsync();
            }
            using var trace = new ActivityCapture();
            stalePerson.Name = "private-loser";
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());
            var root = Assert.Single(trace.Spans, s => s.OperationName == GraphBridgeDiagnostics.SaveChangesActivityName);
            Assert.Equal("concurrency", root.GetTagItem("error.type"));
            Assert.Single(trace.Spans, s => s.OperationName == GraphBridgeDiagnostics.RollbackActivityName);
            Assert.DoesNotContain(trace.Spans, s => s.OperationName == GraphBridgeDiagnostics.CommitActivityName);
            trace.AssertCompleteAndPrivate(runId, person.Id.ToString("D"), "private-loser", "private-winner");
        }
        finally { await CleanupAsync(runId); }
    }
}
