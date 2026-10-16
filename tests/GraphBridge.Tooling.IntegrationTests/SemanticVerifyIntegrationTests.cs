using GraphBridge.Samples.FluentCypher;
using System.Diagnostics;
using System.Text.Json;
using AngleSharp.Html.Parser;
using GraphBridge.Tooling.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;

namespace GraphBridge.IntegrationTests;

[Collection(ToolingIntegrationCollection.Name)]
public sealed partial class SemanticVerifyIntegrationTests(DisposableNeo4jFixture fixture)
{
    [Neo4jIntegrationFact]
    public async Task Three_real_queries_match_independent_controls_and_detect_null_duplicate_direction_regressions()
    {
        var run = Guid.NewGuid().ToString("N");
        var owner = Guid.ParseExact(Environment.GetEnvironmentVariable("GRAPHBRIDGE_FIXTURE_ID")!, "N");
        var acknowledgment = new GraphVerificationFixture(FluentMovieReferences.FixtureId, owner, FluentMovieReferences.FixtureFingerprint, true);
        var constraints = new[] { "gb_v2_person_" + run, "gb_v2_movie_" + run, "gb_v2_credit_" + run };
        var baseline = await State(run);
        try
        {
            await Execute($"CREATE CONSTRAINT {constraints[0]} FOR (n:FGQPerson) REQUIRE n.Id IS UNIQUE");
            await Execute($"CREATE CONSTRAINT {constraints[1]} FOR (n:FGQMovie) REQUIRE n.Id IS UNIQUE");
            await Execute($"CREATE CONSTRAINT {constraints[2]} FOR ()-[r:FGQ_ACTED]-() REQUIRE r.Id IS UNIQUE");
            await Seed(run, owner);
            var before = await State(run);
            var catalog = FluentMovieContracts.Create(run, afterYear: 2000);
            var references = FluentMovieReferences.Create(run);
            Task<GraphSemanticVerificationReport> Verify(GraphQueryCatalog selected, IEnumerable<GraphNativeReference>? controls = null,
                GraphVerificationFixture? selectedFixture = null) => GraphVerify.SemanticAsync(
                    () => new Context(fixture.Driver, fixture.Database), selected, "owned.community.v1",
                    selectedFixture ?? acknowledgment, controls ?? references);
            var passed = await Verify(catalog);
            Assert.Equal(GraphEvidenceStatus.Pass, passed.Status);
            Assert.Equal(0, passed.ExitCode);
            Assert.Equal(3, passed.Cases.Count);
            Assert.All(passed.Cases, item =>
            {
                Assert.Equal(GraphEvidenceStatus.Pass, item.Status);
                Assert.Equal(GraphResultDifference.None, item.ReferenceComparison!.Difference);
                Assert.Equal(GraphResultDifference.None, item.QueryComparison!.Difference);
            });
            Assert.Equal(fixture.ServerVersion, passed.Inspection!.Evidence.Database!.ServerVersion);
            Assert.Equal(before, await State(run));
            Assert.Equal(passed.ToJson(), (await Verify(catalog)).ToJson());
            var host = await Host("--verify-semantic", run);
            Assert.Equal(0, host.ExitCode);
            using (var json = JsonDocument.Parse(host.Output))
            {
                Assert.Equal("V2", json.RootElement.GetProperty("tier").GetString());
                Assert.Equal("pass", json.RootElement.GetProperty("status").GetString());
                Assert.Equal(3, json.RootElement.GetProperty("cases").GetArrayLength());
            }
            var hostText = await Host("--verify-semantic-text", run);
            Assert.Equal(0, hostText.ExitCode);
            Assert.Contains("movies.optional.v1 | Pass | None | None", hostText.Output);
            Assert.Equal(2, (await Host("--verify-semantic", run, acknowledged: false)).ExitCode);
            Assert.Equal(1, (await Host("--verify-semantic", Guid.NewGuid().ToString("N"))).ExitCode);
            var evidenceDirectory = Path.Combine(RepositoryRoot(), ".artifacts", "next-roadmap");
            Directory.CreateDirectory(evidenceDirectory);
            await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, "verify-semantic.json"), host.Output);
            await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, "verify-semantic.txt"), hostText.Output);
            var lab = await Host("--lab-semantic", run);
            Assert.Equal(0, lab.ExitCode);
            var labDocument = new HtmlParser().ParseDocument(lab.Output);
            Assert.Equal(3, labDocument.QuerySelectorAll("article.query").Length);
            Assert.Equal(4, labDocument.QuerySelectorAll("svg.pattern").Length);
            Assert.Equal(3, labDocument.QuerySelectorAll(".source-code").Length);
            Assert.Equal(3, labDocument.QuerySelectorAll(".plan").Length);
            Assert.Contains("Fluent vs native", labDocument.Body!.TextContent);
            Assert.Contains("Native vs fixed expectations", labDocument.Body.TextContent);
            Assert.Contains("WHERE belongs to this OptionalMatch clause", labDocument.Body.TextContent);
            Assert.Contains("ReturnDistinct", labDocument.Body.TextContent);
            Assert.DoesNotContain(run, lab.Output);
            Assert.DoesNotContain(owner.ToString("N"), lab.Output);
            Assert.Empty(labDocument.QuerySelectorAll("script,iframe,link,img"));
            Assert.Equal(lab.Output, (await Host("--lab-semantic", run)).Output);
            await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, "query-lab-semantic.html"), lab.Output);
            var inspectedLab = await Host("--lab-inspect", run);
            Assert.Equal(0, inspectedLab.ExitCode);
            var inspectedDocument = new HtmlParser().ParseDocument(inspectedLab.Output);
            Assert.Equal(3, inspectedDocument.QuerySelectorAll(".plan").Length);
            Assert.Contains("Semantic comparison not run", inspectedDocument.Body!.TextContent);
            await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, "query-lab-inspect.html"), inspectedLab.Output);
            Assert.Equal(before, await State(run));
            foreach (var text in new[] { passed.ToJson(), passed.ToText() })
            {
                Assert.DoesNotContain(run, text);
                Assert.DoesNotContain(owner.ToString("N"), text);
                Assert.DoesNotContain("FGQPerson", text);
                Assert.DoesNotContain("Ada", text);
            }

            var optional = catalog.Contracts.Single(c => c.Definition.Id == "movies.optional.v1").Definition;
            // A deliberate source change, not a compiler rewrite: the same predicate after WITH
            // removes Dan/Eve's null-extended rows. Retain both commands and actual row counts.
            var movedDefinition = new GraphQueryDefinition(optional.Id,
                "Deliberate contrast: filtering after WITH does not satisfy the null-preserving OPTIONAL contract.",
                "samples/05-FluentCypher/FluentMovieQueries.cs#FilteredOptionalMovies",
                optional.Columns, optional.Comparison, optional.Duplicates, optional.Budget,
                optional.RequiredFeatures, optional.ReferenceId, optional.FixtureId);
            var movedPredicate = GraphQueryContract.Fluent(movedDefinition,
                db => FluentMovieQueries.FilteredOptionalMovies(db, run, 2000),
                value => new object?[] { value.Name, value.Title, value.Year });
            var moved = await GraphVerify.SemanticAsync(
                () => new Context(fixture.Driver, fixture.Database), new([movedPredicate]), "owned.community.v1",
                acknowledgment, references, includeStatements: true);
            Assert.Equal(GraphEvidenceStatus.Fail, moved.Status);
            Assert.Equal(GraphEvidenceStatus.Pass, moved.Cases[0].ReferenceComparison!.Status);
            Assert.Equal(GraphResultDifference.RowCount, moved.Cases[0].QueryComparison!.Difference);
            Assert.Equal(8, moved.Cases[0].QueryComparison!.ExpectedRows);
            Assert.Equal(6, moved.Cases[0].QueryComparison!.ActualRows);
            var movedHtml = GraphQueryLab.RenderHtml(moved);
            var movedDocument = new HtmlParser().ParseDocument(movedHtml);
            Assert.Contains("WHERE belongs to this With clause", movedDocument.Body!.TextContent);
            Assert.DoesNotContain("WHERE belongs to this OptionalMatch clause", movedDocument.Body.TextContent);
            Assert.Contains("FilteredOptionalMovies", movedDocument.Body.TextContent);
            Assert.Contains("8 / 6", movedDocument.Body.TextContent);
            Assert.Contains("Fail", movedDocument.QuerySelector(".hero")!.TextContent);
            await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, "query-lab-optional-after-with.html"), movedHtml);

            var nullRegression = GraphQueryContract.Fluent(optional, db => FluentMovieQueries.OptionalMovies(db, run, 2000),
                value => new object?[] { value.Name, value.Title ?? "", value.Year });
            var nullFailure = await Verify(new([nullRegression]));
            Assert.Equal(GraphEvidenceStatus.Fail, nullFailure.Status);
            Assert.Equal(GraphResultDifference.Values, nullFailure.Cases[0].QueryComparison!.Difference);
            var failedLab = new HtmlParser().ParseDocument(GraphQueryLab.RenderHtml(nullFailure));
            Assert.Contains("Fail", failedLab.QuerySelector(".hero")!.TextContent);
            Assert.Contains("Values", failedLab.Body!.TextContent);

            var duplicateRegression = GraphQueryContract.Fluent(optional, db =>
            {
                var person = Graph.Node<Person>(); var movie = Graph.Node<Movie>();
                return db.Cypher().Match(person).Where(person.Property(p => p.RunId).Eq(run))
                    .OptionalMatch(person.Out(Graph.Edge<Credit>(), movie)).Where(movie.Property(p => p.Year).Gt(2000))
                    .ReturnDistinct(() => new OptionalMovieRow(person.Value.Name, movie.Value.Title, (int?)movie.Value.Year))
                    .OrderBy(person.Property(p => p.Name)).Limit(100);
            }, value => new object?[] { value.Name, value.Title, value.Year });
            var duplicateFailure = await Verify(new([duplicateRegression]));
            Assert.Equal(GraphEvidenceStatus.Fail, duplicateFailure.Status);
            Assert.Equal(GraphResultDifference.RowCount, duplicateFailure.Cases[0].QueryComparison!.Difference);

            var wrongDirection = new GraphNativeReference("movies.coactors.native.v1", FluentMovieReferences.FixtureId,
                FluentMovieReferences.Coactors.Replace("<-[:FGQ_ACTED]-", "-[:FGQ_ACTED]->", StringComparison.Ordinal),
                new Dictionary<string, object?> { ["run"] = run, ["actor"] = "Ada" }, r => [r["name"]], [["Bob"], ["Cara"]]);
            var directionFailure = await Verify(new([catalog.Contracts[0]]), [wrongDirection]);
            Assert.Equal(GraphEvidenceStatus.Fail, directionFailure.Status);
            Assert.Equal(GraphResultDifference.RowCount, directionFailure.Cases[0].ReferenceComparison!.Difference);
            Assert.Null(directionFailure.Cases[0].QueryComparison);
            Assert.Equal(before, await State(run));

            var wrongFingerprint = new GraphVerificationFixture(FluentMovieReferences.FixtureId, owner, new string('0', 64), true);
            var blocked = await Verify(catalog, selectedFixture: wrongFingerprint);
            Assert.Equal(GraphEvidenceStatus.Blocked, blocked.Status);
            Assert.Equal(GraphSemanticIssue.FixtureMismatch, blocked.Issue);
            Assert.All(blocked.Cases, c => Assert.Equal(GraphEvidenceStatus.NotRun, c.Status));
            var blockedLab = new HtmlParser().ParseDocument(GraphQueryLab.RenderHtml(blocked));
            Assert.Contains("Blocked", blockedLab.QuerySelector(".hero")!.TextContent);
            Assert.Contains("FixtureMismatch", blockedLab.Body!.TextContent);
            Assert.Contains("Not observed", blockedLab.Body.TextContent);
            Assert.Equal(before, await State(run));

            // Even a mistakenly registered write control cannot commit: all result reads share the owned rollback transaction.
            var accidentalWrite = new GraphNativeReference("movies.coactors.native.v1", FluentMovieReferences.FixtureId,
                "CREATE (:FGQPerson {Id:$id, RunId:$run, Name:'Uncommitted'}) RETURN 'Bob' AS name",
                new Dictionary<string, object?> { ["id"] = Guid.NewGuid().ToString("D"), ["run"] = run }, r => [r["name"]], [["Bob"], ["Cara"]]);
            Assert.Equal(GraphEvidenceStatus.Fail, (await Verify(new([catalog.Contracts[0]]), [accidentalWrite])).Status);
            Assert.Equal(before, await State(run));
        }
        finally
        {
            await Execute("MATCH (:FGQPerson {RunId:$run})-[r:FGQ_ACTED]->(:FGQMovie {RunId:$run}) DELETE r", new { run });
            await Execute("MATCH (n:FGQPerson {RunId:$run}) DELETE n", new { run });
            await Execute("MATCH (n:FGQMovie {RunId:$run}) DELETE n", new { run });
            await Execute("MATCH (f:GraphBridgeVerificationFixture {runId:$run}) DELETE f", new { run });
            foreach (var name in constraints) await Execute($"DROP CONSTRAINT {name} IF EXISTS");
        }
        Assert.Equal(baseline, await State(run));
    }

    private async Task Seed(string run, Guid owner)
    {
        await using var session = fixture.OpenSession();
        await using var transaction = await session.BeginTransactionAsync();
        await (await transaction.RunAsync("""
            UNWIND ['Ada','Bob','Cara','Dan','Eve'] AS name
            CREATE (:FGQPerson {Id:randomUUID(), RunId:$run, Name:name})
            """, new { run })).ConsumeAsync();
        await (await transaction.RunAsync("""
            CREATE (:FGQMovie {Id:randomUUID(), RunId:$run, Title:'First', Year:2010}),
                   (:FGQMovie {Id:randomUUID(), RunId:$run, Title:'Second', Year:2024})
            """, new { run })).ConsumeAsync();
        await (await transaction.RunAsync("""
            UNWIND [['Ada','First'],['Ada','First'],['Ada','Second'],['Bob','First'],['Bob','Second'],['Cara','First']] AS pair
            MATCH (p:FGQPerson {RunId:$run,Name:pair[0]}),(m:FGQMovie {RunId:$run,Title:pair[1]})
            CREATE (p)-[:FGQ_ACTED {Id:randomUUID()}]->(m)
            """, new { run })).ConsumeAsync();
        await (await transaction.RunAsync("""
            CREATE (:GraphBridgeVerificationFixture {ownerId:$owner, fixtureId:$fixture, fingerprint:$fingerprint,runId:$run})
            """, new { owner = owner.ToString("N"), fixture = FluentMovieReferences.FixtureId,
                fingerprint = FluentMovieReferences.FixtureFingerprint, run })).ConsumeAsync();
        await transaction.CommitAsync();
    }
    private async Task Execute(string text, object? parameters = null)
    {
        await using var session = fixture.OpenSession();
        await (await session.RunAsync(text, parameters)).ConsumeAsync();
    }
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "samples", "05-FluentCypher", "FluentCypher.Sample.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Run this private acceptance from the repository build.");
    }
    private static async Task<(int ExitCode, string Output)> Host(string flag, string run, bool acknowledged = true)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(Path.Combine(RepositoryRoot(), "samples", "05-FluentCypher", "bin", "Debug", "net10.0", "FluentCypher.Sample.dll"));
        start.ArgumentList.Add(flag);
        start.Environment["GRAPHBRIDGE_VERIFY_DISPOSABLE"] = acknowledged ? "1" : "0";
        start.Environment["GRAPHBRIDGE_VERIFY_RUN_ID"] = run;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        await stderr; // Never include arbitrary exception/credential output in acceptance reports.
        return (process.ExitCode, await stdout);
    }
    private async Task<string[]> State(string run)
    {
        await using var session = fixture.OpenSession();
        var nodes = await (await session.RunAsync("MATCH (n {RunId:$run}) RETURN elementId(n) AS id, properties(n) AS props ORDER BY id", new { run }))
            .ToListAsync(r => r["id"].As<string>() + System.Text.Json.JsonSerializer.Serialize(r["props"]));
        var edges = await (await session.RunAsync("MATCH (p:FGQPerson {RunId:$run})-[r]->(m) RETURN elementId(r) AS id, elementId(p) AS p, elementId(m) AS m, properties(r) AS props ORDER BY id", new { run }))
            .ToListAsync(r => r["id"].As<string>() + r["p"].As<string>() + r["m"].As<string>() + System.Text.Json.JsonSerializer.Serialize(r["props"]));
        var markers = await (await session.RunAsync("MATCH (f:GraphBridgeVerificationFixture {runId:$run}) RETURN count(f) AS count", new { run })).SingleAsync();
        var constraints = await (await session.RunAsync("SHOW CONSTRAINTS YIELD name RETURN name ORDER BY name")).ToListAsync(r => r["name"].As<string>());
        var indexes = await (await session.RunAsync("SHOW INDEXES YIELD name RETURN name ORDER BY name")).ToListAsync(r => r["name"].As<string>());
        return [.. nodes, .. edges, markers["count"].As<long>().ToString(System.Globalization.CultureInfo.InvariantCulture), .. constraints, .. indexes];
    }
    private sealed class Context(IDriver driver, string database) : DbContext(new DbContextOptionsBuilder().UseNeo4j(driver, database).Options)
    {
        protected override void OnModelCreating(ModelBuilder b) => FluentMovieQueries.Configure(b);
    }
}
