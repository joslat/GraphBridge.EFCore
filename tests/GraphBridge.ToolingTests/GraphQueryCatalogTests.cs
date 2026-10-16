using System.Reflection;
using GraphBridge.Samples.FluentCypher;
using GraphBridge.Tooling.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;

namespace GraphBridge.ToolingTests;

public sealed class GraphQueryCatalogTests
{
    [Fact]
    public void Three_real_registrations_compile_identically_and_keep_explicit_semantics_without_transport()
    {
        var driver = DispatchProxy.Create<IDriver, GraphEvidenceTests.NeverTransport>();
        using var db = new MoviesContext(driver);
        const string secret = "sensitive-fixture-input";
        var catalog = FluentMovieContracts.Create(secret);
        Assert.Equal(new[] { "movies.coactors.v1", "movies.optional.v1", "movies.shared-movies.v1" }, catalog.Contracts.Select(c => c.Definition.Id));
        var expected = new[] { FluentMovieQueries.Coactors(db, secret, "Ada").ToCommand(),
            FluentMovieQueries.OptionalMovies(db, secret, 2020).ToCommand(), FluentMovieQueries.SharedMovies(db, secret, "Ada").ToCommand() };
        for (var i = 0; i < expected.Length; i++)
        {
            var actual = catalog.Contracts[i].Compile(db);
            Assert.Equal(expected[i].Text, actual.Text);
            Assert.Equal(expected[i].Parameters, actual.Parameters);
            Assert.Equal(100, catalog.Contracts[i].Definition.Budget.MaxRows);
            Assert.Equal(TimeSpan.FromSeconds(10), catalog.Contracts[i].Definition.Budget.Deadline);
            Assert.NotNull(catalog.Contracts[i].Definition.ReferenceId);
            Assert.Equal("movies.parallel-credits.v1", catalog.Contracts[i].Definition.FixtureId);
        }
        Assert.Equal(GraphResultComparison.Multiset, catalog.Contracts[1].Definition.Comparison);
        Assert.Equal(GraphDuplicatePolicy.Preserve, catalog.Contracts[1].Definition.Duplicates);
        Assert.Equal(new[] { false, true, true }, catalog.Contracts[1].Definition.Columns.Select(c => c.Nullable));
        Assert.Equal(GraphDuplicatePolicy.Eliminate, catalog.Contracts[0].Definition.Duplicates);
        Assert.Equal(0, ((GraphEvidenceTests.NeverTransport)(object)driver).Calls);
    }

    [Fact]
    public void Offline_doctor_is_deterministic_redacted_and_preserves_the_owned_context_and_tracking()
    {
        var driver = DispatchProxy.Create<IDriver, GraphEvidenceTests.NeverTransport>();
        using var db = new MoviesContext(driver);
        var tracked = new Person { Id = Guid.NewGuid(), RunId = "secret-marker", Name = "secret-person" };
        db.Add(tracked);
        var catalog = FluentMovieContracts.Create("secret-marker", "secret-person");
        var report = GraphDoctor.InspectOffline(db, catalog);
        Assert.Equal(GraphEvidenceStatus.Pass, report.Status);
        Assert.Equal(db.Database.GetGraphSchemaManifest().Fingerprint, report.Model!.Fingerprint);
        Assert.Equal(2, report.Model.Nodes);
        Assert.Equal(2, report.Model.Relationships);
        Assert.Equal(3, report.Queries.Count);
        Assert.All(report.Queries, q => { Assert.Equal(GraphEvidenceStatus.Pass, q.Compilation); Assert.Equal(GraphEvidenceStatus.NotRun, q.Execution); Assert.NotNull(q.Definition); });
        var json = report.ToJson();
        Assert.Equal(json, GraphDoctor.InspectOffline(db, catalog).ToJson());
        Assert.DoesNotContain("secret-", json);
        Assert.DoesNotContain("FGQPerson", json);
        Assert.DoesNotContain("MATCH", json);
        Assert.Equal(EntityState.Added, db.Entry(tracked).State);
        Assert.Single(db.ChangeTracker.Entries());
        Assert.Equal(0, ((GraphEvidenceTests.NeverTransport)(object)driver).Calls);
        var disclosed = GraphDoctor.InspectOffline(db, catalog, includeStatements: true);
        Assert.Contains("FGQPerson", disclosed.ToJson());
        Assert.DoesNotContain("secret-", disclosed.ToJson());
        Assert.True(disclosed.SensitiveTextIncluded);
    }

