using System.Globalization;
using System.Reflection;
using AngleSharp.Html.Parser;
using GraphBridge.Samples.FluentCypher;
using GraphBridge.Tooling.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;

namespace GraphBridge.ToolingTests;

public sealed class GraphQueryLabTests
{
    [Fact]
    public void Three_actual_queries_render_typed_graphs_scope_counts_and_no_unobserved_semantic_success()
    {
        var driver = DispatchProxy.Create<IDriver, GraphEvidenceTests.NeverTransport>();
        using var db = new Context(driver);
        var report = GraphVerify.Offline(db, FluentMovieContracts.Create("private-partition"), includeStatements: true);
        var html = GraphQueryLab.RenderHtml(report);
        var document = new HtmlParser().ParseDocument(html);
        Assert.Equal(3, document.QuerySelectorAll("article.query").Length);
        Assert.Equal(4, document.QuerySelectorAll("svg.pattern").Length);
        Assert.Equal(5, document.QuerySelectorAll("svg .edge").Length);
        Assert.Contains("count(DISTINCT n1)", document.Body!.TextContent);
        Assert.Contains("count(*)", document.Body.TextContent);
        Assert.Contains("WHERE belongs to this OptionalMatch clause", document.Body.TextContent);
        Assert.Contains("n3 → n1", document.Body.TextContent);
        Assert.Contains("Semantic comparison not run", document.Body.TextContent);
        Assert.Contains("No EXPLAIN plan captured", document.Body.TextContent);
        Assert.DoesNotContain("private-partition", html);
        foreach (var link in document.QuerySelectorAll("a[href^='#']"))
            Assert.NotNull(document.GetElementById(link.GetAttribute("href")![1..]));
        Assert.Empty(document.QuerySelectorAll("script,iframe,object,embed,link,img,form"));
        Assert.Equal("default-src 'none'; style-src 'unsafe-inline'; script-src 'none'; base-uri 'none'; form-action 'none'",
            document.QuerySelector("meta[http-equiv='Content-Security-Policy']")!.GetAttribute("content"));
        Assert.Equal(0, ((GraphEvidenceTests.NeverTransport)(object)driver).Calls);
        Assert.Equal(html, GraphQueryLab.RenderHtml(report));
        var culture = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = new("fr-FR"); Assert.Equal(html, GraphQueryLab.RenderHtml(report)); }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    [Fact]
    public void Hostile_source_metadata_and_reference_strings_are_text_not_active_content()
    {
        using var db = new Context();
        const string hostile = "</textarea><script>alert('x')</script><img src=x onerror=alert(1)>";
        var definition = new GraphQueryDefinition("hostile.query.v1", hostile, "javascript:alert(1)",
            [new("name", GraphResultKind.String)], GraphResultComparison.Ordered, GraphDuplicatePolicy.Preserve, new(100, TimeSpan.FromSeconds(1)));
        var report = GraphDoctor.InspectOffline(db, new([GraphQueryContract.Fluent(definition, context => FluentMovieQueries.Coactors(context, "private-partition", "Ada"))]), includeStatements: true);
        var html = GraphQueryLab.RenderHtml(report, [new("hostile.query.v1", hostile)]);
        var document = new HtmlParser().ParseDocument(html);
        Assert.Empty(document.QuerySelectorAll("script,img,iframe,object,embed,link"));
        Assert.Contains(hostile, document.QuerySelector(".source-code")!.TextContent);
        Assert.Contains("javascript:alert(1)", document.QuerySelector(".source-ref")!.TextContent);
        Assert.Empty(document.QuerySelectorAll("[onerror],[onclick],a[href^='javascript:']"));
        Assert.Contains("Host-supplied excerpt", document.Body!.TextContent);
        Assert.DoesNotContain("private-partition", html);
        Assert.Throws<ArgumentException>(() => GraphQueryLab.RenderHtml(report, [new("wrong.query.v1", "text")]));
        Assert.Throws<ArgumentException>(() => GraphQueryLab.RenderHtml(report, [new("hostile.query.v1", "one"), new("hostile.query.v1", "two")]));
    }

    [Fact]
    public void Default_redaction_and_nested_scope_omissions_cannot_become_invented_graphs()
    {
        using var db = new Context();
        var hidden = GraphQueryLab.RenderHtml(GraphVerify.Offline(db, FluentMovieContracts.Create("secret")));
        var document = new HtmlParser().ParseDocument(hidden);
        Assert.Empty(document.QuerySelectorAll("svg"));
        Assert.DoesNotContain("FGQPerson", hidden);
        Assert.Contains("Statement text was not disclosed", document.Body!.TextContent);
        var definition = FluentMovieContracts.Create("secret").Contracts[0].Definition;
        var nested = GraphDoctor.InspectOffline(db, new([GraphQueryContract.Fluent(definition,
            context => FluentMovieQueries.FriendsReaching(context, "secret", "Dan"))]), includeStatements: true);
        var unavailable = new HtmlParser().ParseDocument(GraphQueryLab.RenderHtml(nested));
        Assert.Empty(unavailable.QuerySelectorAll("svg"));
        Assert.Contains("NestedExistsNotRepresented", unavailable.Body!.TextContent);
        Assert.Contains("EXISTS", unavailable.QuerySelector(".cypher-code")!.TextContent);
    }

