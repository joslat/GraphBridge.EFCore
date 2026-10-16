using GraphBridge.EntityFrameworkCore.Cypher;
using GraphBridge.EntityFrameworkCore.Update;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Neo4j.Driver;

namespace GraphBridge.UnitTests;

/// <summary>Actual EF entries captured by the existing planning-only test database; no simulated graph state or transport.</summary>
public sealed class NodeReferenceMutationTests : IDisposable
{
    private readonly IDriver _driver = ProviderTestDriver.Create();
    public void Dispose() => Assert.Empty(((ProviderTestDriver)(object)_driver).Calls);

    [Fact]
    public async Task New_blog_and_two_posts_create_nodes_before_owned_links_and_count_three_EF_entries()
    {
        using var context = Context();
        var blog = new Blog { Name = "Blog" };
        blog.Posts.Add(new Post { Title = "first `' MATCH (x) DELETE x" });
        blog.Posts.Add(new Post { Title = "second" });
        context.Add(blog);
        var plan = await Plan(context);
        Assert.Equal(3, plan.ExpectedStateEntries);
        Assert.Equal(5, plan.Operations.Count);
        Assert.Equal(new[] { GraphMutationPhase.NodeCreate, GraphMutationPhase.NodeCreate, GraphMutationPhase.NodeCreate,
            GraphMutationPhase.NodeReferenceAdd, GraphMutationPhase.NodeReferenceAdd }, plan.Operations.Select(operation => operation.Phase));
        Assert.Equal(3, context.ChangeTracker.Entries().Count());
        Assert.All(context.ChangeTracker.Entries(), entry => Assert.True(entry.Entity is Blog or Post));
        Assert.Equal(new[] { "Blog", "Post" }, plan.RequiredNodeBindings.Select(binding => binding.Label));
        Assert.Empty(plan.RequiredIdentifiedEdges);
        foreach (var node in plan.Operations.Take(3))
        {
            Assert.Empty(node.LockCommands);
            Assert.DoesNotContain("BlogId", Map(node.Command, "properties").Keys);
            Assert.Equal(1L, Map(node.Command, "properties")["Version"]);
            Assert.DoesNotContain("MATCH (x)", node.Command.Text);
        }
        foreach (var link in plan.Operations.Skip(3))
        {
            var post = Assert.IsType<Post>(link.Entry.ToEntityEntry().Entity);
            Assert.Same(blog, post.Blog);
            Assert.Equal(blog.Id, post.BlogId);
            Assert.Equal(2, link.LockCommands.Count);
            Assert.Contains("MATCH (n:`Post`", link.LockCommands[0].Text);
            Assert.Contains("MATCH (n:`Blog`", link.LockCommands[1].Text);
            Assert.Equal(post.Id.ToString("D"), link.LockCommands[0].Parameters["k0"]);
            Assert.Equal(blog.Id.ToString("D"), link.LockCommands[1].Parameters["k0"]);
            Assert.Contains("CREATE (p)-[:`HAS_POST`]->(d)", link.Command.Text);
            Assert.Contains("size(links) = 0", link.Command.Text);
            Assert.DoesNotContain("MERGE", link.Command.Text);
            Assert.Empty(link.GeneratedValues);
        }
        Assert.Equal(3, plan.GeneratedValues.Count);
        Assert.All(plan.GeneratedValues.GroupBy(value => value.Entry), group => Assert.Single(group));
        Assert.All(context.ChangeTracker.Entries(), entry =>
        {
            Assert.Equal(EntityState.Added, entry.State);
            Assert.Equal(0L, entry.Property("Version").CurrentValue);
        });
    }

