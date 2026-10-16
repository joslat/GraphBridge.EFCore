using System.Diagnostics;
using GraphBridge.EntityFrameworkCore.Diagnostics;
using GraphBridge.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore;

namespace GraphBridge.IntegrationTests;

public sealed partial class ProviderMigrationTests
{
    [Neo4jIntegrationFact]
    public async Task Live_migration_traces_initialization_and_apply_without_journal_identity_or_values()
    {
        await using var db = Context();
        using var trace = new ActivityCapture();
        await db.Database.InitializeGraphMigrationsAsync();
        var plan = Plan(GraphMigrationStep.AssertPrecondition("private-step", Scope(), 0));
        await db.Database.ApplyGraphMigrationAsync(plan);
        Assert.True(trace.Spans.Count(s => s.OperationName == GraphBridgeDiagnostics.MigrationActivityName) >= 2);
        Assert.Contains(trace.Spans, s => s.OperationName == GraphBridgeDiagnostics.CommitActivityName
            && Equals("acknowledged", s.GetTagItem("graphbridge.commit.outcome")));
        var statements = trace.Spans.Where(s => s.OperationName == GraphBridgeDiagnostics.StatementActivityName
            && Equals("migration", s.GetTagItem("graphbridge.command.kind"))).ToArray();
        Assert.NotEmpty(statements);
        Assert.All(statements, s => Assert.Null(s.GetTagItem("graphbridge.statement.fingerprint")));
        Assert.DoesNotContain(trace.Spans, s => s.Status == ActivityStatusCode.Error);
        trace.AssertCompleteAndPrivate(_suffix, Label, "private-step");
    }
}
