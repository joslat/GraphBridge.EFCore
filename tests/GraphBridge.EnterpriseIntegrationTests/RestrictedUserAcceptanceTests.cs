using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using Xunit.Abstractions;

namespace GraphBridge.EnterpriseIntegrationTests;

[Collection(EnterpriseCollection.Name)]
public sealed class RestrictedUserAcceptanceTests(EnterpriseFixture fixture, ITestOutputHelper output)
{
    private const string Forbidden = "Neo.ClientError.Security.Forbidden";

    [EnterpriseFact]
    public void Effective_privileges_are_audited_including_cleared_PUBLIC_defaults()
    {
        // These rows contain privilege metadata only, never passwords/auth tokens/query parameters.
        output.WriteLine("Pinned Enterprise image: " + EnterpriseFixture.PinnedImage);
        output.WriteLine("Original owned-fixture PUBLIC grants: " + JsonSerializer.Serialize(fixture.OriginalPublicPrivileges));
        output.WriteLine("Effective restricted-user grants after clearing PUBLIC: " + JsonSerializer.Serialize(fixture.EffectivePrivileges));
        Assert.Equal(new[] { "gb_no_schema", "gb_reader", "gb_writer" }, fixture.EffectivePrivileges.Keys.Order(StringComparer.Ordinal));
        Assert.All(fixture.EffectivePrivileges.Values.SelectMany(rows => rows), row =>
        {
            Assert.Equal("neo4j", row.Graph);
            Assert.Equal("GRANTED", row.Access);
            Assert.False(row.Immutable);
            Assert.DoesNotContain(row.Action, new[] { "all_dbms_privileges", "create_user", "create_constraint", "name_management", "load" });
        });
    }

    [EnterpriseFact]
    public async Task Reader_executes_parameterized_LINQ_native_reference_Include_and_identified_edge_reads()
    {
        var data = RunData.New();
        try
        {
            await SeedAsync(data);
            await using var context = Context(fixture.Reader);
            var blogs = await context.Set<Blog>().Where(b => b.Run == data.Run).Include(b => b.Posts).ToListAsync();
            var blog = Assert.Single(blogs);
            var post = Assert.Single(blog.Posts);
            Assert.Equal(data.Post, post.Id);
            Assert.Same(blog, post.Blog);
            Assert.True(context.Entry(blog).Collection(b => b.Posts).IsLoaded);
            Assert.Same(post, await context.Set<Post>().Where(p => p.Id == data.Post).Include(p => p.Blog).SingleAsync());
            var credit = await context.Set<Credit>().SingleAsync(c => c.Id == data.Credit);
            Assert.Equal(data.Blog, credit.BlogId);
            Assert.Equal(data.Movie, credit.MovieId);
            Assert.Equal("lead", credit.Role);
            Assert.DoesNotContain(data.Run, context.Set<Post>().Where(p => p.Run == data.Run).ToQueryString());
        }
        finally { await fixture.CleanupAsync(data.Run); }
    }

    [EnterpriseFact]
    public async Task Writer_creates_updates_and_deletes_nodes_owned_references_and_identified_edges_without_admin_privileges()
    {
        var data = RunData.New();
        try
        {
            await using (var context = Context(fixture.Writer))
            {
                var blog = new Blog { Id = data.Blog, Name = "writer blog", Run = data.Run };
                var post = new Post { Id = data.Post, Title = "writer post", Blog = blog, Run = data.Run };
                var movie = new Movie { Id = data.Movie, Title = "writer movie", Run = data.Run };
                var credit = new Credit { Id = data.Credit, BlogId = blog.Id, MovieId = movie.Id, Role = "lead", Run = data.Run };
                context.AddRange(blog, post, movie, credit);
                Assert.Equal(4, await context.SaveChangesAsync());
                Assert.Equal(1L, blog.Version);
                Assert.Equal(1L, post.Version);
                Assert.Equal(1L, credit.Version);
            }
            await using (var context = Context(fixture.Writer))
            {
                var blog = await context.Set<Blog>().Where(b => b.Id == data.Blog).Include(b => b.Posts).SingleAsync();
                var post = Assert.Single(blog.Posts);
                var credit = await context.Set<Credit>().SingleAsync(c => c.Id == data.Credit);
                blog.Name = "updated blog";
                post.Title = "updated post";
                credit.Role = "updated role";
                Assert.Equal(3, await context.SaveChangesAsync());
                Assert.Equal(2L, post.Version);
                Assert.Equal(2L, credit.Version);
            }
            await using (var verify = Context(fixture.Reader))
            {
                Assert.Equal("updated post", await verify.Set<Post>().Where(p => p.Id == data.Post).Select(p => p.Title).SingleAsync());
                Assert.Equal("updated role", await verify.Set<Credit>().Where(c => c.Id == data.Credit).Select(c => c.Role).SingleAsync());
            }
            await using (var native = fixture.Admin.AsyncSession(o => o.WithDatabase(EnterpriseFixture.Database)))
            {
                var cursor = await native.RunAsync("MATCH (b:EnterpriseBlog {Id: $blog})-[r:ENTERPRISE_HAS_POST]->(p:EnterprisePost {Id: $post}) RETURN count(r) AS count, p.BlogId AS scalarFk", new
                {
                    blog = data.Blog.ToString("D"), post = data.Post.ToString("D")
                });
                var row = await cursor.SingleAsync();
                Assert.Equal(1L, row["count"].As<long>());
                Assert.Null(row["scalarFk"]);
            }
            await using (var context = Context(fixture.Writer))
            {
                var post = await context.Set<Post>().SingleAsync(p => p.Id == data.Post);
                var blog = await context.Set<Blog>().SingleAsync(b => b.Id == data.Blog);
                var movie = await context.Set<Movie>().SingleAsync(m => m.Id == data.Movie);
                var credit = await context.Set<Credit>().SingleAsync(c => c.Id == data.Credit);
                context.Remove(credit);
                context.Remove(post);
                context.Remove(blog);
                context.Remove(movie);
                Assert.Equal(4, await context.SaveChangesAsync());
            }
            Assert.Equal(0, await fixture.CountRunAsync(data.Run));
        }
        finally { await fixture.CleanupAsync(data.Run); }
    }

