using GraphBridge.EntityFrameworkCore.Cypher;
using GraphBridge.EntityFrameworkCore.Metadata;
using GraphBridge.EntityFrameworkCore.Update;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;
using Neo4j.Driver;

namespace GraphBridge.UnitTests;

/// <summary>Actual EF update entries captured by a test-only database; no graph writes or simulated graph store.</summary>
public sealed class AssociationMutationTests : IDisposable
{
    private static readonly Guid ActorId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid FilmId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid OtherFilmId = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private readonly IDriver _driver = ProviderTestDriver.Create();
    public void Dispose() => Assert.Empty(((ProviderTestDriver)(object)_driver).Calls);

    [Fact]
    public async Task New_endpoints_and_join_plan_two_node_creates_before_native_edge_and_count_each_EF_entry_once()
    {
        using var context = Context();
        var actor = new Actor { Name = "new actor" };
        var film = new Film { Title = "new film" };
        actor.Films.Add(film);
        context.Add(actor);
        context.ChangeTracker.DetectChanges();
        var join = Assert.Single(context.ChangeTracker.Entries<Credit>());
        join.Entity.Role = "O'Reilly `$payload` MATCH (n) DETACH DELETE n";

        var plan = await Plan(context);
        Assert.Equal(3, plan.ExpectedStateEntries);
        Assert.Equal(new[] { GraphMutationPhase.NodeCreate, GraphMutationPhase.NodeCreate, GraphMutationPhase.AssociationAdd },
            plan.Operations.Select(p => p.Phase));
        Assert.Equal(new[] { "Actor", "Film" }, plan.RequiredNodeBindings.Select(b => b.Label));
        var edge = plan.Operations[2];
        Assert.Same(join.Entity, edge.Entry.ToEntityEntry().Entity);
        Assert.Equal(2, edge.LockCommands.Count);
        Assert.Empty(plan.Operations[0].LockCommands);
        Assert.Empty(plan.Operations[1].LockCommands);
        Assert.Equal("MATCH (n:`Actor` {`actorId`: $k0})\nSET n.`__graphbridge_lock` = true REMOVE n.`__graphbridge_lock`\nRETURN count(n) AS count", edge.LockCommands[0].Text);
        Assert.Equal("MATCH (n:`Film` {`filmId`: $k0})\nSET n.`__graphbridge_lock` = true REMOVE n.`__graphbridge_lock`\nRETURN count(n) AS count", edge.LockCommands[1].Text);
        Assert.Equal(actor.Id.ToString("D"), edge.LockCommands[0].Parameters["k0"]);
        Assert.Equal(film.Id.ToString("D"), edge.LockCommands[1].Parameters["k0"]);
        Assert.Contains("MERGE (s)-[r:`ACTED_IN`]->(t)", edge.Command.Text);
        Assert.DoesNotContain("CREATE (n:", edge.Command.Text);
        Assert.DoesNotContain("O'Reilly", edge.Command.Text);
        var payload = Map(edge.Command, "properties");
        Assert.Equal(join.Entity.Role, payload["role"]);
        Assert.Null(payload["note"]);
        Assert.Null(payload["token"]);
        Assert.Equal(1L, payload["version"]);
        Assert.Equal(4, payload.Count);
        Assert.DoesNotContain("ActorId", payload.Keys);
        Assert.DoesNotContain("FilmId", payload.Keys);
        Assert.Equal(0L, join.Entity.Version);
        Assert.Equal(EntityState.Added, join.State);
        Assert.Equal(2, plan.GeneratedValues.Count); // Actor version + association version; film has none.
        Assert.Equal(1L, Assert.Single(plan.GeneratedValues, p => ReferenceEquals(p.Entry.ToEntityEntry().Entity, join.Entity)).Value);
        Assert.All(context.ChangeTracker.Entries(), e => Assert.Equal(EntityState.Added, e.State));
    }