    [Fact]
    public async Task Existing_principal_is_required_even_when_only_new_dependent_is_changed()
    {
        using var context = Context();
        var blogId = Guid.NewGuid();
        var post = new Post { Id = Guid.NewGuid(), BlogId = blogId, Title = "new" };
        context.Add(post);
        var plan = await Plan(context);
        Assert.Equal(1, plan.ExpectedStateEntries);
        Assert.Equal(2, plan.Operations.Count);
        Assert.Equal(new[] { "Blog", "Post" }, plan.RequiredNodeBindings.Select(binding => binding.Label));
        Assert.Equal(blogId.ToString("D"), plan.Operations[1].Command.Parameters["p0"]);
        Assert.Single(plan.GeneratedValues);
    }

    [Fact]
    public async Task Payload_update_locks_both_endpoints_checks_original_membership_then_reuses_node_concurrency()
    {
        using var context = Context();
        var post = Existing(context);
        post.Title = "changed";
        var plan = await Plan(context);
        var operation = Assert.Single(plan.Operations);
        Assert.Equal(GraphMutationPhase.NodeUpdate, operation.Phase);
        Assert.Equal(1, plan.ExpectedStateEntries);
        Assert.Equal(3, operation.LockCommands.Count);
        Assert.Contains("MATCH (n:`Post`", operation.LockCommands[0].Text);
        Assert.Contains("MATCH (n:`Blog`", operation.LockCommands[1].Text);
        Assert.Contains("size(links) = 1 AND parents[0] = p", operation.LockCommands[2].Text);
        Assert.DoesNotContain("DELETE", operation.LockCommands[2].Text);
        Assert.Equal(post.BlogId.ToString("D"), operation.LockCommands[2].Parameters["p0"]);
        Assert.Equal(new[] { "Title", "Version" }, Map(operation.Command, "changes").Keys.Order(StringComparer.Ordinal));
        Assert.Equal("changed", Map(operation.Command, "changes")["Title"]);
        Assert.Equal(8L, Map(operation.Command, "changes")["Version"]);
        Assert.Contains(7L, operation.Command.Parameters.Values);
        Assert.Contains(operation.Command.Parameters, parameter => parameter.Key.StartsWith("o", StringComparison.Ordinal) && parameter.Value is null);
        Assert.Equal(8L, Assert.Single(plan.GeneratedValues).Value);
        Assert.Equal(7L, post.Version);
        Assert.Equal(7L, context.Entry(post).Property(value => value.Version).OriginalValue);
        Assert.Equal(new[] { "Blog", "Post" }, plan.RequiredNodeBindings.Select(binding => binding.Label));
    }

    [Fact]
    public async Task Delete_removes_only_owned_membership_before_plain_node_delete_with_original_concurrency()
    {
        using var context = Context();
        var post = Existing(context);
        context.Remove(post);
        var plan = await Plan(context);
        var operation = Assert.Single(plan.Operations);
        Assert.Equal(GraphMutationPhase.NodeDelete, operation.Phase);
        Assert.Equal(3, operation.LockCommands.Count);
        Assert.Contains("FOREACH (r IN eligible | DELETE r)", operation.LockCommands[2].Text);
        Assert.Contains("size(links) = 1 AND parents[0] = p", operation.LockCommands[2].Text);
        Assert.Contains("DELETE n", operation.Command.Text);
        Assert.DoesNotContain("DETACH", operation.Command.Text);
        Assert.DoesNotContain("DELETE p", operation.LockCommands[2].Text);
        Assert.Contains(7L, operation.Command.Parameters.Values);
        Assert.Empty(plan.GeneratedValues);
        Assert.Equal(1, plan.ExpectedStateEntries);
    }

    [Fact]
    public async Task Principal_delete_alone_does_not_remove_dependents_or_owned_links()
    {
        using var context = Context();
        var blog = new Blog { Id = Guid.NewGuid(), Version = 1 };
        context.Attach(blog);
        context.Remove(blog);
        var operation = Assert.Single((await Plan(context)).Operations);
        Assert.Single(operation.LockCommands);
        Assert.Contains("DELETE n", operation.Command.Text);
        Assert.DoesNotContain("HAS_POST", operation.Command.Text);
        Assert.DoesNotContain("DETACH", operation.Command.Text);
    }

