using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;
using Neo4j.Driver;
using Xunit.Abstractions;

namespace GraphBridge.UnitTests;

/// <summary>
/// GB-009: observe real EF entries at the database-provider seam, without persisting them.
/// Only the graph model validator and database service are replaced, inside this test file.
/// No EF internal type, alternate tracker, InMemory provider, or graph transport is used.
/// </summary>
public sealed class JoinEntryExperimentTests(ITestOutputHelper output) : IDisposable
{
    private static readonly Guid PersonId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid MovieId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid OtherMovieId = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private readonly IDriver _driver = ProviderTestDriver.Create();

    public void Dispose() => Assert.Empty(((ProviderTestDriver)(object)_driver).Calls);

    [Fact]
    public void Observation_overrides_are_test_only_and_normal_provider_still_rejects_unmapped_relationships()
    {
        using var normal = new ObservationContext(_driver, observe: false);
        var error = Assert.Throws<InvalidOperationException>(() => normal.Model);
        Assert.Contains("relationship storage requires explicit ToGraphReference metadata", error.Message);

        using var context = Context();
        Assert.Equal("GraphBridge.EntityFrameworkCore", context.Database.ProviderName);
        Assert.IsType<ModelValidator>(context.GetService<IModelValidator>());
        Assert.IsType<RecordingDatabase>(context.GetService<IDatabase>());
        Assert.Equal(ChangeTrackingStrategy.Snapshot,
            context.Model.FindEntityType(typeof(ImplicitPerson))!.GetChangeTrackingStrategy());
    }

    [Fact]
    public async Task New_endpoints_and_implicit_dictionary_join_reach_save_as_three_added_entries()
    {
        using var context = Context();
        var person = new ImplicitPerson();
        var movie = new ImplicitMovie();
        person.Movies.Add(movie);
        context.Add(person);
        context.ChangeTracker.DetectChanges();

        Assert.NotEqual(Guid.Empty, person.Id);
        Assert.NotEqual(Guid.Empty, movie.Id);
        var join = ImplicitJoin(context);
        Assert.IsType<Dictionary<string, object>>(join.Entity);
        Assert.True(join.Metadata.IsPropertyBag);
        var skip = context.Model.FindEntityType(typeof(ImplicitPerson))!
            .FindSkipNavigation(nameof(ImplicitPerson.Movies))!;
        Assert.Same(join.Metadata, skip.JoinEntityType);
        Assert.Equal(2, join.Metadata.GetForeignKeys().Count());
        Assert.Equal(2, join.Metadata.FindPrimaryKey()!.Properties.Count);
        AssertEndpointPair(join, person.Id, movie.Id);
        Assert.All(join.Properties, p =>
        {
            Assert.True(p.Metadata.IsIndexerProperty());
            Assert.True(p.Metadata.IsPrimaryKey());
            Assert.True(p.Metadata.IsForeignKey());
            Assert.False(p.IsModified);
            Assert.False(p.IsTemporary);
            Assert.Equal(p.CurrentValue, p.OriginalValue);
        });
        var before = Before(join);

        Assert.Equal(3, await context.SaveChangesAsync());
        var batch = Batch(context);
        Assert.Equal(3, batch.Count);
        Assert.All(batch, e => Assert.Equal(EntityState.Added, e.State));
        AssertAtSeam(before, Assert.Single(batch, e => e.EntityType == join.Metadata));
        Assert.All(context.ChangeTracker.Entries(), e => Assert.Equal(EntityState.Unchanged, e.State));
        Assert.Same(person, Assert.Single(movie.Actors));
    }