    [Fact]
    public async Task Existing_endpoint_add_requires_both_node_bindings_and_full_payload_for_idempotent_merge()
    {
        using var context = Context();
        var (actor, film) = AttachEndpoints(context);
        actor.Films.Add(film);
        var plan = await Plan(context);
        var edge = Assert.Single(plan.Operations);
        Assert.Equal(GraphMutationPhase.AssociationAdd, edge.Phase);
        Assert.Equal(1, plan.ExpectedStateEntries);
        Assert.Equal(2, plan.RequiredNodeBindings.Count);
        Assert.Contains("ON CREATE SET r = $properties", edge.Command.Text);
        Assert.DoesNotContain("ON MATCH", edge.Command.Text);
        Assert.Contains("LIMIT 2", edge.Command.Text);
        Assert.Contains("version", edge.Command.Text);
        Assert.Contains("IS NULL", edge.Command.Text);
        Assert.Equal(4, Map(edge.Command, "properties").Count);
        Assert.Equal(EntityState.Unchanged, context.Entry(actor).State);
        Assert.Equal(EntityState.Unchanged, context.Entry(film).State);
        Assert.Equal(ActorId.ToString("D"), edge.Command.Parameters["s0"]);
        Assert.Equal(FilmId.ToString("D"), edge.Command.Parameters["t0"]);
    }

    [Fact]
    public async Task Modified_payload_uses_original_tokens_only_changed_properties_and_pending_numeric_version()
    {
        using var context = Context();
        var credit = AttachCredit(context, role: "old role", version: 7);
        credit.Role = null;
        var plan = await Plan(context);
        var edge = Assert.Single(plan.Operations);
        Assert.Equal(GraphMutationPhase.AssociationUpdate, edge.Phase);
        var changes = Map(edge.Command, "changes");
        Assert.Equal(2, changes.Count);
        Assert.Null(changes["role"]);
        Assert.Equal(8L, changes["version"]);
        Assert.DoesNotContain("note", changes.Keys);
        Assert.DoesNotContain("token", changes.Keys);
        Assert.Contains(7L, edge.Command.Parameters.Values);
        Assert.Contains(edge.Command.Parameters, p => p.Key.StartsWith("o", StringComparison.Ordinal) && p.Value is null);
        Assert.Equal(ActorId.ToString("D"), edge.Command.Parameters["s0"]);
        Assert.Equal(FilmId.ToString("D"), edge.Command.Parameters["t0"]);
        Assert.Equal(7L, credit.Version);
        Assert.Equal(8L, Assert.Single(plan.GeneratedValues).Value);
        Assert.Equal("old role", context.Entry(credit).Property(p => p.Role).OriginalValue);
        Assert.True(context.Entry(credit).Property(p => p.Role).IsModified);
    }

    [Fact]
    public async Task Caller_managed_token_checks_original_and_writes_current_value_in_same_edge_update()
    {
        using var context = Context();
        var credit = AttachCredit(context, role: "role", version: 4);
        credit.Token = "new token' `$x`";
        var edge = Assert.Single((await Plan(context)).Operations);
        Assert.Null(context.Entry(credit).Property(p => p.Token).OriginalValue);
        Assert.Equal(credit.Token, Map(edge.Command, "changes")["token"]);
        Assert.DoesNotContain(credit.Token, edge.Command.Text);
        Assert.Contains(edge.Command.Parameters, p => p.Key.StartsWith("o", StringComparison.Ordinal) && p.Value is null);
    }

    [Fact]
    public async Task Deleted_join_uses_original_endpoint_keys_tokens_and_never_deletes_a_node_or_generates_values()
    {
        using var context = Context();
        var credit = AttachCredit(context, role: "role", version: 9);
        credit.Token = "current token is not the original";
        context.Remove(credit);
        var plan = await Plan(context);
        var edge = Assert.Single(plan.Operations);
        Assert.Equal(GraphMutationPhase.AssociationDelete, edge.Phase);
        Assert.Equal(ActorId.ToString("D"), edge.Command.Parameters["s0"]);
        Assert.Equal(FilmId.ToString("D"), edge.Command.Parameters["t0"]);
        Assert.Contains(9L, edge.Command.Parameters.Values);
        Assert.DoesNotContain(credit.Token, edge.Command.Parameters.Values);
        Assert.Contains("DELETE", edge.Command.Text);
        Assert.DoesNotContain("DELETE n", edge.Command.Text);
        Assert.DoesNotContain("DETACH", edge.Command.Text);
        Assert.False(edge.Command.Parameters.ContainsKey("changes"));
        Assert.Empty(plan.GeneratedValues);
        Assert.Equal(EntityState.Deleted, context.Entry(credit).State);
    }

