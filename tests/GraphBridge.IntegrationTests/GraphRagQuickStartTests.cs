using System.Text;
using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.GraphRag;
using GraphBridge.Samples.GraphRag;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GraphBridge.IntegrationTests;

// Sample 07 acceptance (GraphRAG wave 2): the sample's own setup, ingestion and answers, offline models, on the fixture.
[Collection(Neo4jIntegrationCollection.Name)]
public sealed class GraphRagQuickStartTests(DisposableNeo4jFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => Enabled ? ResetJournal() : Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (!Enabled) return;
        foreach (var definition in GraphRagSchema.Definitions(GraphRagLayout.Default, QuickStart.EmbeddingSet))
            await Execute($"DROP {(definition.IsIndex ? "INDEX" : "CONSTRAINT")} {definition.Name} IF EXISTS");
        await ResetJournal();
    }

    private static bool Enabled => Environment.GetEnvironmentVariable("GRAPHBRIDGE_INTEGRATION") == "1";

    [Neo4jIntegrationFact]
    public async Task The_sample_answers_from_the_docs_with_checked_citations_and_refuses_an_unrelated_question()
    {
        await using var services = new ServiceCollection()
            .AddDbContext<QuickStartContext>(options => options.UseNeo4j(fixture.Driver, fixture.Database))
            .AddQuickStart()
            .BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var db = provider.GetRequiredService<QuickStartContext>();
        await db.Database.InitializeGraphMigrationsAsync();
        Assert.Equal(7, (await db.Database.ApplyGraphMigrationAsync(QuickStart.SchemaPlan)).CompletedSteps);
        try
        {
            // Start empty: an interrupted earlier run may have left documents that would now count as unchanged.
            await provider.GetRequiredService<IGraphRagMaintenance>().ResetAsync(new GraphRagResetScope());
            await Execute("MATCH (n:IngestionRun) DELETE n");
            var docs = Path.Combine(RepositoryRoot(), "docs", "features");
            var files = new[] { "SCHEMA_AND_MIGRATIONS.md", "GRAPH_SEARCH.md", "VECTOR_DATA.md" }.Select(f => Path.Combine(docs, f)).ToArray();
            var receipt = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync(files.Select(GraphRagSources.File));
            Assert.All(receipt.Documents, d => Assert.Equal(GraphRagDocumentStatus.Ingested, d.Status));

            var answers = provider.GetRequiredService<IGraphRagAnswers>();
            var answer = await answers.AnswerAsync("How does a migration create vector and full-text indexes?");
            Assert.Equal(GraphRagAnswerDisposition.Answered, answer.Disposition);
            var citation = Assert.Single(answer.Citations);
            var document = receipt.Documents.Single(d => d.DocumentId == citation.DocumentId);
            var bytes = await File.ReadAllBytesAsync(new Uri(document.SourceUri).LocalPath);
            Assert.Equal(citation.Quote, Encoding.UTF8.GetString(bytes, citation.StartByte, citation.EndByte - citation.StartByte));

            var unrelated = await answers.AnswerAsync("What is the capital of Mongolia?");
            Assert.Equal(GraphRagAnswerDisposition.InsufficientEvidence, unrelated.Disposition);
            Assert.Null(unrelated.Text);

            // GR-14: both export, read back to the same document, and check clean without the database.
            foreach (var exported in new[] { answer, unrelated })
            {
                var json = GraphRagAnswerExport.ToJson(exported, DateTimeOffset.UnixEpoch);
                Assert.Equal(json, GraphRagAnswerExport.ToJson(GraphRagAnswerExport.FromJson(json), DateTimeOffset.UnixEpoch));
                Assert.Empty(GraphRagAnswerExport.Check(GraphRagAnswerExport.FromJson(json)));
            }

            var again = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync(files.Select(GraphRagSources.File));
            Assert.All(again.Documents, d => Assert.Equal(GraphRagDocumentStatus.Unchanged, d.Status));
        }
        finally
        {
            await provider.GetRequiredService<IGraphRagMaintenance>().ResetAsync(new GraphRagResetScope());
            await Execute("MATCH (n:IngestionRun) DELETE n");
        }
        Assert.Equal(0, await db.Set<GraphRagChunk>().CountAsync());
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GraphBridge.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    private async Task Execute(string text)
    {
        await using var session = fixture.OpenSession();
        await (await session.RunAsync(text)).ConsumeAsync();
    }

    private async Task ResetJournal()
    {
        foreach (var label in new[] { GraphMigrationJournal.StepLabel, GraphMigrationJournal.HistoryLabel, GraphMigrationJournal.ControlLabel })
            await Execute($"MATCH (n:{label}) DELETE n");
        foreach (var name in new[] { GraphMigrationJournal.ControlConstraint, GraphMigrationJournal.HistoryConstraint, GraphMigrationJournal.StepConstraint })
            await Execute($"DROP CONSTRAINT {name} IF EXISTS");
    }
}