    [EnterpriseFact]
    public async Task Reader_SaveChanges_is_forbidden_preserves_pending_EF_state_and_writes_nothing()
    {
        var data = RunData.New();
        try
        {
            await using var context = Context(fixture.Reader);
            var blog = new Blog { Id = data.Blog, Name = "denied", Run = data.Run };
            var post = new Post { Id = data.Post, Blog = blog, Title = "denied", Run = data.Run };
            context.AddRange(blog, post);
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.Equal(Forbidden, Assert.IsAssignableFrom<Neo4jException>(error.InnerException).Code);
            Assert.Equal(2, error.Entries.Count);
            Assert.All(context.ChangeTracker.Entries(), entry => Assert.Equal(EntityState.Added, entry.State));
            Assert.Equal(0L, blog.Version);
            Assert.Equal(0L, post.Version);
            Assert.Equal(0, await fixture.CountRunAsync(data.Run));
            Assert.Null(context.Database.CurrentTransaction);
        }
        finally { await fixture.CleanupAsync(data.Run); }
    }

    [EnterpriseFact]
    public async Task Missing_SHOW_CONSTRAINT_privilege_denies_SaveChanges_before_mutation_despite_WRITE()
    {
        var data = RunData.New();
        try
        {
            // First establish that WRITE itself is genuinely granted, in an explicitly rolled-back transaction.
            await using (var session = fixture.NoSchemaWriter.AsyncSession(o => o.WithDatabase(EnterpriseFixture.Database)))
            await using (var transaction = await session.BeginTransactionAsync())
            {
                await (await transaction.RunAsync("CREATE (:EnterpriseBlog {Id: $id, Name: 'rollback write proof', Version: 1, Run: $run})",
                    new { id = data.Blog.ToString("D"), run = data.Run })).ConsumeAsync();
                await transaction.RollbackAsync();
            }
            await using var context = Context(fixture.NoSchemaWriter);
            var blog = new Blog { Id = data.Blog, Name = "no schema visibility", Run = data.Run };
            context.Add(blog);
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.Equal(Forbidden, Assert.IsAssignableFrom<Neo4jException>(error.InnerException).Code);
            Assert.Equal(EntityState.Added, context.Entry(blog).State);
            Assert.Equal(0L, blog.Version);
            Assert.Equal(0, await fixture.CountRunAsync(data.Run));
            await AssertForbiddenRollbackAsync(fixture.NoSchemaWriter, EnterpriseFixture.Database,
                "SHOW CONSTRAINTS YIELD name RETURN name");
        }
        finally { await fixture.CleanupAsync(data.Run); }
    }

