using System.Diagnostics;
using GraphBridge.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

[Collection(Neo4jIntegrationCollection.Name)]
public sealed class ProviderObservabilityTests(DisposableNeo4jFixture fixture)
{
    [Neo4jIntegrationFact]
    public async Task Native_explain_schema_and_connectivity_emit_correlated_private_traces()
    {
        await using var db = new ProviderQueryTests.QueryContext(new DbContextOptionsBuilder<ProviderQueryTests.QueryContext>()
            .UseNeo4j(fixture.Driver, fixture.Database).EnableSensitiveDataLogging().Options);
        using var trace = new ActivityCapture();
        var parameters = new Dictionary<string, object?> { ["private_parameter"] = "private-value" };
        Assert.Equal("private-value", Assert.Single(await db.Database.QueryCypherAsync(
            "RETURN $private_parameter AS value, 'private-literal' AS text", parameters, r => r["value"].As<string>())));
        Assert.NotNull((await db.Database.ExplainCypherAsync("RETURN $private_parameter AS value", parameters)).Plan);
        Assert.NotNull(await db.Database.InspectGraphSchemaAsync());
        Assert.True(await db.Database.CanConnectAsync());
        foreach (var name in new[] { GraphBridgeDiagnostics.NativeQueryActivityName, GraphBridgeDiagnostics.ExplainActivityName,
            GraphBridgeDiagnostics.SchemaActivityName, GraphBridgeDiagnostics.ConnectivityActivityName })
        {
            var root = Assert.Single(trace.Spans, s => s.OperationName == name);
            Assert.Equal(trace.Parent.SpanId, root.ParentSpanId);
            Assert.Equal(ActivityStatusCode.Unset, root.Status);
        }
        foreach (var statement in trace.Spans.Where(s => s.OperationName == GraphBridgeDiagnostics.StatementActivityName))
        {
            if (statement.GetTagItem("graphbridge.command.kind") is "native" or "explain")
                Assert.Null(statement.GetTagItem("graphbridge.statement.fingerprint"));
        }
        trace.AssertCompleteAndPrivate("private-value", "private-literal", "private_parameter");
    }
}
