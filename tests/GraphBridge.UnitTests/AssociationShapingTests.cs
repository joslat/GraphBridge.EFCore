using System.Globalization;
using System.Linq.Expressions;
using GraphBridge.EntityFrameworkCore.Cypher;
using GraphBridge.EntityFrameworkCore.Metadata;
using GraphBridge.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using Neo4j.Driver;

namespace GraphBridge.UnitTests;

/// <summary>
/// Isolated row-to-EF shaping experiment. Native projected values are scripted; EF materializers,
/// query tracking, snapshots and fix-up are real. Production association query roots remain disabled.
/// </summary>
public sealed class AssociationShapingTests : IDisposable
{
    private static readonly Guid ActorId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid FilmId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private readonly IDriver _driver = ProviderTestDriver.Create();
    public void Dispose() => Assert.Empty(((ProviderTestDriver)(object)_driver).Calls);

    [Theory]
    [InlineData(0, 1, 2)]
    [InlineData(0, 2, 1)]
    [InlineData(1, 0, 2)]
    [InlineData(1, 2, 0)]
    [InlineData(2, 0, 1)]
    [InlineData(2, 1, 0)]
    public void Query_materializers_fix_up_explicit_join_reference_inverse_and_skip_navigations_in_every_row_order(
        int first, int second, int third)
    {
        using var context = Context();
        var tracked = new List<(object Entity, bool FromQuery)>();
        context.ChangeTracker.Tracked += (_, args) => tracked.Add((args.Entry.Entity, args.FromQuery));
        var harness = new ShapingHarness(context, QueryTrackingBehavior.TrackAll);
        var actorRead = Node<Actor>(context);
        var filmRead = Node<Film>(context);
        var creditRead = Association<Credit>(context);
        var actorShape = harness.Compile<Actor>(actorRead);
        var filmShape = harness.Compile<Film>(filmRead);
        var creditShape = harness.Compile<Credit>(creditRead);
        var results = new object[3];
        foreach (var row in new[] { first, second, third })
        {
            results[row] = row switch
            {
                0 => harness.Read(actorRead, NativeRow(actorRead, ("Id", ActorId.ToString("D")), ("Name", "Ada")), actorShape),
                1 => harness.Read(filmRead, NativeRow(filmRead, ("Id", FilmId.ToString("D")), ("Title", "Graph film")), filmShape),
                _ => harness.Read(creditRead, CreditRow(creditRead), creditShape)
            };
        }

        var actor = Assert.IsType<Actor>(results[0]);
        var film = Assert.IsType<Film>(results[1]);
        var credit = Assert.IsType<Credit>(results[2]);
        Assert.Equal(3, tracked.Count);
        Assert.All(tracked, entry => Assert.True(entry.FromQuery));
        Assert.Same(actor, credit.Actor);
        Assert.Same(film, credit.Film);
        Assert.Same(film, Assert.Single(actor.Films));
        Assert.Same(actor, Assert.Single(film.Actors));
        Assert.Same(credit, Assert.Single(actor.Credits));
        Assert.Same(credit, Assert.Single(film.Credits));
        Assert.All(context.ChangeTracker.Entries(), entry => Assert.Equal(EntityState.Unchanged, entry.State));
        var original = context.Entry(credit).OriginalValues;
        Assert.Equal(ActorId, original[nameof(Credit.ActorId)]);
        Assert.Equal(FilmId, original[nameof(Credit.FilmId)]);
        Assert.Equal("lead", original[nameof(Credit.Role)]);
        Assert.Equal(12.50m, original[nameof(Credit.Fee)]);
        Assert.Equal(5, original[nameof(Credit.Rating)]);
        Assert.Equal(7L, original[nameof(Credit.Version)]);
        Assert.Equal(12.50m, credit.Fee);
        Assert.Equal(5, credit.Rating);
        Assert.False(context.Entry(actor).Collection(e => e.Films).IsLoaded);
        Assert.False(context.Entry(film).Collection(e => e.Actors).IsLoaded);

        credit.Role = "updated locally";
        context.ChangeTracker.DetectChanges();
        Assert.Equal(EntityState.Modified, context.Entry(credit).State);
        Assert.Equal(nameof(Credit.Role), Assert.Single(context.Entry(credit).Properties, p => p.IsModified).Metadata.Name);
        Assert.Equal("lead", context.Entry(credit).Property(e => e.Role).OriginalValue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Named_dictionary_join_uses_EF_indexer_materialization_and_skip_fixup(bool joinFirst)
    {
        using var context = Context();
        var harness = new ShapingHarness(context, QueryTrackingBehavior.TrackAll);
        var actorRead = Node<BagActor>(context);
        var filmRead = Node<BagFilm>(context);
        var type = context.Model.FindEntityType("ImplicitCredit")!;
        var joinRead = GraphReadBinding.FromAssociation(context.GetService<GraphBindingCompiler>().GetAssociation(type));
        var joinShape = harness.Compile<Dictionary<string, object>>(joinRead);
        var joinRow = NativeRow(joinRead, ("ActorId", ActorId.ToString("D")), ("FilmId", FilmId.ToString("D")), ("Note", null));
        Dictionary<string, object>? join = joinFirst ? harness.Read(joinRead, joinRow, joinShape) : null;
        var actor = harness.Read(actorRead, NativeRow(actorRead, ("Id", ActorId.ToString("D"))), harness.Compile<BagActor>(actorRead));
        var film = harness.Read(filmRead, NativeRow(filmRead, ("Id", FilmId.ToString("D"))), harness.Compile<BagFilm>(filmRead));
        join ??= harness.Read(joinRead, joinRow, joinShape);

        Assert.Equal(ActorId, join["ActorId"]);
        Assert.Equal(FilmId, join["FilmId"]);
        Assert.Null(join["Note"]);
        Assert.Same(film, Assert.Single(actor.Films));
        Assert.Same(actor, Assert.Single(film.Actors));
        var entry = Assert.Single(context.ChangeTracker.Entries(), e => e.Metadata == type);
        Assert.Same(join, entry.Entity);
        Assert.Equal(EntityState.Unchanged, entry.State);
        Assert.Equal(ActorId, entry.Property("ActorId").OriginalValue);
        Assert.Same(join, harness.Read(joinRead, joinRow, joinShape));
        Assert.Equal(3, context.ChangeTracker.Entries().Count());
    }

    [Fact]
    public void Repeated_projected_identity_reuses_tracked_join_without_overwriting_local_changes_or_originals()
    {
        using var context = Context();
        var harness = new ShapingHarness(context, QueryTrackingBehavior.TrackAll);
        var read = Association<Credit>(context);
        var shape = harness.Compile<Credit>(read);
        var credit = harness.Read(read, CreditRow(read), shape);
        credit.Role = "local modification";
        context.ChangeTracker.DetectChanges();
        var repeatedRow = CreditRow(read);
        repeatedRow[Alias(read, nameof(Credit.Role))] = "different projection payload";
        repeatedRow[Alias(read, nameof(Credit.Version))] = 99L;

        Assert.Same(credit, harness.Read(read, repeatedRow, shape));
        Assert.Equal("local modification", credit.Role);
        Assert.Equal(7L, credit.Version);
        Assert.Equal("lead", context.Entry(credit).Property(e => e.Role).OriginalValue);
        Assert.Equal(7L, context.Entry(credit).Property(e => e.Version).OriginalValue);
        Assert.Single(context.ChangeTracker.Entries());
        // This is EF identity behavior for repeated projected keys, not acceptance of parallel physical edges.
        // A future graph executor must reject physical association duplicates before invoking the shaper.
    }

    [Theory]
    [InlineData(QueryTrackingBehavior.NoTracking, false)]
    [InlineData(QueryTrackingBehavior.NoTrackingWithIdentityResolution, true)]
    public void Untracked_modes_preserve_their_identity_contract_within_one_query_context(
        QueryTrackingBehavior tracking, bool sameIdentity)
    {
        using var context = Context();
        var harness = new ShapingHarness(context, tracking);
        var read = Association<Credit>(context);
        var shape = harness.Compile<Credit>(read);
        var first = harness.Read(read, CreditRow(read), shape);
        var second = harness.Read(read, CreditRow(read), shape);
        Assert.Equal(sameIdentity, ReferenceEquals(first, second));
        Assert.Equal(ActorId, first.ActorId);
        Assert.Equal(12.50m, first.Fee);
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Null(first.Actor);
        Assert.Null(first.Film);
    }

    [Theory]
    [InlineData(QueryTrackingBehavior.NoTracking)]
    [InlineData(QueryTrackingBehavior.NoTrackingWithIdentityResolution)]
    public void Untracked_association_query_cannot_fix_up_endpoints_in_the_context_state_manager(QueryTrackingBehavior tracking)
    {
        using var context = Context();
        var tracked = new ShapingHarness(context, QueryTrackingBehavior.TrackAll);
        var actorRead = Node<Actor>(context);
        var filmRead = Node<Film>(context);
        var actor = tracked.Read(actorRead, NativeRow(actorRead, ("Id", ActorId.ToString("D")), ("Name", "Ada")), tracked.Compile<Actor>(actorRead));
        var film = tracked.Read(filmRead, NativeRow(filmRead, ("Id", FilmId.ToString("D")), ("Title", "Graph film")), tracked.Compile<Film>(filmRead));
        var untracked = new ShapingHarness(context, tracking);
        var creditRead = Association<Credit>(context);
        var credit = untracked.Read(creditRead, CreditRow(creditRead), untracked.Compile<Credit>(creditRead));

        Assert.Null(credit.Actor);
        Assert.Null(credit.Film);
        Assert.Empty(actor.Films);
        Assert.Empty(actor.Credits);
        Assert.Empty(film.Actors);
        Assert.Empty(film.Credits);
        Assert.Equal(2, context.ChangeTracker.Entries().Count());
        Assert.All(context.ChangeTracker.Entries(), entry => Assert.Equal(EntityState.Unchanged, entry.State));
        Assert.False(context.Entry(actor).Collection(e => e.Films).IsLoaded);
    }

    [Theory]
    [InlineData("required-null")]
    [InlineData("malformed-guid")]
    [InlineData("integer-overflow")]
    [InlineData("malformed-converter-value")]
    [InlineData("missing-column")]
    [InlineData("duplicate-relationships")]
    [InlineData("missing-cardinality")]
    public void Invalid_native_row_is_rejected_before_any_partial_entity_is_tracked(string failure)
    {
        using var context = Context();
        var fromQueryEvents = 0;
        context.ChangeTracker.Tracked += (_, _) => fromQueryEvents++;
        var harness = new ShapingHarness(context, QueryTrackingBehavior.TrackAll);
        var read = Association<Credit>(context);
        var shape = harness.Compile<Credit>(read);
        var row = CreditRow(read);
        Type expected;
        switch (failure)
        {
            case "duplicate-relationships":
                row[CypherQueryPlan.AssociationCountAlias] = 2L;
                expected = typeof(InvalidOperationException);
                break;
            case "missing-cardinality":
                row.Remove(CypherQueryPlan.AssociationCountAlias);
                expected = typeof(InvalidOperationException);
                break;
            case "required-null":
                row[Alias(read, nameof(Credit.ActorId))] = null;
                expected = typeof(InvalidOperationException);
                break;
            case "malformed-guid":
                row[Alias(read, nameof(Credit.ActorId))] = "not a guid";
                expected = typeof(FormatException);
                break;
            case "integer-overflow":
                row[Alias(read, nameof(Credit.Rating))] = long.MaxValue;
                expected = typeof(OverflowException);
                break;
            case "malformed-converter-value":
                row[Alias(read, nameof(Credit.Fee))] = "not decimal";
                expected = typeof(FormatException);
                break;
            default:
                row.Remove(Alias(read, nameof(Credit.Version)));
                expected = typeof(KeyNotFoundException);
                break;
        }
        Assert.Throws(expected, () => harness.Read(read, row, shape));
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Equal(0, fromQueryEvents);
    }

    [Fact]
    public void Production_association_query_root_uses_same_guarded_projection_without_transport_for_query_text()
    {
        using var context = Context();
        Assert.Contains("__gb_association_count", context.Set<Credit>().ToQueryString());
    }

    private ProviderModelContext Context() => new(_driver, Configure);
    private static GraphReadBinding Node<T>(DbContext context)
        => GraphReadBinding.FromNode(context.GetService<GraphBindingCompiler>().GetNode(context.Model.FindEntityType(typeof(T))!));
    private static GraphReadBinding Association<T>(DbContext context)
        => GraphReadBinding.FromAssociation(context.GetService<GraphBindingCompiler>().GetAssociation(context.Model.FindEntityType(typeof(T))!));
    private static string Alias(GraphReadBinding read, string property)
        => read.Properties.Single(p => p.ModelProperty.Property.Name == property).Column.Alias;
    private static Dictionary<string, object?> CreditRow(GraphReadBinding read)
        => NativeRow(read, (nameof(Credit.ActorId), ActorId.ToString("D")), (nameof(Credit.FilmId), FilmId.ToString("D")),
            (nameof(Credit.Role), "lead"), (nameof(Credit.Fee), "12.50"), (nameof(Credit.Rating), 5L), (nameof(Credit.Version), 7L));

    private static Dictionary<string, object?> NativeRow(GraphReadBinding read, params (string Property, object? Value)[] values)
    {
        var byName = values.ToDictionary(pair => pair.Property, pair => pair.Value);
        // Deliberately reverse insertion order: aliases/EF slots, not dictionary order, control decoding.
        var row = read.Properties.Reverse().ToDictionary(slot => slot.Column.Alias, slot => byName[slot.ModelProperty.Property.Name]);
        if (read.Root is CypherAssociationRoot)
            row[CypherQueryPlan.AssociationCountAlias] = 1L;
        return row;
    }

    private sealed class ShapingHarness
    {
        private readonly QueryContext _queryContext;
        private readonly MaterializerProbe _probe;

        public ShapingHarness(DbContext context, QueryTrackingBehavior tracking)
        {
            context.ChangeTracker.QueryTrackingBehavior = tracking;
            var compilation = context.GetService<IQueryCompilationContextFactory>().Create(async: true);
            Assert.Equal(tracking, compilation.QueryTrackingBehavior);
            _probe = new MaterializerProbe(context.GetService<ShapedQueryCompilingExpressionVisitorDependencies>(), compilation);
            _queryContext = context.GetService<IQueryContextFactory>().Create();
            _queryContext.InitializeStateManager(standAlone: tracking == QueryTrackingBehavior.NoTrackingWithIdentityResolution);
        }

        public Func<QueryContext, ValueBuffer, T> Compile<T>(GraphReadBinding read) => _probe.Compile<T>(read.EntityType);

        public T Read<T>(GraphReadBinding read, IReadOnlyDictionary<string, object?> nativeRow,
            Func<QueryContext, ValueBuffer, T> shaper)
        {
            // Test-only row adapter mirrors the provider's conversion-before-shaping boundary.
            // No IRecord/session/driver protocol or production query-root translation is exercised.
            return shaper(_queryContext, read.ReadRow(nativeRow));
        }
    }

    private sealed class MaterializerProbe(ShapedQueryCompilingExpressionVisitorDependencies dependencies,
        QueryCompilationContext compilation) : ShapedQueryCompilingExpressionVisitor(dependencies, compilation)
    {
        public Func<QueryContext, ValueBuffer, T> Compile<T>(IEntityType entityType)
        {
            var row = Expression.Parameter(typeof(ValueBuffer), "row");
            var body = InjectStructuralTypeMaterializers(new StructuralTypeShaperExpression(entityType, row, nullable: false));
            return Expression.Lambda<Func<QueryContext, ValueBuffer, T>>(body, QueryCompilationContext.QueryContextParameter, row).Compile();
        }

        protected override Expression VisitShapedQuery(ShapedQueryExpression shapedQueryExpression)
            => throw new NotSupportedException("This test probe injects EF materializers only; it does not implement query translation or execution.");
    }

    private static void Configure(ModelBuilder model)
    {
        model.Entity<Actor>().ToGraphNode("ShapingActor").Property(e => e.Id).HasGraphPropertyName("actorId");
        model.Entity<Film>().ToGraphNode("ShapingFilm").Property(e => e.Id).HasGraphPropertyName("filmId");
        model.Entity<Actor>().HasMany(e => e.Films).WithMany(e => e.Actors).UsingEntity<Credit>(
            right => right.HasOne(e => e.Film).WithMany(e => e.Credits).HasForeignKey(e => e.FilmId),
            left => left.HasOne(e => e.Actor).WithMany(e => e.Credits).HasForeignKey(e => e.ActorId),
            join =>
            {
                join.HasKey(e => new { e.ActorId, e.FilmId });
                join.ToGraphRelationship("ACTED_IN", [nameof(Credit.ActorId)], [nameof(Credit.FilmId)]);
                join.Property(e => e.Role).HasGraphPropertyName("role");
                join.Property(e => e.Fee).HasConversion(value => value.ToString(CultureInfo.InvariantCulture),
                    value => decimal.Parse(value, CultureInfo.InvariantCulture));
                join.Property(e => e.Version).IsRowVersion();
            });
        model.Entity<BagActor>().ToGraphNode("BagActor").HasMany(e => e.Films).WithMany(e => e.Actors)
            .UsingEntity<Dictionary<string, object>>("ImplicitCredit",
                right => right.HasOne<BagFilm>().WithMany().HasForeignKey("FilmId"),
                left => left.HasOne<BagActor>().WithMany().HasForeignKey("ActorId"),
                join =>
                {
                    join.ToGraphRelationship("TAGGED", ["ActorId"], ["FilmId"]);
                    join.IndexerProperty<string?>("Note");
                });
        model.Entity<BagFilm>().ToGraphNode("BagFilm");
    }

    public sealed class Actor
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public List<Film> Films { get; } = [];
        public List<Credit> Credits { get; } = [];
    }
    public sealed class Film
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = "";
        public List<Actor> Actors { get; } = [];
        public List<Credit> Credits { get; } = [];
    }
    public sealed class Credit
    {
        public Guid ActorId { get; set; }
        public Guid FilmId { get; set; }
        public string? Role { get; set; }
        public decimal Fee { get; set; }
        public int? Rating { get; set; }
        public long Version { get; set; }
        public Actor Actor { get; set; } = null!;
        public Film Film { get; set; } = null!;
    }
    public sealed class BagActor { public Guid Id { get; set; } public List<BagFilm> Films { get; } = []; }
    public sealed class BagFilm { public Guid Id { get; set; } public List<BagActor> Actors { get; } = []; }
}