    [Fact]
    public async Task Acyclic_chain_creates_all_nodes_before_links_and_deletes_deepest_dependent_first()
    {
        using var create = Context(Chain);
        var first = new RootNode { Id = Guid.NewGuid() };
        var second = new MiddleNode { Id = Guid.NewGuid(), RootId = first.Id };
        var third = new LeafNode { Id = Guid.NewGuid(), MiddleId = second.Id };
        create.AddRange(first, second, third);
        var adds = await Plan(create);
        Assert.Equal(3, adds.ExpectedStateEntries);
        Assert.All(adds.Operations.Take(3), operation => Assert.Equal(GraphMutationPhase.NodeCreate, operation.Phase));
        Assert.All(adds.Operations.Skip(3), operation => Assert.Equal(GraphMutationPhase.NodeReferenceAdd, operation.Phase));
        Assert.Equal(2, adds.Operations.Count(operation => operation.Phase == GraphMutationPhase.NodeReferenceAdd));

        using var delete = Context(Chain);
        delete.AttachRange(first, second, third);
        // Respect EF's Restrict fixup while marking the graph; the planner input below is deliberately reversed.
        delete.Remove(third);
        delete.Remove(second);
        delete.Remove(first);
        // Force principal-first incoming order at the actual planner boundary; dependency ordering must repair it.
        delete.Capture.BeforePlan = entries =>
        {
            var ordered = entries.OrderBy(entry => entry.EntityType.ClrType == typeof(RootNode) ? 0
                : entry.EntityType.ClrType == typeof(MiddleNode) ? 1 : 2).ToArray();
            entries.Clear();
            foreach (var entry in ordered) entries.Add(entry);
        };
        var removes = await Plan(delete);
        Assert.Equal(new[] { typeof(LeafNode), typeof(MiddleNode), typeof(RootNode) }, removes.Operations.Select(operation => operation.Entry.EntityType.ClrType));
        Assert.All(removes.Operations, operation => Assert.Equal(GraphMutationPhase.NodeDelete, operation.Phase));
        Assert.Equal(3, removes.Operations[0].LockCommands.Count);
        Assert.Equal(3, removes.Operations[1].LockCommands.Count);
        Assert.Single(removes.Operations[2].LockCommands);
        Assert.Empty(removes.GeneratedValues);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scalar_or_navigation_reassignment_fails_complete_plan_before_transport(bool navigation)
    {
        using var context = Context();
        var post = Existing(context);
        if (navigation) post.Blog = new Blog { Id = Guid.NewGuid(), Name = "replacement" };
        else post.BlogId = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => context.SaveChangesAsync(false));
        Assert.Contains("cannot be reassigned", error.Message);
        Assert.Null(context.Capture.Plan);
        Assert.Equal(7L, post.Version);
    }

    [Fact]
    public async Task Deleted_dependent_cannot_disguise_reassignment_by_using_current_FK_as_original()
    {
        using var context = Context();
        var post = Existing(context);
        context.Remove(post);
        context.Capture.BeforePlan = entries =>
        {
            var entry = Assert.Single(entries);
            entry.SetOriginalValue(entry.EntityType.FindProperty(nameof(Post.BlogId))!, Guid.NewGuid());
        };
        Assert.Contains("cannot be reassigned", (await Assert.ThrowsAsync<NotSupportedException>(() => context.SaveChangesAsync(false))).Message);
        Assert.Null(context.Capture.Plan);
    }

    [Fact]
    public async Task Temporary_and_conceptually_null_FKs_are_rejected_at_actual_update_entry_boundary()
    {
        using var temporary = Context();
        temporary.Add(new Post { Id = Guid.NewGuid(), BlogId = Guid.NewGuid() });
        temporary.Capture.BeforePlan = entries => Assert.Single(entries).ToEntityEntry().Property(nameof(Post.BlogId)).IsTemporary = true;
        Assert.Contains("temporary", (await Assert.ThrowsAsync<InvalidOperationException>(() => temporary.SaveChangesAsync(false))).Message);
        Assert.Null(temporary.Capture.Plan);

        using var missing = Context();
        var post = Existing(missing);
        post.Title = "pending";
        missing.Capture.BeforePlan = entries => Assert.Single(entries).ToEntityEntry().Property(nameof(Post.BlogId)).CurrentValue = null;
        Assert.Contains("null", (await Assert.ThrowsAsync<InvalidOperationException>(() => missing.SaveChangesAsync(false))).Message);
        Assert.Null(missing.Capture.Plan);
    }

