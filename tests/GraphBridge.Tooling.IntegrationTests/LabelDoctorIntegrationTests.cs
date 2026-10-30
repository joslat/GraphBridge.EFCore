using GraphBridge.Tooling.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

/// <summary>Doctor's additional-label check (ADR 0055) against real nodes on the disposable fixture.</summary>
[Collection(ToolingIntegrationCollection.Name)]
public sealed class LabelDoctorIntegrationTests(DisposableNeo4jFixture fixture)
{
    [Neo4jIntegrationFact]
    public async Task Doctor_warns_about_a_node_without_its_types_additional_labels_and_passes_once_it_has_them()
    {
        var run = "doc_" + Guid.NewGuid().ToString("N")[..12];
        await Execute("CREATE CONSTRAINT al_person_key IF NOT EXISTS FOR (n:ALPerson) REQUIRE n.Id IS UNIQUE");
        await Execute("CREATE CONSTRAINT al_organization_key IF NOT EXISTS FOR (n:ALOrganization) REQUIRE n.Id IS UNIQUE");
        try
        {
            await using var db = new LabelContext(new DbContextOptionsBuilder<LabelContext>().UseNeo4j(fixture.Driver, fixture.Database).Options);
            db.Add(new LabelPerson { Id = run + "-ada", Run = run, Name = "Ada" });
            await db.SaveChangesAsync();
            var catalog = new GraphQueryCatalog([], []);
            Assert.Contains("GBD-LABEL-000", await Codes());

            await Execute("CREATE (:ALPerson {Id: $id, Run: $run, Name: 'Bare'})", new { id = run + "-bare", run });
            var codes = await Codes();
            Assert.Contains("GBD-LABEL-001", codes);
            Assert.DoesNotContain("GBD-LABEL-000", codes);

            // Doctor only reads: the node is still bare.
            await using var session = fixture.OpenSession();
            var cursor = await session.RunAsync("MATCH (n:ALPerson {Id: $id}) RETURN size(labels(n)) AS labels", new { id = run + "-bare" });
            Assert.Equal(1L, (await cursor.SingleAsync())["labels"].As<long>());

            // Labelled in a data step, as the warning suggests: the check passes again.
            await Execute("MATCH (n:ALPerson {Id: $id}) SET n:__Entity__:__KGBuilder__", new { id = run + "-bare" });
            Assert.Contains("GBD-LABEL-000", await Codes());

            async Task<string[]> Codes() => [.. (await GraphDoctor.InspectAsync(db, catalog, "owned.community.v1")).Diagnostics.Select(d => d.Code)];
        }
        finally
        {
            await Execute("MATCH (n:ALPerson) WHERE n.Run = $run DELETE n", new { run });
        }
    }

    private async Task Execute(string text, object? parameters = null)
    {
        await using var session = fixture.OpenSession();
        await (parameters is null ? await session.RunAsync(text) : await session.RunAsync(text, parameters)).ConsumeAsync();
    }
}