    [Fact]
    public void Actual_invalid_alias_is_associated_with_its_contract_and_other_queries_still_compile()
    {
        var driver = DispatchProxy.Create<IDriver, GraphEvidenceTests.NeverTransport>();
        using var db = new MoviesContext(driver);
        var normal = FluentMovieContracts.Create("fixture").Contracts;
        var invalid = GraphQueryContract.Fluent(Definition("movies.invalid.v1"), context =>
        {
            var person = Graph.Node<Person>();
            var dropped = Graph.Node<Movie>();
            return context.Cypher().Match(person).Return(() => dropped.Value.Title).Limit(10);
        });
        var report = GraphDoctor.InspectOffline(db, new([.. normal, invalid]));
        Assert.Equal(GraphEvidenceStatus.Unsupported, report.Status);
        Assert.Contains("GBD-QUERY-002", report.ToText());
        Assert.Contains("Action:", report.ToText());
        var failed = Assert.Single(report.Queries, q => q.Compilation != GraphEvidenceStatus.Pass);
        Assert.Equal("movies.invalid.v1", failed.ContractId);
        Assert.Equal("GBD-QUERY-002", failed.Diagnostic!.Code);
        // FC2-07b: the fixed Fluent code is kept as a subcode; the exception text is not.
        Assert.Equal("GBFC303", failed.Diagnostic.Subcode);
        Assert.Contains("\"subcode\": \"GBFC303\"", report.ToJson());
        Assert.DoesNotContain("outside the current clause scope", report.ToJson());
        Assert.Null(failed.StatementFingerprint);
        Assert.Null(failed.Statement);
        Assert.Equal(4, report.Queries.Count);
        Assert.Equal(0, ((GraphEvidenceTests.NeverTransport)(object)driver).Calls);
    }

    [Fact]
    public void Invalid_model_and_precancellation_leave_every_contract_unobserved_and_never_pass()
    {
        var driver = DispatchProxy.Create<IDriver, GraphEvidenceTests.NeverTransport>();
        using var invalid = new InvalidContext(driver);
        var catalog = FluentMovieContracts.Create("fixture");
        var failed = GraphDoctor.InspectOffline(invalid, catalog);
        Assert.Equal(GraphEvidenceStatus.Fail, failed.Status);
        Assert.Equal("GBD-MODEL-001", Assert.Single(failed.Diagnostics).Code);
        Assert.Null(failed.Model);
        Assert.All(failed.Queries, q => Assert.Equal(GraphEvidenceStatus.NotRun, q.Compilation));
        using var valid = new MoviesContext(driver);
        var canceled = GraphDoctor.InspectOffline(valid, catalog, cancellationToken: new CancellationToken(true));
        Assert.Equal(GraphEvidenceStatus.Blocked, canceled.Status);
        Assert.Equal("GBD-EXEC-001", Assert.Single(canceled.Diagnostics).Code);
        Assert.All(canceled.Queries, q => Assert.Equal(GraphEvidenceStatus.NotRun, q.Compilation));
        Assert.Equal(0, ((GraphEvidenceTests.NeverTransport)(object)driver).Calls);
    }

    [Fact]
    public void Cancellation_during_a_factory_does_not_mark_it_or_later_contracts_successful()
    {
        var driver = DispatchProxy.Create<IDriver, GraphEvidenceTests.NeverTransport>();
        using var db = new MoviesContext(driver);
        using var cancel = new CancellationTokenSource();
        var first = GraphQueryContract.Fluent(Definition("aaa.cancel.v1"), context =>
        {
            cancel.Cancel();
            return FluentMovieQueries.Coactors(context, "fixture", "Ada");
        });
        var report = GraphDoctor.InspectOffline(db, new([first, .. FluentMovieContracts.Create("fixture").Contracts]), cancellationToken: cancel.Token);
        Assert.Equal(GraphEvidenceStatus.Blocked, report.Status);
        Assert.Equal(GraphEvidenceStatus.Blocked, report.Queries[0].Compilation);
        Assert.All(report.Queries.Skip(1), q => Assert.Equal(GraphEvidenceStatus.NotRun, q.Compilation));
        Assert.Equal(0, ((GraphEvidenceTests.NeverTransport)(object)driver).Calls);
    }