    [Fact]
    public async Task Actual_planning_observation_is_rendered_without_implying_execution_or_reopening_transport()
    {
        var protocol = new ConnectedDoctorTests.Protocol { Explain = (_, _) => GraphVerifyTests.Cursor(GraphVerifyTests.Plan("ProduceResults", GraphVerifyTests.Plan("Projection"))) };
        using var db = new ConnectedDoctorTests.Context(protocol.Driver);
        var definition = new GraphQueryDefinition("test.plan.v1", "Synthetic plan", "test.cs", [new("name", GraphResultKind.String)],
            GraphResultComparison.Ordered, GraphDuplicatePolicy.Preserve, new(10, TimeSpan.FromSeconds(1)));
        var contract = GraphQueryContract.Fluent(definition, context =>
        {
            var node = Graph.Node<ConnectedDoctorTests.Item>();
            return context.Cypher().Match(node).Return(() => node.Value.Name).Limit(10);
        });
        var evidence = await GraphVerify.InspectAsync(db, new([contract]), "test.read.v1", includeStatements: true);
        var calls = protocol.Calls.Count;
        var document = new HtmlParser().ParseDocument(GraphQueryLab.RenderHtml(evidence));
        Assert.Equal(2, document.QuerySelectorAll(".plan li").Length);
        Assert.Contains("ProduceResults", document.Body!.TextContent);
        Assert.Contains("Semantic comparison not run", document.Body.TextContent);
        Assert.Equal(calls, protocol.Calls.Count);
        protocol.AssertCleanup(2);
    }

    [Fact]
    public async Task No_context_or_empty_catalog_evidence_stays_visible_as_nonpassing()
    {
        using var db = new Context();
        var empty = new HtmlParser().ParseDocument(GraphQueryLab.RenderHtml(GraphVerify.Offline(db, new([]))));
        Assert.Contains("Not run", empty.QuerySelector(".hero")!.TextContent);
        Assert.Contains("No compiled query snapshots", empty.Body!.TextContent);
        var canceled = await GraphVerify.SemanticAsync(() => throw new InvalidOperationException("Should not run"),
            FluentMovieContracts.Create("private"), "test.read.v1", new("movies.parallel-credits.v1", Guid.NewGuid(), new string('a', 64), true), [], cancellationToken: new(true));
        var blocked = new HtmlParser().ParseDocument(GraphQueryLab.RenderHtml(canceled));
        Assert.Contains("Blocked", blocked.QuerySelector(".hero")!.TextContent);
        Assert.Equal(3, blocked.QuerySelectorAll("table tbody tr").Length);
        Assert.Empty(blocked.QuerySelectorAll("svg"));
        Assert.Contains("GBD-EXEC-001", blocked.Body!.TextContent);
    }

    private sealed class Context(IDriver? driver = null) : DbContext(new DbContextOptionsBuilder().UseNeo4j(
        driver ?? DispatchProxy.Create<IDriver, GraphEvidenceTests.NeverTransport>(), "offline").Options)
    {
        protected override void OnModelCreating(ModelBuilder builder) => FluentMovieQueries.Configure(builder);
    }

    [Fact]
    public void Large_pattern_omits_only_SVG_and_retains_exact_accessible_data()
    {
        using var db = new Context();
        var start = Graph.Node<Person>();
        var pattern = start.Out(Graph.Edge<Friendship>(), Graph.Node<Person>());
        for (var i = 0; i < 31; i++) pattern = pattern.Out(Graph.Edge<Friendship>(), Graph.Node<Person>());
        var command = db.Cypher().Match(pattern).Return(() => start.Value.Name).Limit(10).Inspect();
        var evidence = new GraphEvidenceReport(GraphEvidenceMode.Offline, [GraphQueryEvidence.Capture("large.query.v1", command, includeStatement: true)], []);
        var document = new HtmlParser().ParseDocument(GraphQueryLab.RenderHtml(evidence));
        Assert.Empty(document.QuerySelectorAll("svg"));
        Assert.Contains("32-node / 64-step display budget", document.Body!.TextContent);
        Assert.Equal(33, document.QuerySelectorAll(".pattern-data table:first-of-type tbody tr").Length);
        Assert.Equal(32, document.QuerySelectorAll(".pattern-data table:nth-of-type(2) tbody tr").Length);
    }

    [Fact]
    public void Excessive_report_and_source_text_fail_without_silent_truncation()
    {
        using var db = new Context();
        var command = FluentMovieQueries.Coactors(db, "private", "Ada").Inspect();
        Assert.Throws<ArgumentException>(() => new GraphQuerySource("test.source.v1", new string('x', 32_769)));
        var ids = Enumerable.Range(0, 33).Select(i => "large.query.v" + i.ToString(CultureInfo.InvariantCulture)).ToArray();
        var evidence = new GraphEvidenceReport(GraphEvidenceMode.Offline, ids.Select(id => GraphQueryEvidence.Capture(id, command, includeStatement: true)), []);
        var excerpts = ids.Select(id => new GraphQuerySource(id, new string('<', 32_768))).ToArray();
        Assert.Throws<InvalidOperationException>(() => GraphQueryLab.RenderHtml(evidence, excerpts));
    }
}
