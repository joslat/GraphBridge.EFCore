using GraphBridge.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

// Vector and full-text index steps (ADR 0053) on the disposable fixture.
public sealed partial class ProviderMigrationTests
{
    [Neo4jIntegrationFact]
    public async Task Search_index_steps_create_journal_serve_typed_search_and_drop()
    {
        var section = Label + "_Section";
        var vector = Track(GraphMigrationSchemaDefinition.VectorIndex(Name("vec"), "NODE", Label, "embedding", 3, GraphVectorSimilarity.Cosine));
        var text = Track(GraphMigrationSchemaDefinition.FullTextIndex(Name("txt"), "NODE", [Label, section], ["title", "body"]));
        try
        {
            await using var db = Context();
            await db.Database.InitializeGraphMigrationsAsync();
            var create = Plan(GraphMigrationStep.CreateIndex("vector", vector), GraphMigrationStep.CreateIndex("text", text));
            var validation = await db.Database.ValidateGraphMigrationAsync(create);
            Assert.True(validation.CanApply, string.Join("; ", validation.Issues));

            var applied = await db.Database.ApplyGraphMigrationAsync(create);
            Assert.False(applied.AlreadyApplied);
            Assert.Equal(2, applied.CompletedSteps);
            var (vectorState, vectorConfig) = await Index(vector.Name);
            Assert.Equal("ONLINE", vectorState);
            Assert.Equal(3L, vectorConfig["vector.dimensions"]);
            Assert.Equal("COSINE", vectorConfig["vector.similarity_function"]);
            var (textState, textConfig) = await Index(text.Name);
            Assert.Equal("ONLINE", textState);
            Assert.Equal("standard-no-stop-words", textConfig["fulltext.analyzer"]);
            Assert.False((bool)textConfig["fulltext.eventually_consistent"]);
            Assert.Equal(2L, await Scalar($"MATCH (s:__GraphBridgeMigrationStep {{migrationId: '{create.Id}', status: 'Completed'}}) RETURN count(s) AS value"));
            Assert.True((await db.Database.ApplyGraphMigrationAsync(create)).AlreadyApplied);

            // Typed search reads the created indexes: one vector label, and full-text across both labels.
            await Execute($"CREATE (:`{Label}` {{title: 'graphs connect facts', embedding: [1.0, 0.0, 0.0]}}), (:`{Label}` {{title: 'tables hold rows', embedding: [0.0, 1.0, 0.0]}}), (:`{section}` {{body: 'graphs in sections'}})");
            var nearest = await db.Database.QueryVectorNodesAsync(vector.Name, [0.9, 0.1, 0.0], node => node["title"].As<string>(), limit: 1);
            Assert.Equal("graphs connect facts", Assert.Single(nearest.Results).Value);
            Assert.Equal(3, nearest.Provenance.Dimensions);
            var words = await db.Database.QueryFullTextNodesAsync(text.Name, "graphs", node => node.Labels.Single(), limit: 5);
            Assert.Equal([Label, section], words.Results.Select(r => r.Value).Order(StringComparer.Ordinal));

            var drop = new GraphMigrationPlan(create.Id + "_drop", create.Id,
                [GraphMigrationStep.DropIndex("vector", vector, "Test removes the index it created"),
                 GraphMigrationStep.DropIndex("text", text, "Test removes the index it created")],
                GraphMigrationRollbackPolicy.NoRollback("Explicit disposable-fixture migration; no automatic compensation"));
            Assert.Equal(2, (await db.Database.ApplyGraphMigrationAsync(drop)).CompletedSteps);
            var names = await SchemaNames();
            Assert.DoesNotContain("index:" + vector.Name, names);
            Assert.DoesNotContain("index:" + text.Name, names);
        }
        finally
        {
            await Execute($"MATCH (n:`{section}`) DELETE n");
        }
    }