    [Fact]
    public async Task Replacing_endpoint_plans_old_edge_delete_before_new_edge_add_despite_EF_entry_order()
    {
        using var context = Context();
        var oldCredit = AttachCredit(context, role: "old", version: 3);
        var actor = Assert.Single(context.ChangeTracker.Entries<Actor>()).Entity;
        var oldFilm = Assert.Single(context.ChangeTracker.Entries<Film>()).Entity;
        var newFilm = new Film { Id = OtherFilmId };
        context.Attach(newFilm);
        actor.Films.Remove(oldFilm);
        actor.Films.Add(newFilm);
        var plan = await Plan(context);
        Assert.Equal(2, plan.ExpectedStateEntries);
        Assert.Equal(new[] { GraphMutationPhase.AssociationDelete, GraphMutationPhase.AssociationAdd }, plan.Operations.Select(p => p.Phase));
        Assert.Same(oldCredit, plan.Operations[0].Entry.ToEntityEntry().Entity);
        Assert.Equal(FilmId.ToString("D"), plan.Operations[0].Command.Parameters["t0"]);
        Assert.Equal(OtherFilmId.ToString("D"), plan.Operations[1].Command.Parameters["t0"]);
        Assert.Equal(EntityState.Added, context.Capture.Entries[0].EntityState);
    }

    [Fact]
    public async Task Tracked_cascade_removes_edge_before_endpoint_node_and_preserves_node_planner_contract()
    {
        using var context = Context();
        var credit = AttachCredit(context, role: "role", version: 2);
        var actor = Assert.Single(context.ChangeTracker.Entries<Actor>()).Entity;
        context.Remove(actor);
        var plan = await Plan(context);
        Assert.Equal(new[] { GraphMutationPhase.AssociationDelete, GraphMutationPhase.NodeDelete }, plan.Operations.Select(p => p.Phase));
        Assert.Same(credit, plan.Operations[0].Entry.ToEntityEntry().Entity);
        Assert.Same(actor, plan.Operations[1].Entry.ToEntityEntry().Entity);
        Assert.Equal(2, plan.ExpectedStateEntries);
        Assert.Equal(3, plan.Operations.Sum(o => o.LockCommands.Count));
        var originalNodePlan = Assert.Single(new GraphNodeMutationPlanner(context.GetService<GraphBindingCompiler>())
            .Plan(context.Capture.Entries.Where(e => e.EntityType.ClrType == typeof(Actor)).ToList()));
        Assert.Equal(originalNodePlan.Command.Text, plan.Operations[1].Command.Text);
        Assert.Equal(originalNodePlan.Command.Parameters, plan.Operations[1].Command.Parameters);
        Assert.Equal(originalNodePlan.LockCommand!.Text, Assert.Single(plan.Operations[1].LockCommands).Text);
    }