    [EnterpriseFact]
    public async Task Reader_and_writer_cannot_create_schema_or_users_and_all_probes_rollback()
    {
        foreach (var driver in new[] { fixture.Reader, fixture.Writer })
        {
            var suffix = Guid.NewGuid().ToString("N");
            await AssertForbiddenRollbackAsync(driver, EnterpriseFixture.Database,
                $"CREATE CONSTRAINT gb_rbac_denied_{suffix} FOR (b:EnterpriseBlog) REQUIRE b.Run IS UNIQUE");
            await AssertForbiddenRollbackAsync(driver, "system",
                $"CREATE USER gb_denied_{suffix} SET PASSWORD $password CHANGE NOT REQUIRED",
                new { password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) });
            await using var admin = fixture.Admin.AsyncSession(o => o.WithDatabase("system"));
            var users = await admin.RunAsync("SHOW USERS YIELD user WHERE user = $user RETURN user", new { user = "gb_denied_" + suffix });
            Assert.Empty(await users.ToListAsync());
        }
    }

    [EnterpriseFact]
    public async Task Writer_cannot_create_unreviewed_property_label_or_relationship_tokens()
    {
        var data = RunData.New();
        try
        {
            await SeedAsync(data);
            var parameters = new { id = data.Blog.ToString("D") };
            var suffix = Guid.NewGuid().ToString("N");
            await AssertForbiddenRollbackAsync(fixture.Writer, EnterpriseFixture.Database,
                $"MATCH (b:EnterpriseBlog {{Id: $id}}) SET b.UnreviewedProperty_{suffix} = 1", parameters);
            await AssertForbiddenRollbackAsync(fixture.Writer, EnterpriseFixture.Database,
                $"MATCH (b:EnterpriseBlog {{Id: $id}}) SET b:UnreviewedLabel_{suffix}", parameters);
            await AssertForbiddenRollbackAsync(fixture.Writer, EnterpriseFixture.Database,
                $"MATCH (b:EnterpriseBlog {{Id: $id}}) CREATE (b)-[:UNREVIEWED_TYPE_{suffix}]->(b)", parameters);
            Assert.Equal(3, await fixture.CountRunAsync(data.Run));
        }
        finally { await fixture.CleanupAsync(data.Run); }
    }

    private async Task SeedAsync(RunData data)
        => await fixture.ExecuteAdminAsync("""
            CREATE (b:EnterpriseBlog {Id: $blog, Name: 'seed blog', Version: 1, Run: $run}),
                   (p:EnterprisePost {Id: $post, Title: 'seed post', Version: 1, Run: $run}),
                   (m:EnterpriseMovie {Id: $movie, Title: 'seed movie', Run: $run}),
                   (b)-[:ENTERPRISE_HAS_POST]->(p),
                   (b)-[:ENTERPRISE_CREDIT {Id: $credit, Role: 'lead', Version: 1, Run: $run}]->(m)
            """, new { blog = data.Blog.ToString("D"), post = data.Post.ToString("D"), movie = data.Movie.ToString("D"), credit = data.Credit.ToString("D"), run = data.Run });

    private static async Task AssertForbiddenRollbackAsync(IDriver driver, string database, string command, object? parameters = null)
    {
        await using var session = driver.AsyncSession(o => o.WithDatabase(database));
        await using var transaction = await session.BeginTransactionAsync();
        Exception? failure;
        try
        {
            failure = await Record.ExceptionAsync(async () =>
            {
                await (await transaction.RunAsync(command, parameters ?? new { })).ConsumeAsync();
            });
        }
        finally
        {
            // Even an unexpectedly permitted administrative probe is never committed.
            await transaction.RollbackAsync();
        }
        Assert.Equal(Forbidden, Assert.IsAssignableFrom<Neo4jException>(failure).Code);
    }

    private static RestrictedContext Context(IDriver driver)
        => new(new DbContextOptionsBuilder<RestrictedContext>().UseNeo4j(driver, EnterpriseFixture.Database).Options);

    private sealed record RunData(string Run, Guid Blog, Guid Post, Guid Movie, Guid Credit)
    {
        public static RunData New() => new(Guid.NewGuid().ToString("N"), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    }

    public sealed class RestrictedContext(DbContextOptions<RestrictedContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<Blog>().ToGraphNode("EnterpriseBlog").Property(b => b.Version).IsRowVersion();
            model.Entity<Post>().ToGraphNode("EnterprisePost").Property(p => p.Version).IsRowVersion();
            model.Entity<Post>().HasOne(p => p.Blog).WithMany(b => b.Posts).HasForeignKey(p => p.BlogId)
                .IsRequired().OnDelete(DeleteBehavior.Restrict).ToGraphReference("ENTERPRISE_HAS_POST");
            model.Entity<Movie>().ToGraphNode("EnterpriseMovie");
            var credit = model.Entity<Credit>();
            credit.HasOne<Blog>().WithMany().HasForeignKey(c => c.BlogId).OnDelete(DeleteBehavior.Restrict);
            credit.HasOne<Movie>().WithMany().HasForeignKey(c => c.MovieId).OnDelete(DeleteBehavior.Restrict);
            credit.Property(c => c.Version).IsRowVersion();
            credit.ToGraphIdentifiedRelationship("ENTERPRISE_CREDIT", nameof(Credit.Id), [nameof(Credit.BlogId)], [nameof(Credit.MovieId)]);
        }
    }
    public sealed class Blog
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string Run { get; set; } = "";
        public long Version { get; set; }
        public List<Post> Posts { get; set; } = [];
    }
    public sealed class Post
    {
        public Guid Id { get; set; }
        public Guid BlogId { get; set; }
        public string Title { get; set; } = "";
        public string Run { get; set; } = "";
        public long Version { get; set; }
        public Blog Blog { get; set; } = null!;
    }
    public sealed class Movie { public Guid Id { get; set; } public string Title { get; set; } = ""; public string Run { get; set; } = ""; }
    public sealed class Credit
    {
        public Guid Id { get; set; }
        public Guid BlogId { get; set; }
        public Guid MovieId { get; set; }
        public string Role { get; set; } = "";
        public string Run { get; set; } = "";
        public long Version { get; set; }
    }
}
