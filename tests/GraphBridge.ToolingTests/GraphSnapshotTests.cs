using System.Reflection;
using System.Text.Json;
using GraphBridge.EntityFrameworkCore.FluentCypher;
using GraphBridge.EntityFrameworkCore.Paths;
using GraphBridge.Samples.FluentCypher;
using GraphBridge.Tooling.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;

namespace GraphBridge.ToolingTests;

public sealed class GraphSnapshotTests
{
    [Fact]
    public void Shared_evidence_captures_the_once_compiled_snapshot_only_with_explicit_text_disclosure()
    {
        using var db = new Context();
        var calls = 0;
        var definition = FluentMovieContracts.Create("private-partition").Contracts[0].Definition;
        var catalog = new GraphQueryCatalog([GraphQueryContract.Fluent(definition, context =>
        {
            calls++;
            return FluentMovieQueries.Coactors(context, "private-partition", "private-name");
        })]);
        var privateReport = GraphDoctor.InspectOffline(db, catalog);
        Assert.Equal(1, calls);
        Assert.Null(privateReport.Queries[0].Semantics);
        Assert.DoesNotContain("FGQPerson", privateReport.ToJson());
        var disclosed = GraphDoctor.InspectOffline(db, catalog, includeStatements: true);
        Assert.Equal(2, calls);
        Assert.True(disclosed.SensitiveTextIncluded);
        Assert.Equal(CypherSnapshotStatus.Complete, disclosed.Queries[0].Semantics!.Status);
        Assert.Equal(privateReport.Queries[0].StatementFingerprint, disclosed.Queries[0].StatementFingerprint);
        Assert.DoesNotContain("private-", disclosed.ToJson());
        var inspected = FluentMovieQueries.Coactors(db, "private-partition", "private-name").Inspect();
        Assert.Null(GraphQueryEvidence.Capture("test.snapshot.v1", inspected).Semantics);
        Assert.Same(inspected.Semantics, GraphQueryEvidence.Capture("test.snapshot.v1", inspected, includeStatement: true).Semantics);
    }

