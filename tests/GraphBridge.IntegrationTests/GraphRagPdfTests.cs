using System.Text;
using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.GraphRag;
using GraphBridge.GraphRag.Pdf;
using GraphBridge.Samples.GraphRagShared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using static GraphBridge.IntegrationTests.GraphRagServeTests;

namespace GraphBridge.IntegrationTests;

// GraphRAG GR-53 (ADR 0052, section 14) on the disposable fixture: a PDF built with PdfPig is ingested and asked, with the
// offline stand-ins for the models; the citation is the extracted text's own bytes, on the right page.
[Collection(Neo4jIntegrationCollection.Name)]
public sealed class GraphRagPdfTests(DisposableNeo4jFixture fixture) : IAsyncLifetime
{
    private const int Dimensions = 8;
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..12];
    private readonly string _folder = Directory.CreateTempSubdirectory("graphrag-pdf-live-").FullName;
    private readonly GraphRagEmbeddingSet _set = new("graphbridge-sample-hashing", Dimensions);
    private GraphRagLayout Layout => GraphRagLayout.Default with
    {
        DocumentLabel = "PDocument_" + _suffix, SectionLabel = "PSection_" + _suffix, ChunkLabel = "PChunk_" + _suffix,
        RunLabel = "PRun_" + _suffix, LeaseLabel = "PLease_" + _suffix, SchemaPrefix = "gbp_" + _suffix
    };

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        Directory.Delete(_folder, recursive: true);
        if (Environment.GetEnvironmentVariable("GRAPHBRIDGE_INTEGRATION") != "1") return;
        var labels = new[] { Layout.DocumentLabel, Layout.SectionLabel, Layout.ChunkLabel, Layout.RunLabel, Layout.LeaseLabel }.Select(l => "n:" + l);
        await Execute($"MATCH (n)-[r]-() WHERE {string.Join(" OR ", labels)} DELETE r");
        await Execute($"MATCH (n) WHERE {string.Join(" OR ", labels)} DELETE n");
        foreach (var definition in GraphRagSchema.Definitions(Layout, _set))
            await Execute($"DROP {(definition.IsIndex ? "INDEX" : "CONSTRAINT")} `{definition.Name}` IF EXISTS");
    }

    [Neo4jIntegrationFact]
    public async Task A_PDF_is_ingested_with_its_page_map_and_an_answer_cites_its_text_on_the_right_page()
    {
        foreach (var step in GraphRagSchema.CreateSteps(Layout, _set)) await Execute(GraphMigrationStepRenderer.Render(step).Cypher);
        await Execute("CALL db.awaitIndexes(60)");
        var path = Path.Combine(_folder, "mission-log.pdf");
        await File.WriteAllBytesAsync(path, Build(
            ["The crew boarded the capsule at dawn.", "The engines lit at noon and the rocket climbed."],
            ["The capsule reached orbit before sunset."],
            ["The parachutes opened and splashdown came in the Pacific Ocean ten days later."]));

        await using var provider = Services();
        var receipt = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync([GraphRagSources.File(path)]);
        var outcome = Assert.Single(receipt.Documents);
        Assert.Equal(GraphRagDocumentStatus.Ingested, outcome.Status);
        await using (var scope = provider.CreateAsyncScope())
        {
            var document = await scope.ServiceProvider.GetRequiredService<ServeContext>().Set<GraphRagDocument>().SingleAsync(d => d.Id == outcome.DocumentId);
            Assert.Equal(("application/pdf", 3), (document.MediaType, document.PageStarts!.Split(',').Length));
        }

        var answer = await provider.GetRequiredService<IGraphRagAnswers>().AnswerAsync("Where did the splashdown come with the parachutes?");
        Assert.Equal(GraphRagAnswerDisposition.Answered, answer.Disposition);
        var citation = Assert.Single(answer.Citations);
        Assert.Equal((3, 3), (citation.Page, citation.EndPage));
        var text = (await new GraphRagPdfLoader(new GraphRagPdfOptions(), 1 << 20).LoadAsync(GraphRagSources.File(path))).Utf8.ToArray();
        Assert.Equal(citation.Quote, Encoding.UTF8.GetString(text, citation.StartByte, citation.EndByte - citation.StartByte));

        // Plain documents keep no page map, and their citations name no page.
        var plain = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync([GraphRagSources.Text("note", "The launch tower stood empty after the rocket left the pad.")]);
        Assert.Equal(GraphRagDocumentStatus.Ingested, Assert.Single(plain.Documents).Status);
        var noted = await provider.GetRequiredService<IGraphRagAnswers>().AnswerAsync("What stood empty after the rocket left the launch pad?");
        Assert.Null(Assert.Single(noted.Citations).Page);
    }

    private static byte[] Build(params string[][] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var lines in pages)
        {
            var page = builder.AddPage(PageSize.A4);
            for (var i = 0; i < lines.Length; i++) page.AddText(lines[i], 11, new PdfPoint(40, 760 - 120 * i), font);
        }
        builder.DocumentInformation.Title = "Mission log";
        return builder.Build();
    }

    private ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(new ExtractiveChatClient());
        services.AddSingleton(new ServeModel(Layout, Dimensions, null));
        services.AddDbContext<ServeContext>(o => o.UseNeo4j(fixture.Driver, fixture.Database).ReplaceService<IModelCacheKeyFactory, ServeKey>());
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HashingEmbedder(Dimensions));
        services.AddGraphRag<ServeContext>(_set, o => o.Retrieval.NeighbourChunks = 0).AddGraphRagPdf();
        return services.BuildServiceProvider();
    }

    private async Task Execute(string cypher)
    {
        await using var session = fixture.OpenSession();
        await (await session.RunAsync(cypher)).ConsumeAsync();
    }
}