    [Fact]
    public async Task Composite_endpoint_slots_use_reviewed_store_names_and_parameterized_values()
    {
        using var context = Context(model =>
        {
            model.Entity<CompositeBlog>().ToGraphNode("CompositeBlog").HasKey(blog => new { blog.Tenant, blog.Code });
            model.Entity<CompositeBlog>().Property(blog => blog.Tenant).HasGraphPropertyName("tenant");
            model.Entity<CompositeBlog>().Property(blog => blog.Code).HasGraphPropertyName("code");
            model.Entity<CompositePost>().ToGraphNode("CompositePost").HasOne<CompositeBlog>().WithMany()
                .HasForeignKey(post => new { post.BlogTenant, post.BlogCode }).IsRequired().OnDelete(DeleteBehavior.Restrict)
                .ToGraphReference("HAS_COMPOSITE_POST");
        });
        var post = new CompositePost { Id = Guid.NewGuid(), BlogTenant = 7, BlogCode = "O'Reilly `$code`" };
        context.Add(post);
        var plan = await Plan(context);
        Assert.Equal(1, plan.ExpectedStateEntries);
        var link = plan.Operations[1];
        Assert.Contains("MATCH (p:`CompositeBlog` {`tenant`: $p0, `code`: $p1})", link.Command.Text);
        Assert.Equal(7L, link.Command.Parameters["p0"]);
        Assert.Equal(post.BlogCode, link.Command.Parameters["p1"]);
        Assert.DoesNotContain(post.BlogCode, link.Command.Text);
        Assert.DoesNotContain("BlogTenant", Map(plan.Operations[0].Command, "properties").Keys);
        Assert.DoesNotContain("BlogCode", Map(plan.Operations[0].Command, "properties").Keys);
    }

    [Fact]
    public async Task Physical_payload_named_like_derived_FK_is_still_saved_as_payload()
    {
        using var context = Context(model =>
        {
            Configure(model);
            model.Entity<Post>().Property(post => post.Title).HasGraphPropertyName(nameof(Post.BlogId));
        });
        var post = new Post { Id = Guid.NewGuid(), BlogId = Guid.NewGuid(), Title = "payload, not endpoint" };
        context.Add(post);
        var plan = await Plan(context);
        Assert.Equal(post.Title, Map(plan.Operations[0].Command, "properties")["BlogId"]);
        Assert.Equal(post.BlogId.ToString("D"), plan.Operations[1].Command.Parameters["p0"]);
    }

    [Fact]
    public void Reference_renderer_bounds_membership_before_principal_filter_and_rejects_unsafe_metadata_or_missing_keys()
    {
        var principal = new Dictionary<string, object?> { ["id"] = "principal" };
        var dependent = new Dictionary<string, object?> { ["id"] = "dependent" };
        var check = CypherNodeReferenceMutationRenderer.Assert("HAS_POST", "Blog", principal, "Post", dependent);
        Assert.Contains("OPTIONAL MATCH (parent)-[r:`HAS_POST`]->(d)", check.Text);
        Assert.Contains("WITH parent,r LIMIT 2", check.Text);
        Assert.True(check.Text.IndexOf("LIMIT 2", StringComparison.Ordinal) < check.Text.IndexOf("MATCH (p:`Blog`", StringComparison.Ordinal));
        Assert.Contains("size(links) = 1 AND parents[0] = p", check.Text);
        Assert.DoesNotContain("parent:`Blog`", check.Text);
        Assert.DoesNotContain("DISTINCT", check.Text);
        Assert.DoesNotContain("elementId", check.Text);
        Assert.Throws<ArgumentException>(() => CypherNodeReferenceMutationRenderer.Create("TYPE`", "Blog", principal, "Post", dependent));
        Assert.Throws<ArgumentException>(() => CypherNodeReferenceMutationRenderer.Delete("HAS_POST", "Blog`", principal, "Post", dependent));
        Assert.Throws<InvalidOperationException>(() => CypherNodeReferenceMutationRenderer.Assert("HAS_POST", "Blog", new Dictionary<string, object?>(), "Post", dependent));
        Assert.Throws<InvalidOperationException>(() => CypherNodeReferenceMutationRenderer.Assert("HAS_POST", "Blog", principal, "Post", new Dictionary<string, object?> { ["id"] = null }));
    }