    [Fact]
    public async Task Existing_endpoints_add_only_the_join_and_save_can_detect_the_collection_change()
    {
        using var context = Context();
        var (person, movie) = AttachImplicitEndpoints(context);
        person.Movies.Add(movie);

        // Deliberately do not inspect Entries/Entry or call DetectChanges before SaveChanges.
        Assert.Equal(1, await context.SaveChangesAsync());
        var join = Assert.Single(Batch(context));
        Assert.True(join.EntityType.IsPropertyBag);
        Assert.Equal(EntityState.Added, join.State);
        Assert.All(join.Properties, p =>
        {
            Assert.False(p.Modified);
            Assert.Equal(p.Current, p.Original);
        });
        AssertEndpointPair(ImplicitJoin(context), PersonId, MovieId);
        Assert.Equal(EntityState.Unchanged, context.Entry(person).State);
        Assert.Equal(EntityState.Unchanged, context.Entry(movie).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Add_then_remove_before_save_produces_no_write_whether_add_was_detected(bool detectAdd)
    {
        using var context = Context();
        var (person, movie) = AttachImplicitEndpoints(context);
        person.Movies.Add(movie);
        EntityEntry? addedJoin = null;
        if (detectAdd)
        {
            context.ChangeTracker.DetectChanges();
            addedJoin = ImplicitJoin(context);
            Assert.Equal(EntityState.Added, addedJoin.State);
        }
        person.Movies.Remove(movie);
        context.ChangeTracker.DetectChanges();

        if (addedJoin is not null)
            Assert.Equal(EntityState.Detached, addedJoin.State);
        Assert.DoesNotContain(context.ChangeTracker.Entries(), e => e.Metadata.IsPropertyBag);
        Assert.Equal(0, await context.SaveChangesAsync());
        Assert.Empty(context.Recorder.Batches);
    }

    [Fact]
    public async Task Removing_accepted_association_supplies_deleted_join_with_original_endpoint_keys()
    {
        using var context = Context();
        var (person, movie) = AttachImplicitEndpoints(context);
        person.Movies.Add(movie);
        Assert.Equal(1, await context.SaveChangesAsync()); // Recorder success; no graph write.
        context.Recorder.Batches.Clear();
        var join = ImplicitJoin(context);

        person.Movies.Remove(movie);
        context.ChangeTracker.DetectChanges();
        Assert.Equal(EntityState.Deleted, join.State);
        AssertEndpointPair(join, PersonId, MovieId);
        var before = Before(join);
        Assert.All(before.Properties, p =>
        {
            Assert.Equal(p.Original, p.Current);
            Assert.False(p.Modified);
        });

        Assert.Equal(1, await context.SaveChangesAsync());
        AssertAtSeam(before, Assert.Single(Batch(context)));
        Assert.Equal(EntityState.Detached, join.State);
        Assert.Empty(movie.Actors);
        Assert.Equal(EntityState.Unchanged, context.Entry(person).State);
        Assert.Equal(EntityState.Unchanged, context.Entry(movie).State);
    }

    [Fact]
    public async Task Explicit_composite_join_payload_has_original_current_and_only_payload_modified()
    {
        using var context = Context();
        var (person, movie) = AttachExplicitEndpoints(context);
        person.Movies.Add(movie);
        context.ChangeTracker.DetectChanges();
        var join = Assert.Single(context.ChangeTracker.Entries<ActingCredit>());
        join.Entity.Role = "lead";
        context.ChangeTracker.DetectChanges();
        Assert.Equal(EntityState.Added, join.State);
        var added = Before(join);
        Assert.Equal(1, await context.SaveChangesAsync());
        AssertAtSeam(added, Assert.Single(Batch(context)));
        Assert.Equal("lead", join.Property(e => e.Role).OriginalValue);
        context.Recorder.Batches.Clear();

        join.Entity.Role = "supporting";
        context.ChangeTracker.DetectChanges();
        var modified = Before(join);
        Assert.Equal(EntityState.Modified, modified.State);
        Assert.Equal(nameof(ActingCredit.Role), Assert.Single(modified.Properties, p => p.Modified).Name);
        Assert.Equal("lead", Property(modified, nameof(ActingCredit.Role)).Original);
        Assert.Equal("supporting", Property(modified, nameof(ActingCredit.Role)).Current);
        Assert.All(modified.Properties.Where(p => p.Name != nameof(ActingCredit.Role)), p =>
        {
            Assert.Equal(p.Current, p.Original);
            Assert.False(p.Modified);
        });

        Assert.Equal(1, await context.SaveChangesAsync());
        AssertAtSeam(modified, Assert.Single(Batch(context)));
        Assert.Equal("supporting", join.Property(e => e.Role).OriginalValue);
        Assert.Equal(EntityState.Unchanged, join.State);
    }

    [Fact]
    public void Editing_accepted_identifying_join_FK_fails_in_EF_before_provider_entry_selection()
    {
        using var context = Context();
        var (person, movie) = AttachExplicitEndpoints(context);
        person.Movies.Add(movie);
        context.ChangeTracker.DetectChanges();
        context.ChangeTracker.AcceptAllChanges(); // Establish an existing EF snapshot; no persistence claim.
        var join = Assert.Single(context.ChangeTracker.Entries<ActingCredit>());
        Assert.True(join.Property(e => e.MovieId).Metadata.IsPrimaryKey());
        Assert.Equal(MovieId, join.Property(e => e.MovieId).OriginalValue);

        join.Entity.MovieId = OtherMovieId;
        var error = Assert.Throws<InvalidOperationException>(() => context.ChangeTracker.DetectChanges());
        Assert.Contains("part of a key", error.Message);
        Assert.Contains(nameof(ActingCredit.MovieId), error.Message);
        Assert.Empty(context.Recorder.Batches);
    }

    [Fact]
    public async Task Replacing_skip_navigation_endpoint_yields_delete_old_and_add_new_composite_joins()
    {
        using var context = Context();
        var (person, oldMovie) = AttachExplicitEndpoints(context);
        var newMovie = new ExplicitMovie { Id = OtherMovieId };
        context.Attach(newMovie);
        person.Movies.Add(oldMovie);
        await context.SaveChangesAsync();
        context.Recorder.Batches.Clear();

        person.Movies.Remove(oldMovie);
        person.Movies.Add(newMovie);
        context.ChangeTracker.DetectChanges();
        var joins = context.ChangeTracker.Entries<ActingCredit>().ToArray();
        var deleted = Before(Assert.Single(joins, e => e.State == EntityState.Deleted));
        var added = Before(Assert.Single(joins, e => e.State == EntityState.Added));
        Assert.Equal(MovieId, Property(deleted, nameof(ActingCredit.MovieId)).Original);
        Assert.Equal(MovieId, Property(deleted, nameof(ActingCredit.MovieId)).Current);
        Assert.Equal(OtherMovieId, Property(added, nameof(ActingCredit.MovieId)).Current);

        Assert.Equal(2, await context.SaveChangesAsync());
        var batch = Batch(context);
        Assert.Equal(2, batch.Count);
        AssertAtSeam(deleted, Assert.Single(batch, e => e.State == EntityState.Deleted));
        AssertAtSeam(added, Assert.Single(batch, e => e.State == EntityState.Added));
        Assert.Same(newMovie, Assert.Single(person.Movies));
        Assert.Empty(oldMovie.Actors);
        Assert.Same(person, Assert.Single(newMovie.Actors));
    }

    [Fact]
    public void Changing_accepted_principal_key_is_also_rejected_before_provider_save()
    {
        using var context = Context();
        var (person, movie) = AttachImplicitEndpoints(context);
        person.Movies.Add(movie);
        context.ChangeTracker.DetectChanges();
        context.ChangeTracker.AcceptAllChanges();

        person.Id = Guid.Parse("00000000-0000-0000-0000-000000000099");
        var error = Assert.Throws<InvalidOperationException>(() => context.ChangeTracker.DetectChanges());
        Assert.Contains("part of a key", error.Message);
        Assert.Empty(context.Recorder.Batches);
    }

    [Fact]
    public async Task Duplicate_list_references_are_one_association_until_last_reference_is_removed()
    {
        using var context = Context();
        var (person, movie) = AttachImplicitEndpoints(context);
        person.Movies.Add(movie);
        person.Movies.Add(movie);
        context.ChangeTracker.DetectChanges();
        Assert.Equal(2, person.Movies.Count);
        Assert.Equal(EntityState.Added, ImplicitJoin(context).State);
        Assert.Equal(1, await context.SaveChangesAsync());
        Assert.Single(Batch(context));
        context.Recorder.Batches.Clear();

        person.Movies.Remove(movie);
        Assert.Equal(0, await context.SaveChangesAsync());
        Assert.Empty(context.Recorder.Batches);
        Assert.Equal(EntityState.Unchanged, ImplicitJoin(context).State);

        person.Movies.Remove(movie);
        Assert.Equal(1, await context.SaveChangesAsync());
        Assert.Equal(EntityState.Deleted, Assert.Single(Batch(context)).State);
    }

    [Fact]
    public async Task Remove_and_readd_before_detection_is_no_change_to_an_accepted_association()
    {
        using var context = Context();
        var (person, movie) = AttachImplicitEndpoints(context);
        person.Movies.Add(movie);
        await context.SaveChangesAsync();
        context.Recorder.Batches.Clear();

        person.Movies.Remove(movie);
        person.Movies.Add(movie);
        Assert.Equal(0, await context.SaveChangesAsync());
        Assert.Empty(context.Recorder.Batches);
        Assert.Equal(EntityState.Unchanged, ImplicitJoin(context).State);
    }

    [Fact]
    public async Task Readding_after_removal_was_detected_does_not_implicitly_undelete_the_join()
    {
        using var context = Context();
        var (person, movie) = AttachImplicitEndpoints(context);
        person.Movies.Add(movie);
        await context.SaveChangesAsync();
        context.Recorder.Batches.Clear();

        person.Movies.Remove(movie);
        context.ChangeTracker.DetectChanges();
        Assert.Equal(EntityState.Deleted, ImplicitJoin(context).State);
        person.Movies.Add(movie);
        context.ChangeTracker.DetectChanges();
        var join = ImplicitJoin(context);
        Assert.Equal(EntityState.Deleted, join.State);
        Assert.Same(movie, Assert.Single(person.Movies));
        var before = Before(join);

        Assert.Equal(1, await context.SaveChangesAsync());
        AssertAtSeam(before, Assert.Single(Batch(context)));
    }

    [Fact]
    public void Tracking_two_explicit_join_instances_with_same_endpoint_key_is_an_identity_conflict()
    {
        using var context = Context();
        AttachExplicitEndpoints(context);
        context.Add(new ActingCredit { PersonId = PersonId, MovieId = MovieId, Role = "first" });

        var error = Assert.Throws<InvalidOperationException>(() =>
            context.Add(new ActingCredit { PersonId = PersonId, MovieId = MovieId, Role = "second" }));
        Assert.Contains("cannot be tracked", error.Message);
        Assert.Empty(context.Recorder.Batches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deleting_tracked_endpoint_cascades_only_to_tracked_join(bool associationAccepted)
    {
        using var context = Context();
        var (person, movie) = AttachImplicitEndpoints(context);
        person.Movies.Add(movie);
        context.ChangeTracker.DetectChanges();
        var join = ImplicitJoin(context);
        if (associationAccepted)
            await context.SaveChangesAsync();
        context.Recorder.Batches.Clear();
        Assert.All(join.Metadata.GetForeignKeys(), fk => Assert.Equal(DeleteBehavior.Cascade, fk.DeleteBehavior));

        context.Remove(person);
        Assert.Equal(associationAccepted ? EntityState.Deleted : EntityState.Detached, join.State);
        Assert.Equal(EntityState.Unchanged, context.Entry(movie).State);
        var before = associationAccepted ? Before(join) : null;
        Assert.Equal(associationAccepted ? 2 : 1, await context.SaveChangesAsync());
        var batch = Batch(context);
        Assert.All(batch, e => Assert.Equal(EntityState.Deleted, e.State));
        Assert.Contains(batch, e => ReferenceEquals(e.Entity, person));
        Assert.DoesNotContain(batch, e => ReferenceEquals(e.Entity, movie));
        if (before is not null)
            AssertAtSeam(before, Assert.Single(batch, e => e.EntityType.IsPropertyBag));
        else
            Assert.DoesNotContain(batch, e => e.EntityType.IsPropertyBag);
    }

    [Fact]
    public async Task Save_without_acceptance_preserves_added_modified_deleted_states_and_originals()
    {
        using var context = Context();
        var (person, movie) = AttachExplicitEndpoints(context);
        person.Movies.Add(movie);
        context.ChangeTracker.DetectChanges();
        var join = Assert.Single(context.ChangeTracker.Entries<ActingCredit>());

        var added = Before(join);
        Assert.Equal(1, await context.SaveChangesAsync(acceptAllChangesOnSuccess: false));
        AssertAtSeam(added, Assert.Single(Batch(context)));
        Assert.Equal(EntityState.Added, join.State);
        context.Recorder.Batches.Clear();
        Assert.Equal(1, await context.SaveChangesAsync(acceptAllChangesOnSuccess: false));
        AssertAtSeam(added, Assert.Single(Batch(context)));
        context.ChangeTracker.AcceptAllChanges();
        context.Recorder.Batches.Clear();

        join.Entity.Role = "changed";
        context.ChangeTracker.DetectChanges();
        var modified = Before(join);
        Assert.Equal(1, await context.SaveChangesAsync(acceptAllChangesOnSuccess: false));
        AssertAtSeam(modified, Assert.Single(Batch(context)));
        Assert.Equal(EntityState.Modified, join.State);
        Assert.Equal("", join.Property(e => e.Role).OriginalValue);
        context.ChangeTracker.AcceptAllChanges();
        Assert.Equal("changed", join.Property(e => e.Role).OriginalValue);
        context.Recorder.Batches.Clear();

        person.Movies.Remove(movie);
        context.ChangeTracker.DetectChanges();
        var deleted = Before(join);
        Assert.Equal(1, await context.SaveChangesAsync(acceptAllChangesOnSuccess: false));
        AssertAtSeam(deleted, Assert.Single(Batch(context)));
        Assert.Equal(EntityState.Deleted, join.State);
        context.ChangeTracker.AcceptAllChanges();
        Assert.Equal(EntityState.Detached, join.State);
    }

    [Fact]
    public async Task Provider_failure_preserves_join_and_retry_supplies_same_added_values()
    {
        using var context = Context();
        var (person, movie) = AttachImplicitEndpoints(context);
        person.Movies.Add(movie);
        context.ChangeTracker.DetectChanges();
        var join = ImplicitJoin(context);
        var before = Before(join);
        context.Recorder.Failure = new InvalidOperationException("GB-009 observation failure");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        Assert.Same(context.Recorder.Failure, error);
        AssertAtSeam(before, Assert.Single(Batch(context)));
        Assert.Equal(EntityState.Added, join.State);
        context.Recorder.Failure = null;
        context.Recorder.Batches.Clear();

        Assert.Equal(1, await context.SaveChangesAsync());
        AssertAtSeam(before, Assert.Single(Batch(context)));
        Assert.Equal(EntityState.Unchanged, join.State);
    }

    [Fact]
    public async Task Stable_ID_entity_allows_FK_change_but_relationship_snapshot_is_already_current_at_save()
    {
        using var context = Context();
        var person = new IdentifiedPerson { Id = PersonId };
        var oldMovie = new IdentifiedMovie { Id = MovieId };
        var newMovie = new IdentifiedMovie { Id = OtherMovieId };
        var credit = new IdentifiedCredit
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000004"),
            PersonId = PersonId,
            MovieId = MovieId,
            Role = "lead"
        };
        context.AttachRange(person, oldMovie, newMovie, credit);
        var entry = context.Entry(credit);
        Assert.Same(oldMovie, credit.Movie);
        Assert.False(entry.Property(e => e.MovieId).Metadata.IsPrimaryKey());

        credit.MovieId = OtherMovieId;
        context.ChangeTracker.DetectChanges();
        Assert.Same(newMovie, credit.Movie);
        var before = Before(entry);
        Assert.Equal(EntityState.Modified, before.State);
        Assert.Equal(nameof(IdentifiedCredit.MovieId), Assert.Single(before.Properties, p => p.Modified).Name);
        Assert.Equal(MovieId, Property(before, nameof(IdentifiedCredit.MovieId)).Original);
        Assert.Equal(OtherMovieId, Property(before, nameof(IdentifiedCredit.MovieId)).Current);

        Assert.Equal(1, await context.SaveChangesAsync());
        var captured = Assert.Single(Batch(context));
        AssertAtSeam(before, captured);
        Assert.Equal(OtherMovieId, captured.RelationshipSnapshots![nameof(IdentifiedCredit.MovieId)]);
        Assert.Equal(MovieId, Property(captured, nameof(IdentifiedCredit.MovieId)).Original);
        Assert.Equal(OtherMovieId, entry.Property(e => e.MovieId).OriginalValue);
    }

    private ObservationContext Context() => new(_driver);

    private static (ImplicitPerson Person, ImplicitMovie Movie) AttachImplicitEndpoints(ObservationContext context)
    {
        var person = new ImplicitPerson { Id = PersonId };
        var movie = new ImplicitMovie { Id = MovieId };
        context.AttachRange(person, movie);
        return (person, movie);
    }

    private static (ExplicitPerson Person, ExplicitMovie Movie) AttachExplicitEndpoints(ObservationContext context)
    {
        var person = new ExplicitPerson { Id = PersonId };
        var movie = new ExplicitMovie { Id = MovieId };
        context.AttachRange(person, movie);
        return (person, movie);
    }

    private static EntityEntry ImplicitJoin(ObservationContext context)
        => Assert.Single(context.ChangeTracker.Entries(), e => e.Metadata.IsPropertyBag);

    private static void AssertEndpointPair(EntityEntry join, Guid personId, Guid movieId)
    {
        // Resolve roles via public skip-navigation/FK metadata, never generated property names or internal join flags.
        var skip = join.Context.Model.FindEntityType(typeof(ImplicitPerson))!
            .FindSkipNavigation(nameof(ImplicitPerson.Movies))!;
        var sourceProperty = Assert.Single(skip.ForeignKey.Properties);
        var targetProperty = Assert.Single(skip.Inverse.ForeignKey.Properties);
        Assert.Equal(personId, join.Property(sourceProperty.Name).CurrentValue);
        Assert.Equal(movieId, join.Property(targetProperty.Name).CurrentValue);
        Assert.Equal(personId, join.Property(sourceProperty.Name).OriginalValue);
        Assert.Equal(movieId, join.Property(targetProperty.Name).OriginalValue);
    }

    private EntryObservation Before(EntityEntry entry)
    {
        var observation = EntryObservation.FromTracker(entry);
        Log("before", observation);
        return observation;
    }

    private IReadOnlyList<EntryObservation> Batch(ObservationContext context)
    {
        var batch = Assert.Single(context.Recorder.Batches);
        foreach (var entry in batch)
        {
            Assert.Same(context.Model.FindEntityType(entry.EntityType.Name), entry.EntityType);
            Log("provider", entry);
        }
        return batch;
    }

    private void Log(string phase, EntryObservation entry)
        => output.WriteLine($"{phase}: {entry.EntityType.Name} [{entry.State}] "
            + string.Join("; ", entry.Properties.Select(p =>
                $"{p.Name}: original={p.Original ?? "<null>"}, current={p.Current ?? "<null>"}, modified={p.Modified}, temporary={p.Temporary}")));

    private static PropertyObservation Property(EntryObservation entry, string name)
        => Assert.Single(entry.Properties, p => p.Name == name);

    private static void AssertAtSeam(EntryObservation before, EntryObservation captured)
    {
        Assert.Same(before.Entity, captured.Entity);
        Assert.Same(before.EntityType, captured.EntityType);
        Assert.Equal(before.State, captured.State);
        Assert.Equal(before.Properties, captured.Properties);
    }

    public sealed record PropertyObservation(string Name, object? Current, object? Original, bool Modified, bool Temporary);

    public sealed record EntryObservation(
        IEntityType EntityType,
        object Entity,
        EntityState State,
        PropertyObservation[] Properties,
        IReadOnlyDictionary<string, object?>? RelationshipSnapshots)
    {
        public static EntryObservation FromTracker(EntityEntry entry)
            => new(entry.Metadata, entry.Entity, entry.State,
                entry.Properties.Select(p => new PropertyObservation(
                    p.Metadata.Name, p.CurrentValue, p.OriginalValue, p.IsModified, p.IsTemporary)).ToArray(), null);

        public static EntryObservation FromProvider(IUpdateEntry entry)
            => new(entry.EntityType, entry.ToEntityEntry().Entity, entry.EntityState,
                entry.EntityType.GetProperties().Select(p => new PropertyObservation(
                    p.Name, entry.GetCurrentValue(p), entry.GetOriginalValue(p), entry.IsModified(p), entry.HasTemporaryValue(p))).ToArray(),
                entry.EntityType.GetProperties().Where(p => p.IsForeignKey() || p.IsKey())
                    .ToDictionary(p => p.Name, p => entry.GetRelationshipSnapshotValue(p)));
    }

    // TEST ONLY. This records the entries supplied by EF and returns an observation count.
    // It does not execute GraphBridge mutation planning, Cypher, transport, transactions, or any storage.
    public sealed class RecordingDatabase(DatabaseDependencies dependencies) : Database(dependencies)
    {
        public List<IReadOnlyList<EntryObservation>> Batches { get; } = [];
        public Exception? Failure { get; set; }

        public override int SaveChanges(IList<IUpdateEntry> entries)
        {
            Batches.Add(entries.Select(EntryObservation.FromProvider).ToArray());
            if (Failure is not null)
                throw Failure;
            return entries.Count;
        }

        public override Task<int> SaveChangesAsync(IList<IUpdateEntry> entries, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(SaveChanges(entries));
        }
    }

    public sealed class ObservationContext : DbContext
    {
        public ObservationContext(IDriver driver, bool observe = true) : base(Options(driver, observe)) { }
        public RecordingDatabase Recorder => (RecordingDatabase)this.GetService<IDatabase>();

        private static DbContextOptions<ObservationContext> Options(IDriver driver, bool observe)
        {
            var builder = new DbContextOptionsBuilder<ObservationContext>().UseNeo4j(driver);
            if (observe)
            {
                // The production validator must keep rejecting unimplemented graph relationships.
                // Core validation remains active and all other EF/GraphBridge services remain registered.
                builder.ReplaceService<IModelValidator, ModelValidator>();
                builder.ReplaceService<IDatabase, RecordingDatabase>();
            }
            return builder.Options;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ImplicitPerson>().ToGraphNode("ExperimentImplicitPerson")
                .HasMany(e => e.Movies).WithMany(e => e.Actors);
            modelBuilder.Entity<ImplicitMovie>().ToGraphNode("ExperimentImplicitMovie");

            modelBuilder.Entity<ExplicitPerson>().ToGraphNode("ExperimentExplicitPerson")
                .HasMany(e => e.Movies).WithMany(e => e.Actors)
                .UsingEntity<ActingCredit>(
                    right => right.HasOne<ExplicitMovie>().WithMany().HasForeignKey(e => e.MovieId),
                    left => left.HasOne<ExplicitPerson>().WithMany().HasForeignKey(e => e.PersonId),
                    join => join.HasKey(e => new { e.PersonId, e.MovieId }));
            modelBuilder.Entity<ExplicitMovie>().ToGraphNode("ExperimentExplicitMovie");

            modelBuilder.Entity<IdentifiedPerson>().ToGraphNode("ExperimentIdentifiedPerson");
            modelBuilder.Entity<IdentifiedMovie>().ToGraphNode("ExperimentIdentifiedMovie");
            var identified = modelBuilder.Entity<IdentifiedCredit>();
            identified.HasKey(e => e.Id);
            identified.HasOne(e => e.Person).WithMany().HasForeignKey(e => e.PersonId);
            identified.HasOne(e => e.Movie).WithMany().HasForeignKey(e => e.MovieId);
        }
    }

    public sealed class ImplicitPerson
    {
        public Guid Id { get; set; }
        public List<ImplicitMovie> Movies { get; } = [];
    }

    public sealed class ImplicitMovie
    {
        public Guid Id { get; set; }
        public List<ImplicitPerson> Actors { get; } = [];
    }

    public sealed class ExplicitPerson
    {
        public Guid Id { get; set; }
        public List<ExplicitMovie> Movies { get; } = [];
    }

    public sealed class ExplicitMovie
    {
        public Guid Id { get; set; }
        public List<ExplicitPerson> Actors { get; } = [];
    }

    public sealed class ActingCredit
    {
        public Guid PersonId { get; set; }
        public Guid MovieId { get; set; }
        public string Role { get; set; } = "";
    }

    public sealed class IdentifiedPerson { public Guid Id { get; set; } }
    public sealed class IdentifiedMovie { public Guid Id { get; set; } }
    public sealed class IdentifiedCredit
    {
        public Guid Id { get; set; }
        public Guid PersonId { get; set; }
        public Guid MovieId { get; set; }
        public string Role { get; set; } = "";
        public IdentifiedPerson Person { get; set; } = null!;
        public IdentifiedMovie Movie { get; set; } = null!;
    }
}