    [Fact]
    public void Actual_coactor_snapshot_keeps_occurrences_direction_mapped_roles_and_parameter_redaction()
    {
        var driver = DispatchProxy.Create<IDriver, GraphEvidenceTests.NeverTransport>();
        using var db = new Context(driver);
        var query = FluentMovieQueries.Coactors(db, "private-partition", "private-name");
        var inspected = query.Inspect();
        Assert.Equal(query.ToCommand().Text, inspected.Text);
        Assert.Equal(query.ToCommand().Parameters, inspected.Parameters);
        Assert.Null(query.ToCommand().Semantics);
        var snapshot = inspected.Semantics!;
        Assert.Equal(CypherSnapshotStatus.Complete, snapshot.Status);
        Assert.Equal(new[] { CypherClauseKind.Match, CypherClauseKind.Return, CypherClauseKind.OrderBy, CypherClauseKind.Limit }, snapshot.Clauses.Select(c => c.Kind));
        var match = snapshot.Clauses[0];
        Assert.True(match.HasPredicate);
        Assert.Empty(match.ScopeBefore);
        Assert.Equal(new[] { "n0", "n1", "n3", "r2", "r4" }, match.ScopeAfter);
        var pattern = Assert.Single(match.Patterns);
        Assert.Equal(new[] { "n0", "n1", "n3" }, pattern.Nodes.Select(n => n.Alias));
        Assert.Equal(new[] { "FGQPerson", "FGQMovie", "FGQPerson" }, pattern.Nodes.Select(n => n.Label));
        Assert.Equal(new[] { "r2", "r4" }, pattern.Steps.Select(s => s.Alias));
        Assert.Equal(CypherStepDirection.Outgoing, pattern.Steps[0].Direction);
        var incoming = pattern.Steps[1];
        Assert.Equal(CypherStepDirection.Incoming, incoming.Direction);
        Assert.Equal("n1", incoming.StartAlias);
        Assert.Equal("n3", incoming.EndAlias);
        Assert.Equal("n3", incoming.MappedSourceAlias);
        Assert.Equal("n1", incoming.MappedTargetAlias);
        Assert.All(pattern.Steps, s => { Assert.True(s.Identified); Assert.False(s.Repeated); });
        Assert.True(snapshot.Clauses[1].Distinct);
        Assert.Equal(new[] { "c0" }, snapshot.Clauses[1].ScopeAfter);
        Assert.Equal(new[] { "c0" }, snapshot.Clauses[2].ScopeBefore);
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(snapshot));
        Assert.Equal(JsonSerializer.Serialize(snapshot), JsonSerializer.Serialize(query.Inspect().Semantics));
        AssertLineSpans(inspected, ["MATCH", "RETURN DISTINCT", "ORDER BY", "LIMIT"]);
        Assert.Equal(0, ((GraphEvidenceTests.NeverTransport)(object)driver).Calls);
        Assert.Throws<NotSupportedException>(() => ((IList<CypherClauseSnapshot>)snapshot.Clauses).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<CypherNodeSnapshot>)pattern.Nodes).Clear());
    }

    [Fact]
    public void Actual_shared_movie_snapshot_records_aggregate_expressions_and_scope_drops()
    {
        using var db = new Context();
        var query = FluentMovieQueries.SharedMovies(db, "private-partition", "Ada");
        var command = query.Inspect();
        Assert.Equal(query.ToCommand().Text, command.Text);
        Assert.Equal(query.ToCommand().Parameters, command.Parameters);
        var with = command.Semantics!.Clauses.Single(c => c.Kind == CypherClauseKind.With);
        Assert.Equal(new[] { "n0", "n1", "n3", "r2", "r4" }, with.ScopeBefore);
        Assert.Equal(new[] { "n3", "v5", "v6" }, with.ScopeAfter);
        Assert.Equal(new[] { "count(DISTINCT n1)", "count(*)" }, with.Aggregates.Select(a => a.Expression));
        Assert.Equal(new[] { "v5", "v6" }, with.Aggregates.Select(a => a.Alias));
        Assert.True(with.HasPredicate);
        Assert.False(with.Distinct);
        AssertLineSpans(command, ["MATCH", "WITH", "RETURN", "ORDER BY", "LIMIT"]);
        Assert.Equal(3, with.FirstLine);
        Assert.Equal(4, with.LastLine);
    }

    [Fact]
    public void Actual_optional_and_post_WITH_filters_have_different_owners_without_rewriting()
    {
        using var db = new Context();
        var optional = FluentMovieQueries.OptionalMovies(db, "private-partition", 2000).Inspect();
        var filtered = FluentMovieQueries.FilteredOptionalMovies(db, "private-partition", 2000).Inspect();
        var clause = optional.Semantics!.Clauses.Single(c => c.Kind == CypherClauseKind.OptionalMatch);
        Assert.True(clause.HasPredicate);
        Assert.Equal(new[] { "n0" }, clause.ScopeBefore);
        Assert.Equal(new[] { "n0", "n1", "r2" }, clause.ScopeAfter);
        Assert.Equal(3, clause.FirstLine);
        Assert.Equal(4, clause.LastLine);
        Assert.False(filtered.Semantics!.Clauses.Single(c => c.Kind == CypherClauseKind.OptionalMatch).HasPredicate);
        Assert.True(filtered.Semantics.Clauses.Single(c => c.Kind == CypherClauseKind.With).HasPredicate);
        AssertLineSpans(optional, ["MATCH", "OPTIONAL MATCH", "RETURN", "ORDER BY", "LIMIT"]);
        AssertLineSpans(filtered, ["MATCH", "OPTIONAL MATCH", "WITH", "RETURN", "ORDER BY", "LIMIT"]);
    }

    [Theory]
    [InlineData(GraphTraversalDirection.Outgoing, CypherStepDirection.Outgoing)]
    [InlineData(GraphTraversalDirection.Incoming, CypherStepDirection.Incoming)]
    [InlineData(GraphTraversalDirection.Both, CypherStepDirection.Both)]
    public void Bounded_paths_keep_depth_direction_and_do_not_invent_each_edges_physical_endpoints(GraphTraversalDirection direction, CypherStepDirection expected)
    {
        using var db = new Context();
        var query = FluentMovieQueries.FriendPaths(db, "private-partition", "Ada", direction);
        var command = query.Inspect();
        Assert.Equal(query.ToCommand().Text, command.Text);
        var pattern = Assert.Single(command.Semantics!.Clauses[0].Patterns);
        Assert.Equal("path3", pattern.PathAlias);
        Assert.Equal(1, pattern.MinimumHops);
        Assert.Equal(3, pattern.MaximumHops);
        var step = Assert.Single(pattern.Steps);
        Assert.Equal(expected, step.Direction);
        Assert.True(step.Repeated);
        Assert.False(step.Identified);
        Assert.Null(step.MappedSourceAlias);
        Assert.Null(step.MappedTargetAlias);
    }

    [Fact]
    public void Nested_EXISTS_stays_compilable_but_omits_incomplete_diagram()
    {
        using var db = new Context();
        var query = FluentMovieQueries.FriendsReaching(db, "private-partition", "private-target");
        var command = query.Inspect();
        Assert.Equal(query.ToCommand().Text, command.Text);
        Assert.Equal(query.ToCommand().Parameters, command.Parameters);
        Assert.Equal(CypherSnapshotStatus.NestedExistsNotRepresented, command.Semantics!.Status);
        Assert.Empty(command.Semantics.Clauses);
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(command.Semantics));
    }

    [Fact]
    public void Distinct_WITH_scope_reused_edge_and_paging_remain_explicit()
    {
        using var db = new Context();
        var person = Graph.Node<Person>(); var movie = Graph.Node<Movie>(); var edge = Graph.Edge<Credit>();
        var query = db.Cypher().Match(person.Out(edge, movie), movie.In(edge, person))
            .WithDistinct(person, edge).Return(() => edge.Value.Id).OrderBy(edge.Property(e => e.Id)).Skip(2).Limit(10);
        var command = query.Inspect();
        var snapshot = command.Semantics!;
        Assert.Equal(2, snapshot.Clauses[0].Patterns.Count);
        Assert.Equal("r2", snapshot.Clauses[0].Patterns[0].Steps[0].Alias);
        Assert.Equal("r2", snapshot.Clauses[0].Patterns[1].Steps[0].Alias);
        Assert.True(snapshot.Clauses[1].Distinct);
        Assert.Equal(new[] { "n0", "r2" }, snapshot.Clauses[1].ScopeAfter);
        AssertLineSpans(command, ["MATCH", "WITH DISTINCT", "RETURN", "ORDER BY", "SKIP", "LIMIT"]);
        Assert.Equal(query.ToCommand().Parameters, command.Parameters);
    }

    [Fact]
    public void Dropped_alias_rejection_is_not_weakened_by_inspection()
    {
        using var db = new Context();
        var person = Graph.Node<Person>(); var movie = Graph.Node<Movie>();
        var query = db.Cypher().Match(person.Out(Graph.Edge<Credit>(), movie)).With(person).Return(() => movie.Value.Title);
        Assert.Throws<FluentCypherException>(() => query.ToCommand());
        Assert.Throws<FluentCypherException>(() => query.Inspect());
    }

    [Fact]
    public void Snapshot_budgets_omit_all_clauses_without_truncating_or_rejecting_the_command()
    {
        using var db = new Context();
        var person = Graph.Node<Person>();
        var clauses = db.Cypher().Match(person);
        for (var i = 0; i < 256; i++) clauses = clauses.With(person);
        var query = clauses.Return(() => person.Value.Name);
        var command = query.Inspect();
        Assert.Equal(query.ToCommand().Text, command.Text);
        Assert.Equal(CypherSnapshotStatus.BudgetExceeded, command.Semantics!.Status);
        Assert.Empty(command.Semantics.Clauses);
        var movie = Graph.Node<Movie>();
        var pattern = person.Out(Graph.Edge<Credit>(), movie);
        var many = db.Cypher().Match(Enumerable.Repeat(pattern, 1366).ToArray()).Return(() => person.Value.Name);
        var bounded = many.Inspect();
        Assert.Equal(many.ToCommand().Text, bounded.Text);
        Assert.Equal(CypherSnapshotStatus.BudgetExceeded, bounded.Semantics!.Status);
        Assert.Empty(bounded.Semantics.Clauses);
    }

    private static void AssertLineSpans(CypherCommand command, string[] prefixes)
    {
        var lines = command.Text.ReplaceLineEndings("\n").Split('\n');
        var clauses = command.Semantics!.Clauses;
        Assert.Equal(prefixes.Length, clauses.Count);
        for (var i = 0; i < clauses.Count; i++)
        {
            Assert.StartsWith(prefixes[i], lines[clauses[i].FirstLine - 1]);
            Assert.Equal(i == 0 ? 1 : clauses[i - 1].LastLine + 1, clauses[i].FirstLine);
        }
        Assert.Equal(lines.Length, clauses[^1].LastLine);
    }
    private sealed class Context : DbContext
    {
        public Context(IDriver? driver = null) : base(new DbContextOptionsBuilder().UseNeo4j(
            driver ?? DispatchProxy.Create<IDriver, GraphEvidenceTests.NeverTransport>(), "offline").Options) { }
        protected override void OnModelCreating(ModelBuilder b) => FluentMovieQueries.Configure(b);
    }
}
