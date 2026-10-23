using GraphBridge.EntityFrameworkCore.FluentCypher;
using Microsoft.EntityFrameworkCore;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;

namespace GraphBridge.IntegrationTests;

/// <summary>ADR 0049 live oracle: a Fluent entity return equals the same entities loaded with AsNoTracking LINQ.</summary>
[Collection(Neo4jIntegrationCollection.Name)]
public sealed class ProviderFluentEntityReturnTests(DisposableNeo4jFixture fixture)
{
    [Neo4jIntegrationFact]
    public async Task Node_entities_equal_AsNoTracking_LINQ_for_every_mapped_property_type()
    {
        var run = await SeedAsync();
        try
        {
            await using var db = Context();
            var item = Graph.Node<Item>();
            var fluent = await db.Cypher().Match(item).Where(() => item.Value.Run == run)
                .Return(() => item.Value).OrderBy(() => item.Value.Name).Limit(100).ToListAsync();
            Assert.Empty(db.ChangeTracker.Entries());
            var linq = await db.Set<Item>().AsNoTracking().Where(i => i.Run == run).OrderBy(i => i.Name).ToListAsync();
            Assert.Equal(3, linq.Count);
            Assert.Contains(linq, i => i.ShelfId is null);
            AssertSameEntities(db, linq, fluent);

            // The same entities in a DTO, ordered by a returned entity's member after DISTINCT.
            var reader = Graph.Node<Reader>();
            var liked = await db.Cypher().Match(reader.Out<Like>(item)).Where(() => reader.Value.Run == run)
                .ReturnDistinct(() => item.Value).OrderByDescending(() => item.Value.Name).Limit(100).ToListAsync();
            var readerIds = (await db.Set<Reader>().AsNoTracking().Where(r => r.Run == run).ToListAsync()).Select(r => r.Id).ToArray();
            var likedIds = (await db.Set<Like>().AsNoTracking().Where(l => readerIds.Contains(l.ReaderId)).ToListAsync()).Select(l => l.ItemId).ToHashSet();
            AssertSameEntities(db, linq.Where(i => likedIds.Contains(i.Id)).OrderByDescending(i => i.Name, StringComparer.Ordinal).ToList(), liked);
            Assert.Empty(db.ChangeTracker.Entries());
        }
        finally { await CleanupAsync(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Relationship_entities_equal_AsNoTracking_LINQ_for_identified_and_association_edges()
    {
        var run = await SeedAsync();
        try
        {
            await using var db = Context();
            var readerIds = (await db.Set<Reader>().AsNoTracking().Where(r => r.Run == run).ToListAsync()).Select(r => r.Id).ToArray();
            var reader = Graph.Node<Reader>(); var item = Graph.Node<Item>(); var loan = Graph.Edge<Loan>(); var like = Graph.Edge<Like>();
            var loans = await db.Cypher().Match(reader.Out(loan, item)).Where(() => reader.Value.Run == run)
                .Return(() => loan.Value).OrderBy(() => loan.Value.Days).Limit(100).ToListAsync();
            AssertSameEntities(db, await db.Set<Loan>().AsNoTracking().Where(l => readerIds.Contains(l.ReaderId)).OrderBy(l => l.Days).ToListAsync(), loans);
            var likes = await db.Cypher().Match(reader.Out(like, item)).Where(() => reader.Value.Run == run)
                .With(like).Return(() => like.Value).OrderBy(() => like.Value.Stars).Limit(100).ToListAsync();
            AssertSameEntities(db, await db.Set<Like>().AsNoTracking().Where(l => readerIds.Contains(l.ReaderId)).OrderBy(l => l.Stars).ToListAsync(), likes);
            Assert.Equal(2, loans.Count);
            Assert.Equal(3, likes.Count);
            Assert.Empty(db.ChangeTracker.Entries());
        }
        finally { await CleanupAsync(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Optional_symbols_read_as_null_and_collected_entities_group_by_the_returned_entity()
    {
        var run = await SeedAsync();
        try
        {
            await using var db = Context();
            var reader = Graph.Node<Reader>(); var item = Graph.Node<Item>();
            var items = Graph.Collect(item, 10);
            var rows = await db.Cypher().Match(reader).Where(() => reader.Value.Run == run).OptionalMatch(reader.Out<Like>(item))
                .Return(() => new { Reader = reader.Value, Items = items.Value }).OrderBy(() => reader.Value.Name).Limit(10).ToListAsync();
            var readers = await db.Set<Reader>().AsNoTracking().Where(r => r.Run == run).OrderBy(r => r.Name).ToListAsync();
            var allItems = await db.Set<Item>().AsNoTracking().Where(i => i.Run == run).ToListAsync();
            var readerIds = readers.Select(r => r.Id).ToArray();
            var allLikes = await db.Set<Like>().AsNoTracking().Where(l => readerIds.Contains(l.ReaderId)).ToListAsync();
            AssertSameEntities(db, readers, rows.Select(r => r.Reader).ToList());
            foreach (var (row, expected) in rows.Zip(readers))
            {
                var ids = allLikes.Where(l => l.ReaderId == expected.Id).Select(l => l.ItemId).ToHashSet();
                AssertSameEntities(db, allItems.Where(i => ids.Contains(i.Id)).OrderBy(i => i.Name).ToList(), row.Items.OrderBy(i => i.Name).ToList());
            }
            Assert.Empty(rows.Single(r => r.Reader.Name == "Zoe").Items);

            var single = await db.Cypher().Match(reader).Where(() => reader.Value.Run == run).OptionalMatch(reader.Out<Like>(item))
                .Return(() => new { reader.Value.Name, Item = item.Value }).Limit(10).ToListAsync();
            Assert.Null(single.Single(r => r.Name == "Zoe").Item);
            Assert.Empty(db.ChangeTracker.Entries());
        }
        finally { await CleanupAsync(run); }
    }

    [Neo4jIntegrationFact]
    public async Task An_ambiguous_native_reference_is_refused_like_LINQ()
    {
        var run = await SeedAsync();
        try
        {
            // A second parent makes the foreign key ambiguous; neither LINQ nor Fluent may pick one.
            await ExecuteAsync("MATCH (i:FERItem {Run:$run, Name:'Anvil'}) CREATE (:FERShelf {shelf_key:$id, Run:$run, Name:'Extra'})-[:FER_ON_SHELF]->(i)",
                new { run, id = Guid.NewGuid().ToString("D") });
            await using var db = Context();
            var item = Graph.Node<Item>();
            var fluent = await Assert.ThrowsAsync<InvalidOperationException>(() => db.Cypher().Match(item)
                .Where(() => item.Value.Run == run && item.Value.Name == "Anvil").Return(() => item.Value).Limit(10).ToListAsync());
            var linq = await Assert.ThrowsAsync<InvalidOperationException>(() => db.Set<Item>().AsNoTracking()
                .Where(i => i.Run == run && i.Name == "Anvil").ToListAsync());
            Assert.Equal(linq.Message, fluent.Message);
        }
        finally { await CleanupAsync(run); }
    }

    private static void AssertSameEntities<T>(DbContext db, IReadOnlyList<T> expected, IReadOnlyList<T> actual) where T : class
    {
        Assert.Equal(expected.Count, actual.Count);
        var properties = db.Model.FindEntityType(typeof(T))!.GetProperties().Where(p => p.PropertyInfo is not null).ToArray();
        for (var i = 0; i < expected.Count; i++)
            foreach (var property in properties)
                Assert.True(Equals(property.PropertyInfo!.GetValue(expected[i]), property.PropertyInfo.GetValue(actual[i])),
                    $"{typeof(T).Name}[{i}].{property.Name}: LINQ '{property.PropertyInfo.GetValue(expected[i])}', Fluent '{property.PropertyInfo.GetValue(actual[i])}'");
    }

    private EntityContext Context() => new(new DbContextOptionsBuilder<EntityContext>().UseNeo4j(fixture.Driver, fixture.Database).Options);

    private async Task<string> SeedAsync()
    {
        await ExecuteAsync("CREATE CONSTRAINT fer_shelf_key IF NOT EXISTS FOR (n:FERShelf) REQUIRE n.shelf_key IS UNIQUE");
        await ExecuteAsync("CREATE CONSTRAINT fer_item_key IF NOT EXISTS FOR (n:FERItem) REQUIRE n.item_key IS UNIQUE");
        await ExecuteAsync("CREATE CONSTRAINT fer_reader_key IF NOT EXISTS FOR (n:FERReader) REQUIRE n.reader_key IS UNIQUE");
        await ExecuteAsync("CREATE CONSTRAINT fer_loan_key IF NOT EXISTS FOR ()-[r:FER_LOAN]-() REQUIRE r.loan_key IS UNIQUE");
        var run = "fer_" + Guid.NewGuid().ToString("N");
        await using var db = Context();
        var shelf = new Shelf { Id = Guid.NewGuid(), Run = run, Name = "Tools" };
        var offset = TimeSpan.FromHours(2);
        var anvil = new Item
        {
            Id = Guid.NewGuid(), Run = run, Name = "Anvil", Nickname = "heavy", Count = 3, Big = 5_000_000_000L, Score = 2.5, Active = true,
            Level = Level.High, Due = new DateTimeOffset(2026, 11, 6, 9, 30, 15, offset), Born = 1990, Code = "A-1", ShelfId = shelf.Id
        };
        var bell = new Item
        {
            Id = Guid.NewGuid(), Run = run, Name = "Bell", Nickname = null, Count = 0, Big = -1L, Score = -0.125, Active = false,
            Level = Level.Low, Due = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), Born = null, Code = "B-2", ShelfId = shelf.Id
        };
        var chain = new Item
        {
            Id = Guid.NewGuid(), Run = run, Name = "Chain", Count = int.MaxValue, Big = long.MinValue, Score = 1e10, Active = true,
            Level = Level.Medium, Due = new DateTimeOffset(2025, 12, 31, 23, 59, 59, TimeSpan.FromHours(-5)), Code = "C-3", ShelfId = null
        };
        var ada = new Reader { Id = Guid.NewGuid(), Run = run, Name = "Ada" };
        var bob = new Reader { Id = Guid.NewGuid(), Run = run, Name = "Bob" };
        var zoe = new Reader { Id = Guid.NewGuid(), Run = run, Name = "Zoe" };
        db.AddRange(shelf, anvil, bell, chain, ada, bob, zoe);
        db.AddRange(
            new Loan { Id = Guid.NewGuid(), ReaderId = ada.Id, ItemId = anvil.Id, Days = 7, Note = "first", Since = new DateTimeOffset(2026, 10, 1, 8, 0, 0, offset) },
            new Loan { Id = Guid.NewGuid(), ReaderId = bob.Id, ItemId = chain.Id, Days = 14, Note = null, Since = new DateTimeOffset(2026, 10, 2, 8, 0, 0, TimeSpan.Zero) },
            new Like { ReaderId = ada.Id, ItemId = anvil.Id, Stars = 5 },
            new Like { ReaderId = ada.Id, ItemId = bell.Id, Stars = 3 },
            new Like { ReaderId = bob.Id, ItemId = anvil.Id, Stars = 4 });
        await db.SaveChangesAsync();
        return run;
    }

    private async Task ExecuteAsync(string text, object? parameters = null)
    {
        await using var session = fixture.OpenSession();
        await (parameters is null ? await session.RunAsync(text) : await session.RunAsync(text, parameters)).ConsumeAsync();
    }

    private async Task CleanupAsync(string run)
    {
        await ExecuteAsync("MATCH (n)-[r]-() WHERE n.Run = $run AND (n:FERShelf OR n:FERItem OR n:FERReader) DELETE r", new { run });
        await ExecuteAsync("MATCH (n) WHERE n.Run = $run AND (n:FERShelf OR n:FERItem OR n:FERReader) DELETE n", new { run });
    }

    public enum Level { Low = 1, Medium = 5, High = 10 }

    public sealed class Shelf { public Guid Id { get; set; } public string Run { get; set; } = ""; public string Name { get; set; } = ""; }

    public sealed class Item
    {
        public Guid Id { get; set; }
        public string Run { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Nickname { get; set; }
        public int Count { get; set; }
        public long Big { get; set; }
        public double Score { get; set; }
        public bool Active { get; set; }
        public Level Level { get; set; }
        public DateTimeOffset Due { get; set; }
        public int? Born { get; set; }
        public string Code { get; set; } = "";
        public Guid? ShelfId { get; set; }
    }

    public sealed class Reader { public Guid Id { get; set; } public string Run { get; set; } = ""; public string Name { get; set; } = ""; }

    public sealed class Loan
    {
        public Guid Id { get; set; }
        public Guid ReaderId { get; set; }
        public Guid ItemId { get; set; }
        public int Days { get; set; }
        public string? Note { get; set; }
        public DateTimeOffset Since { get; set; }
    }

    public sealed class Like { public Guid ReaderId { get; set; } public Guid ItemId { get; set; } public int Stars { get; set; } }

    public sealed class EntityContext(DbContextOptions<EntityContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<Shelf>().ToGraphNode("FERShelf").Property(s => s.Id).HasGraphPropertyName("shelf_key");
            var item = model.Entity<Item>();
            item.ToGraphNode("FERItem").Property(i => i.Id).HasGraphPropertyName("item_key");
            item.Property(i => i.Code).HasConversion(v => "code:" + v, v => v.Substring(5));
            item.HasOne<Shelf>().WithMany().HasForeignKey(i => i.ShelfId).IsRequired(false)
                .OnDelete(DeleteBehavior.Restrict).ToGraphReference("FER_ON_SHELF");
            model.Entity<Reader>().ToGraphNode("FERReader").Property(r => r.Id).HasGraphPropertyName("reader_key");
            var loan = model.Entity<Loan>();
            loan.HasOne<Reader>().WithMany().HasForeignKey(l => l.ReaderId);
            loan.HasOne<Item>().WithMany().HasForeignKey(l => l.ItemId);
            loan.Property(l => l.Id).HasGraphPropertyName("loan_key");
            loan.ToGraphIdentifiedRelationship("FER_LOAN", nameof(Loan.Id), [nameof(Loan.ReaderId)], [nameof(Loan.ItemId)]);
            var like = model.Entity<Like>();
            like.HasKey(l => new { l.ReaderId, l.ItemId });
            like.HasOne<Reader>().WithMany().HasForeignKey(l => l.ReaderId);
            like.HasOne<Item>().WithMany().HasForeignKey(l => l.ItemId);
            like.ToGraphRelationship("FER_LIKE", [nameof(Like.ReaderId)], [nameof(Like.ItemId)]);
        }
    }
}