    private AssociationMutationTests.PlanContext Context(Action<ModelBuilder>? mapping = null) => new(_driver, mapping ?? Configure);
    private static async Task<GraphMutationPlan> Plan(AssociationMutationTests.PlanContext context)
    {
        var count = await context.SaveChangesAsync(acceptAllChangesOnSuccess: false);
        var plan = Assert.IsType<GraphMutationPlan>(context.Capture.Plan);
        Assert.Equal(plan.ExpectedStateEntries, count);
        return plan;
    }
    private static IReadOnlyDictionary<string, object?> Map(CypherMutationCommand command, string name)
        => Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(command.Parameters[name]);
    private static Post Existing(AssociationMutationTests.PlanContext context)
    {
        var post = new Post { Id = Guid.NewGuid(), BlogId = Guid.NewGuid(), Title = "original", Version = 7 };
        context.Attach(post);
        return post;
    }
    private static void Configure(ModelBuilder model)
    {
        model.Entity<Blog>().ToGraphNode("Blog").Property(blog => blog.Version).IsRowVersion();
        model.Entity<Post>().ToGraphNode("Post").Property(post => post.Version).IsRowVersion();
        model.Entity<Post>().Property(post => post.Token).IsConcurrencyToken();
        model.Entity<Post>().HasOne(post => post.Blog).WithMany(blog => blog.Posts).HasForeignKey(post => post.BlogId)
            .IsRequired().OnDelete(DeleteBehavior.Restrict).ToGraphReference("HAS_POST");
    }
    private static void Chain(ModelBuilder model)
    {
        model.Entity<RootNode>().ToGraphNode("Root");
        model.Entity<MiddleNode>().ToGraphNode("Middle").HasOne<RootNode>().WithMany().HasForeignKey(node => node.RootId)
            .IsRequired().OnDelete(DeleteBehavior.Restrict).ToGraphReference("ROOT_MIDDLE");
        model.Entity<LeafNode>().ToGraphNode("Leaf").HasOne<MiddleNode>().WithMany().HasForeignKey(node => node.MiddleId)
            .IsRequired().OnDelete(DeleteBehavior.Restrict).ToGraphReference("MIDDLE_LEAF");
    }
    public sealed class Blog
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public long Version { get; set; }
        public List<Post> Posts { get; } = [];
    }
    public sealed class Post
    {
        public Guid Id { get; set; }
        public Guid BlogId { get; set; }
        public string Title { get; set; } = "";
        public string? Token { get; set; }
        public long Version { get; set; }
        public Blog Blog { get; set; } = null!;
    }
    public sealed class RootNode { public Guid Id { get; set; } }
    public sealed class MiddleNode { public Guid Id { get; set; } public Guid RootId { get; set; } }
    public sealed class LeafNode { public Guid Id { get; set; } public Guid MiddleId { get; set; } }
    public sealed class CompositeBlog { public long Tenant { get; set; } public string Code { get; set; } = ""; }
    public sealed class CompositePost { public Guid Id { get; set; } public long BlogTenant { get; set; } public string BlogCode { get; set; } = ""; }
}