    [Fact]
    public void Human_export_reuses_retained_evidence_without_values_or_culture_dependent_formatting()
    {
        var driver = DispatchProxy.Create<IDriver, GraphEvidenceTests.NeverTransport>();
        using var db = new MoviesContext(driver);
        var catalog = FluentMovieContracts.Create("private-fixture-value");
        var report = GraphDoctor.InspectOffline(db, catalog);
        var text = report.ToText();
        Assert.Equal(text, report.ToText());
        Assert.Contains("Database observation: not-run", text);
        Assert.Contains("movies.optional.v1", text);
        Assert.Contains("Declared comparison: multiset | duplicates: preserve", text);
        Assert.DoesNotContain("private-fixture-value", text);
        Assert.DoesNotContain("FGQPerson", text);
        Assert.DoesNotContain("\r", text);
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new("fr-FR");
            Assert.Equal(text, report.ToText());
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
        var explicitText = GraphDoctor.InspectOffline(db, catalog, includeStatements: true).ToText();
        Assert.Contains("SENSITIVE TEXT INCLUDED", explicitText);
        Assert.Contains("FGQPerson", explicitText);
        Assert.DoesNotContain("private-fixture-value", explicitText);
        Assert.Equal(0, ((GraphEvidenceTests.NeverTransport)(object)driver).Calls);
    }

    [Fact]
    public void Catalog_declarations_are_immutable_bounded_and_reject_ambiguous_identifiers()
    {
        var fields = new List<GraphResultColumn> { new("name", GraphResultKind.String) };
        var features = new List<string> { "with", "distinct", "with" };
        var definition = new GraphQueryDefinition("query.v1", "Public description", "sample.cs", fields,
            GraphResultComparison.Multiset, GraphDuplicatePolicy.Preserve, new(100, TimeSpan.FromSeconds(1)), features);
        fields.Clear(); features.Clear();
        Assert.Single(definition.Columns);
        Assert.Equal(new[] { "distinct", "with" }, definition.RequiredFeatures);
        Assert.Throws<ArgumentException>(() => new GraphQueryCatalog([null!]));
        var contract = FluentMovieContracts.Create("fixture").Contracts[0];
        Assert.Throws<ArgumentException>(() => new GraphQueryCatalog([contract, contract]));
        Assert.Throws<ArgumentException>(() => new GraphQueryCatalog(Enumerable.Repeat(contract, 257)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphQueryBudget(0, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphQueryBudget(1, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphQueryBudget(1, TimeSpan.FromSeconds(1), 33));
        Assert.Throws<ArgumentException>(() => new GraphQueryDefinition("query.v1", "description", "source", [],
            GraphResultComparison.Ordered, GraphDuplicatePolicy.Preserve, new(10, TimeSpan.FromSeconds(1))));
        Assert.Throws<ArgumentException>(() => new GraphQueryDefinition("query.v1", "description", "source", [new("name", GraphResultKind.String)],
            GraphResultComparison.Ordered, GraphDuplicatePolicy.Preserve, new(10, TimeSpan.FromSeconds(1)), referenceId: "reference.v1"));
    }

    private static GraphQueryDefinition Definition(string id) => new(id, "Public synthetic query", "test.cs",
        [new("name", GraphResultKind.String)], GraphResultComparison.Ordered, GraphDuplicatePolicy.Preserve, new(10, TimeSpan.FromSeconds(1)));
    private sealed class MoviesContext(IDriver driver) : DbContext(new DbContextOptionsBuilder().UseNeo4j(driver, "offline").Options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => FluentMovieQueries.Configure(modelBuilder);
    }
    private sealed class NoKey { public string Name { get; set; } = ""; }
    private sealed class InvalidContext(IDriver driver) : DbContext(new DbContextOptionsBuilder().UseNeo4j(driver, "offline").Options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<NoKey>().ToGraphNode("InvalidSecretLabel");
    }
}