    [Neo4jIntegrationFact]
    public async Task A_different_index_under_the_step_name_conflicts_and_an_equivalent_under_another_name_refuses_creation()
    {
        var vector = Track(GraphMigrationSchemaDefinition.VectorIndex(Name("vec"), "NODE", Label, "embedding", 3, GraphVectorSimilarity.Cosine));
        var other = Name("legacy");
        var otherText = Name("legacy_text");
        try
        {
            await using var db = Context();
            await db.Database.InitializeGraphMigrationsAsync();
            var plan = Plan(GraphMigrationStep.CreateIndex("vector", vector));

            // Same name, other dimensions: the step stops for review and changes nothing.
            await Execute($"CREATE VECTOR INDEX `{vector.Name}` FOR (n:`{Label}`) ON (n.embedding) OPTIONS {{indexConfig: {{`vector.dimensions`: 4, `vector.similarity_function`: 'cosine'}}}}");
            var conflict = await db.Database.ValidateGraphMigrationAsync(plan);
            Assert.False(conflict.CanApply);
            Assert.Contains(conflict.Issues, issue => issue.Contains("differs", StringComparison.Ordinal));
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.ApplyGraphMigrationAsync(plan));
            Assert.Equal(4L, (await Index(vector.Name)).Config["vector.dimensions"]);
            await Execute($"DROP INDEX `{vector.Name}`");
            await ResetJournal();
            await db.Database.InitializeGraphMigrationsAsync();

            // Another name over the same label and property: Neo4j allows one vector index there, so creation is refused.
            await Execute($"CREATE VECTOR INDEX `{other}` FOR (n:`{Label}`) ON (n.embedding) OPTIONS {{indexConfig: {{`vector.dimensions`: 8, `vector.similarity_function`: 'euclidean'}}}}");
            var equivalent = await db.Database.ValidateGraphMigrationAsync(plan);
            Assert.False(equivalent.CanApply);
            Assert.Contains(equivalent.Issues, issue => issue.Contains($"'{other}'", StringComparison.Ordinal));
            Assert.DoesNotContain("index:" + vector.Name, await SchemaNames());

            // The server agrees: a second vector index over that schema is refused, whatever its options.
            var refused = await Assert.ThrowsAnyAsync<Neo4jException>(() => Execute(GraphMigrationStepRenderer.Render(plan.Steps[0]).Cypher));
            Assert.Contains("Schema", refused.Code, StringComparison.Ordinal);

            // Full-text, same labels and properties in the same order with another analyzer: both GraphBridge and the server refuse.
            var text = Track(GraphMigrationSchemaDefinition.FullTextIndex(Name("txt"), "NODE", [Label, Label + "_Section"], ["title", "body"]));
            var textPlan = Plan(GraphMigrationStep.CreateIndex("text", text));
            var textCypher = GraphMigrationStepRenderer.Render(textPlan.Steps[0]).Cypher;
            await Execute($"CREATE FULLTEXT INDEX `{otherText}` FOR (n:`{Label}`|`{Label + "_Section"}`) ON EACH [n.title, n.body] OPTIONS {{indexConfig: {{`fulltext.analyzer`: 'english'}}}}");
            var sameOrder = await db.Database.ValidateGraphMigrationAsync(textPlan);
            Assert.False(sameOrder.CanApply);
            Assert.Contains(sameOrder.Issues, issue => issue.Contains($"'{otherText}'", StringComparison.Ordinal));
            var textRefused = await Assert.ThrowsAnyAsync<Neo4jException>(() => Execute(textCypher));
            Assert.Contains("Schema", textRefused.Code, StringComparison.Ordinal);

            // Listed in another order: GraphBridge still refuses (it compares sets), while the server accepts a second index.
            await Execute($"DROP INDEX `{otherText}`");
            await Execute($"CREATE FULLTEXT INDEX `{otherText}` FOR (n:`{Label + "_Section"}`|`{Label}`) ON EACH [n.body, n.title]");
            var reordered = await db.Database.ValidateGraphMigrationAsync(textPlan);
            Assert.False(reordered.CanApply);
            Assert.Contains(reordered.Issues, issue => issue.Contains($"'{otherText}'", StringComparison.Ordinal));
            await Execute(textCypher);
            Assert.Contains("index:" + text.Name, await SchemaNames());
        }
        finally
        {
            await Execute($"DROP INDEX `{other}` IF EXISTS");
            await Execute($"DROP INDEX `{otherText}` IF EXISTS");
        }
    }

    private string Name(string suffix) => "gb_test_" + _suffix + "_" + suffix;

    private GraphMigrationSchemaDefinition Track(GraphMigrationSchemaDefinition definition)
    {
        _schema.Add(definition);
        return definition;
    }

    private async Task<(string State, IReadOnlyDictionary<string, object> Config)> Index(string name)
    {
        await using var session = fixture.OpenSession();
        var row = await (await session.RunAsync("SHOW INDEXES YIELD name, state, options WHERE name = $name RETURN state, options", new { name })).SingleAsync();
        var options = row["options"].As<Dictionary<string, object>>();
        return (row["state"].As<string>(), options["indexConfig"].As<Dictionary<string, object>>());
    }
}