    [Fact]
    public async Task Unified_plan_orders_all_six_phases_and_preserves_input_order_within_each_phase()
    {
        using var context = Context();
        var deletedCredit = AttachCredit(context, role: "delete", version: 2);
        context.Remove(deletedCredit);
        var modifiedCredit = new Credit { ActorId = ActorId, FilmId = OtherFilmId, Role = "old", Version = 4 };
        context.Attach(new Film { Id = OtherFilmId });
        context.Attach(modifiedCredit);
        modifiedCredit.Role = "new";
        var actor = Assert.Single(context.ChangeTracker.Entries<Actor>()).Entity;
        actor.Name = "changed actor";
        var newFilmA = new Film { Id = Guid.NewGuid(), Title = "A" };
        var newFilmB = new Film { Id = Guid.NewGuid(), Title = "B" };
        context.AddRange(newFilmA, newFilmB);
        actor.Films.Add(newFilmA);
        context.Remove(Assert.Single(context.ChangeTracker.Entries<Film>(), e => e.Entity.Id == FilmId).Entity);
        var plan = await Plan(context);
        Assert.Equal(new[]
        {
            GraphMutationPhase.NodeCreate, GraphMutationPhase.NodeCreate, GraphMutationPhase.NodeUpdate,
            GraphMutationPhase.AssociationDelete, GraphMutationPhase.AssociationUpdate,
            GraphMutationPhase.AssociationAdd, GraphMutationPhase.NodeDelete
        }, plan.Operations.Select(p => p.Phase));
        Assert.Equal(7, plan.ExpectedStateEntries);
        var inputNewNodes = context.Capture.Entries.Where(e => e.EntityType.ClrType == typeof(Film) && e.EntityState == EntityState.Added).ToArray();
        Assert.Equal(inputNewNodes, plan.Operations.Take(2).Select(o => o.Entry));
        Assert.Throws<NotSupportedException>(() => ((IList<GraphMutationOperation>)plan.Operations).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<NodeBinding>)plan.RequiredNodeBindings).Clear());
    }

    [Fact]
    public async Task Planning_does_not_apply_generated_values_and_repeated_unaccepted_save_has_same_plan()
    {
        using var context = Context();
        var (actor, film) = AttachEndpoints(context);
        actor.Films.Add(film);
        var first = await Plan(context);
        var join = Assert.Single(context.ChangeTracker.Entries<Credit>());
        Assert.Equal(0L, join.Entity.Version);
        var second = await Plan(context);
        Assert.Equal(first.ExpectedStateEntries, second.ExpectedStateEntries);
        Assert.Equal(first.Operations[0].Command.Text, second.Operations[0].Command.Text);
        Assert.Equal(Map(first.Operations[0].Command, "properties"), Map(second.Operations[0].Command, "properties"));
        Assert.Equal(0L, join.Entity.Version);
        Assert.Equal(EntityState.Added, join.State);
        context.ChangeTracker.AcceptAllChanges(); // Test snapshot only: no claim anything was persisted.
        var empty = new GraphMutationPlanner(context.GetService<GraphBindingCompiler>()).Plan(context.Capture.Entries);
        Assert.Empty(empty.Operations);
        Assert.Empty(empty.RequiredNodeBindings);
        Assert.Empty(empty.GeneratedValues);
        Assert.Equal(0, empty.ExpectedStateEntries);
    }

    [Fact]
    public async Task Shared_identity_replacement_is_rejected_with_real_EF_entries_before_any_execution()
    {
        using var context = Context();
        var old = AttachCredit(context, role: "old", version: 2);
        context.Remove(old);
        context.Add(new Credit { ActorId = ActorId, FilmId = FilmId, Role = "new" });
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => context.SaveChangesAsync(false));
        Assert.Contains("Shared-identity", error.Message);
        Assert.Contains(context.Capture.Entries, e => e.SharedIdentityEntry is not null);
        Assert.Null(context.Capture.Plan);
    }

    [Fact]
    public async Task Temporary_endpoint_keys_and_changed_accepted_keys_are_rejected_at_planner_boundary()
    {
        using (var temporary = Context())
        {
            var (actor, film) = AttachEndpoints(temporary);
            actor.Films.Add(film);
            // Inject an invalid flag after EF's PrepareToSave so this specifically exercises planner defense.
            temporary.Capture.BeforePlan = entries => Assert.Single(entries).ToEntityEntry().Property(nameof(Credit.ActorId)).IsTemporary = true;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => temporary.SaveChangesAsync(false));
            Assert.Contains("temporary", error.Message);
            Assert.Null(temporary.Capture.Plan);
        }
        using var changed = Context();
        var credit = AttachCredit(changed, role: "old", version: 2);
        credit.Role = "modified";
        // Alter the actual EF original snapshot at the provider seam; no fabricated IUpdateEntry is used.
        changed.Capture.BeforePlan = entries =>
        {
            var entry = Assert.Single(entries);
            entry.SetOriginalValue(entry.EntityType.FindProperty(nameof(Credit.ActorId))!, Guid.NewGuid());
        };
        Assert.Contains("replace an association", (await Assert.ThrowsAsync<InvalidOperationException>(() => changed.SaveChangesAsync(false))).Message);
        Assert.Null(changed.Capture.Plan);
    }

    [Fact]
    public async Task Numeric_version_overflow_and_unsupported_generated_properties_fail_before_plan_publication()
    {
        using (var overflow = Context())
        {
            AttachCredit(overflow, "old", long.MaxValue).Role = "new";
            await Assert.ThrowsAsync<OverflowException>(() => overflow.SaveChangesAsync(false));
            Assert.Null(overflow.Capture.Plan);
        }
        using (var unsupported = Context(b => b.Entity<Credit>().Property<string>("BadVersion").IsRowVersion()))
        {
            var (actor, film) = AttachEndpoints(unsupported);
            actor.Films.Add(film);
            Assert.Contains("unconverted non-nullable long", (await Assert.ThrowsAsync<NotSupportedException>(() => unsupported.SaveChangesAsync(false))).Message);
        }
        using var storeGenerated = Context(b => b.Entity<Credit>().Property<string>("StoreOnly").ValueGeneratedOnAdd());
        var (source, target) = AttachEndpoints(storeGenerated);
        source.Films.Add(target);
        Assert.Contains("Store-generated association property", (await Assert.ThrowsAsync<NotSupportedException>(() => storeGenerated.SaveChangesAsync(false))).Message);
    }

    [Fact]
    public async Task Converted_generated_numeric_version_is_rejected_even_when_wire_type_is_supported()
    {
        using var context = Context(b => b.Entity<Credit>().Property(e => e.Version)
            .HasConversion(value => value.ToString(), value => long.Parse(value)));
        AttachCredit(context, "old", 4).Role = "new";
        Assert.Contains("unconverted non-nullable long", (await Assert.ThrowsAsync<NotSupportedException>(() => context.SaveChangesAsync(false))).Message);
    }

    [Fact]
    public async Task Duplicate_input_reference_is_rejected_and_unchanged_or_detached_entries_are_not_counted()
    {
        using var context = Context();
        var (actor, film) = AttachEndpoints(context);
        actor.Films.Add(film);
        await Plan(context);
        var entry = Assert.Single(context.Capture.Entries);
        var planner = new GraphMutationPlanner(context.GetService<GraphBindingCompiler>());
        Assert.Contains("same EF update entry", Assert.Throws<InvalidOperationException>(() => planner.Plan(new[] { entry, entry })).Message);
        context.ChangeTracker.AcceptAllChanges();
        Assert.Equal(0, planner.Plan(new[] { entry }).ExpectedStateEntries);
        entry.ToEntityEntry().State = EntityState.Detached;
        Assert.Equal(0, planner.Plan(new[] { entry }).ExpectedStateEntries);
    }

    [Fact]
    public async Task Composite_endpoint_parameters_use_principal_store_names_and_declared_slot_order()
    {
        using var context = Context(b =>
        {
            b.Entity<CompositeActor>().ToGraphNode("CompositeActor").HasKey(e => new { e.Tenant, e.Code });
            b.Entity<CompositeActor>().Property(e => e.Tenant).HasGraphPropertyName("tenant");
            b.Entity<CompositeActor>().Property(e => e.Code).HasGraphPropertyName("code");
            var edge = b.Entity<CompositeCredit>();
            edge.HasKey(e => new { e.FilmId, e.SourceCode, e.SourceTenant });
            edge.HasOne<CompositeActor>().WithMany().HasForeignKey(e => new { e.SourceTenant, e.SourceCode });
            edge.HasOne<Film>().WithMany().HasForeignKey(e => e.FilmId);
            edge.ToGraphRelationship("ASSOCIATED", ["SourceTenant", "SourceCode"], ["FilmId"]);
        });
        var actor = new CompositeActor { Tenant = 5, Code = "A' `$bad`" };
        var film = new Film { Id = FilmId };
        context.AttachRange(actor, film);
        context.Add(new CompositeCredit { SourceTenant = actor.Tenant, SourceCode = actor.Code, FilmId = film.Id });
        var edge = Assert.Single((await Plan(context)).Operations);
        Assert.Equal("MATCH (n:`CompositeActor` {`tenant`: $k0, `code`: $k1})\nSET n.`__graphbridge_lock` = true REMOVE n.`__graphbridge_lock`\nRETURN count(n) AS count", edge.LockCommands[0].Text);
        Assert.Equal(5L, edge.Command.Parameters["s0"]);
        Assert.Equal(actor.Code, edge.Command.Parameters["s1"]);
        Assert.Equal(FilmId.ToString("D"), edge.Command.Parameters["t0"]);
        Assert.Empty(Map(edge.Command, "properties"));
        Assert.DoesNotContain(actor.Code, edge.Command.Text);
        Assert.DoesNotContain("SourceCode", edge.Command.Text);
    }

    private PlanContext Context(Action<ModelBuilder>? additional = null)
        => new(_driver, model =>
        {
            var actor = model.Entity<Actor>().ToGraphNode("Actor");
            actor.Property(e => e.Id).HasGraphPropertyName("actorId");
            actor.Property(e => e.Version).IsRowVersion();
            model.Entity<Film>().ToGraphNode("Film").Property(e => e.Id).HasGraphPropertyName("filmId");
            actor.HasMany(e => e.Films).WithMany(e => e.Actors).UsingEntity<Credit>(
                right => right.HasOne<Film>().WithMany().HasForeignKey(e => e.FilmId),
                left => left.HasOne<Actor>().WithMany().HasForeignKey(e => e.ActorId),
                join =>
                {
                    join.HasKey(e => new { e.ActorId, e.FilmId });
                    join.ToGraphRelationship("ACTED_IN", [nameof(Credit.ActorId)], [nameof(Credit.FilmId)]);
                    join.Property(e => e.Role).HasGraphPropertyName("role");
                    join.Property(e => e.Note).HasGraphPropertyName("note");
                    join.Property(e => e.Token).HasGraphPropertyName("token").IsConcurrencyToken();
                    join.Property(e => e.Version).HasGraphPropertyName("version").IsRowVersion();
                });
            additional?.Invoke(model);
        });

    private static (Actor Actor, Film Film) AttachEndpoints(PlanContext context)
    {
        var actor = new Actor { Id = ActorId, Version = 1 };
        var film = new Film { Id = FilmId };
        context.AttachRange(actor, film);
        return (actor, film);
    }

    private static Credit AttachCredit(PlanContext context, string role, long version)
    {
        AttachEndpoints(context);
        var credit = new Credit { ActorId = ActorId, FilmId = FilmId, Role = role, Version = version };
        context.Attach(credit);
        return credit;
    }

    private static async Task<GraphMutationPlan> Plan(PlanContext context)
    {
        var count = await context.SaveChangesAsync(acceptAllChangesOnSuccess: false);
        var plan = Assert.IsType<GraphMutationPlan>(context.Capture.Plan);
        Assert.Equal(plan.ExpectedStateEntries, count);
        return plan;
    }

    private static IReadOnlyDictionary<string, object?> Map(CypherMutationCommand command, string parameter)
        => Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(command.Parameters[parameter]);

    public sealed class PlanContext : DbContext
    {
        public PlanContext(IDriver driver, Action<ModelBuilder> mapping)
            : base(new DbContextOptionsBuilder<PlanContext>().UseNeo4j(driver)
                .ReplaceService<IDatabase, PlanningDatabase>()
                .ReplaceService<IModelCacheKeyFactory, PlanModelCacheKeyFactory>().Options)
            => Mapping = mapping;
        public Action<ModelBuilder> Mapping { get; }
        public PlanningDatabase Capture => (PlanningDatabase)this.GetService<IDatabase>();
        protected override void OnModelCreating(ModelBuilder modelBuilder) => Mapping(modelBuilder);
    }

    public sealed class PlanModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) => (context.GetType(), ((PlanContext)context).Mapping, designTime);
    }

    public sealed class PlanningDatabase(DatabaseDependencies dependencies) : Database(dependencies)
    {
        internal GraphMutationPlan? Plan { get; private set; }
        public IList<IUpdateEntry> Entries { get; private set; } = Array.Empty<IUpdateEntry>();
        public Action<IList<IUpdateEntry>>? BeforePlan { get; set; }

        public override int SaveChanges(IList<IUpdateEntry> entries)
        {
            Entries = entries.ToArray();
            Plan = null;
            BeforePlan?.Invoke(entries);
            Plan = new GraphMutationPlanner(entries[0].Context.GetService<GraphBindingCompiler>()).Plan(entries);
            // Observation count only. Generated values remain pending; no executor or graph store is substituted.
            return Plan.ExpectedStateEntries;
        }

        public override Task<int> SaveChangesAsync(IList<IUpdateEntry> entries, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(SaveChanges(entries));
        }
    }

    public sealed class Actor
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public long Version { get; set; }
        public List<Film> Films { get; } = [];
    }
    public sealed class Film
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = "";
        public List<Actor> Actors { get; } = [];
    }
    public sealed class Credit
    {
        public Guid ActorId { get; set; }
        public Guid FilmId { get; set; }
        public string? Role { get; set; }
        public string? Note { get; set; }
        public string? Token { get; set; }
        public long Version { get; set; }
    }
    public sealed class CompositeActor { public long Tenant { get; set; } public string Code { get; set; } = ""; }
    public sealed class CompositeCredit
    {
        public long SourceTenant { get; set; }
        public string SourceCode { get; set; } = "";
        public Guid FilmId { get; set; }
    }
}
