using System.Reflection;
using GraphBridge.EntityFrameworkCore.Cypher;
using GraphBridge.EntityFrameworkCore.Diagnostics;
using GraphBridge.EntityFrameworkCore.Metadata;
using GraphBridge.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Neo4j.Driver;

namespace GraphBridge.UnitTests;

/// <summary>Real EF query compilation/shaping over scripted transport; live Cypher execution is a separate gate.</summary>
public sealed class NodeReferenceIncludeTests
{
    private static readonly Guid BlogId = Guid.Parse("11000000-0000-0000-0000-000000000001");
    private static readonly Guid EmptyBlogId = Guid.Parse("11000000-0000-0000-0000-000000000002");
    private static readonly Guid PostId = Guid.Parse("22000000-0000-0000-0000-000000000001");
    private static readonly Guid SecondPostId = Guid.Parse("22000000-0000-0000-0000-000000000002");

    [Fact]
    public void Collection_translation_pages_roots_before_expansion_and_checks_every_physical_child()
    {
        var driver = ProviderTestDriver.Create();
        using var context = Context(driver);
        var text = context.Set<Blog>().Where(b => b.Name != "private").OrderBy(b => b.Name)
            .Skip(1).Take(2).Include(b => b.Posts).ToQueryString();
        Assert.StartsWith("MATCH (n:`IncludeBlog`)", text);
        Assert.Contains("\nWITH n\nORDER BY", text);
        Assert.True(text.IndexOf("\nLIMIT $", StringComparison.Ordinal) < text.IndexOf("\nCALL (n)", StringComparison.Ordinal));
        Assert.Contains("MATCH (n)-[:`INCLUDE_HAS_POST`]->(i)", text);
        Assert.DoesNotContain("->(i:`IncludePost`)", text);
        Assert.Contains("OPTIONAL MATCH (parent)-[candidate:`INCLUDE_HAS_POST`]->(i)", text);
        Assert.Contains("WITH parent LIMIT 2", text);
        Assert.Contains("RETURN collect({", text);
        Assert.Contains("coalesce(i:`IncludePost`, false)", text);
        Assert.Contains("ip.`Id`", text);
        Assert.DoesNotContain("i.`BlogId`", text);
        Assert.DoesNotContain("DISTINCT", text);
        Assert.DoesNotContain("private", text);
        Assert.Contains("\nRETURN n.", text);
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Fact]
    public void Reference_translation_keeps_orphan_root_and_uses_already_validated_parent()
    {
        var driver = ProviderTestDriver.Create();
        using var context = Context(driver);
        var text = context.Set<Post>().Include(p => p.Blog).OrderBy(p => p.Title).Take(1).ToQueryString();
        Assert.StartsWith("MATCH (n:`IncludePost`)\nCALL (n)", text);
        Assert.Contains("OPTIONAL MATCH", text);
        Assert.Contains(CypherQueryPlan.NodeReferenceCountAlias, text);
        Assert.Contains("s.`Name`", text);
        Assert.Contains("coalesce(s:`IncludeBlog`, false)", text);
        Assert.DoesNotContain("MATCH (s:", text);
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Theory]
    [InlineData(QueryTrackingBehavior.TrackAll)]
    [InlineData(QueryTrackingBehavior.NoTracking)]
    [InlineData(QueryTrackingBehavior.NoTrackingWithIdentityResolution)]
    public async Task Collection_query_uses_EF_materializers_fixup_and_empty_collection_with_one_data_command(QueryTrackingBehavior tracking)
    {
        var script = new Script();
        await using var context = Context(script.Driver);
        context.ChangeTracker.QueryTrackingBehavior = tracking;
        var root = Node<Blog>(context);
        var child = Reference<Post>(context);
        script.Rows.Add(CollectionRow(root, child, BlogId, PostId, SecondPostId));
        script.Rows.Add(CollectionRow(root, child, EmptyBlogId));
        var trackedFromQuery = new List<bool>();
        context.ChangeTracker.Tracked += (_, args) => trackedFromQuery.Add(args.FromQuery);
        var result = await context.Set<Blog>().Include(b => b.Posts).ToListAsync();
        Assert.Equal(2, result.Count);
        Assert.Equal(2, result[0].Posts.Count);
        Assert.All(result[0].Posts, p => Assert.Same(result[0], p.Blog));
        Assert.NotNull(result[1].Posts);
        Assert.Empty(result[1].Posts);
        Assert.Equal(1, script.DataCommands);
        Assert.Equal(1, script.SchemaCommands);
        Assert.Equal(1, script.SessionDisposals);
        if (tracking == QueryTrackingBehavior.TrackAll)
        {
            Assert.Equal(4, context.ChangeTracker.Entries().Count());
            Assert.All(trackedFromQuery, value => Assert.True(value));
            Assert.True(context.Entry(result[0]).Collection(b => b.Posts).IsLoaded);
            Assert.True(context.Entry(result[1]).Collection(b => b.Posts).IsLoaded);
            Assert.All(result[0].Posts, p => Assert.True(context.Entry(p).Reference(v => v.Blog).IsLoaded));
            Assert.All(context.ChangeTracker.Entries(), entry => Assert.Equal(EntityState.Unchanged, entry.State));
        }
        else Assert.Empty(context.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData(QueryTrackingBehavior.TrackAll, true)]
    [InlineData(QueryTrackingBehavior.NoTracking, false)]
    [InlineData(QueryTrackingBehavior.NoTrackingWithIdentityResolution, true)]
    public async Task Reference_include_preserves_tracking_mode_identity_and_does_not_mark_partial_inverse_collection_loaded(
        QueryTrackingBehavior tracking, bool sameParent)
    {
        var script = new Script();
        await using var context = Context(script.Driver);
        context.ChangeTracker.QueryTrackingBehavior = tracking;
        var root = Reference<Post>(context);
        var parent = Node<Blog>(context);
        script.Rows.Add(ReferenceRow(root, parent, PostId));
        script.Rows.Add(ReferenceRow(root, parent, SecondPostId));
        var posts = await context.Set<Post>().Include(p => p.Blog).ToListAsync();
        Assert.Equal(sameParent, ReferenceEquals(posts[0].Blog, posts[1].Blog));
        Assert.All(posts, post => Assert.Contains(post, post.Blog.Posts));
        Assert.Equal(sameParent ? 2 : 1, posts[0].Blog.Posts.Count);
        if (tracking == QueryTrackingBehavior.TrackAll)
        {
            Assert.Equal(3, context.ChangeTracker.Entries().Count());
            Assert.All(posts, p => Assert.True(context.Entry(p).Reference(v => v.Blog).IsLoaded));
            Assert.False(context.Entry(posts[0].Blog).Collection(b => b.Posts).IsLoaded);
        }
        else Assert.Empty(context.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData("orphan")]
    [InlineData("parallel")]
    [InlineData("wrong-parent-label")]
    [InlineData("wrong-child-label")]
    [InlineData("missing-parent-key")]
    [InlineData("noncanonical-child-key")]
    public async Task Every_related_row_is_decoded_before_any_current_root_materializer(string fault)
    {
        var script = new Script();
        await using var context = Context(script.Driver);
        var root = Node<Blog>(context);
        var child = Reference<Post>(context);
        var row = CollectionRow(root, child, BlogId, PostId, SecondPostId);
        var maps = (List<object>)row[CypherIncludePlan.RowsAlias];
        var invalid = (Dictionary<string, object>)maps[1];
        switch (fault)
        {
            case "orphan": invalid[CypherQueryPlan.NodeReferenceCountAlias] = 0L; break;
            case "parallel": invalid[CypherQueryPlan.NodeReferenceCountAlias] = 2L; break;
            case "wrong-parent-label": invalid[CypherQueryPlan.NodeReferencePrincipalAlias] = false; break;
            case "wrong-child-label": invalid[CypherIncludePlan.IncludedLabelAlias] = false; break;
            case "missing-parent-key": invalid[Alias(child, nameof(Post.BlogId))] = null!; break;
            case "noncanonical-child-key": invalid[Alias(child, nameof(Post.Id))] = "ABCDEF00-1234-5678-90AB-CDEF00123456"; break;
        }
        script.Rows.Add(row);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Set<Blog>().Include(b => b.Posts).ToListAsync());
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Equal(1, script.DataConsumes);
        Assert.Equal(1, script.SessionDisposals);
    }

    [Fact]
    public async Task Invalid_later_root_keeps_only_prior_successful_roots_tracked()
    {
        var script = new Script();
        await using var context = Context(script.Driver);
        var root = Node<Blog>(context);
        var child = Reference<Post>(context);
        script.Rows.Add(CollectionRow(root, child, EmptyBlogId));
        var invalid = CollectionRow(root, child, BlogId, PostId);
        ((Dictionary<string, object>)((List<object>)invalid[CypherIncludePlan.RowsAlias])[0])[CypherQueryPlan.NodeReferenceCountAlias] = 2L;
        script.Rows.Add(invalid);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Set<Blog>().Include(b => b.Posts).ToListAsync());
        Assert.Equal(EmptyBlogId, Assert.IsType<Blog>(Assert.Single(context.ChangeTracker.Entries()).Entity).Id);
    }

    [Fact]
    public async Task Reference_orphan_rejects_before_parent_or_root_materialization()
    {
        var script = new Script();
        await using var context = Context(script.Driver);
        var row = ReferenceRow(Reference<Post>(context), Node<Blog>(context), PostId);
        row[CypherQueryPlan.NodeReferenceCountAlias] = 0L;
        script.Rows.Add(row);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Set<Post>().Include(p => p.Blog).ToListAsync());
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task TrackAll_include_does_not_overwrite_locally_reassigned_pretracked_relationship()
    {
        var script = new Script();
        await using var context = Context(script.Driver);
        var blog = new Blog { Id = BlogId, Name = "local name" };
        var other = new Blog { Id = EmptyBlogId };
        var post = new Post { Id = PostId, BlogId = BlogId, Blog = blog, Title = "local title" };
        context.AttachRange(blog, other, post);
        post.Blog = other;
        context.ChangeTracker.DetectChanges();
        script.Rows.Add(CollectionRow(Node<Blog>(context), Reference<Post>(context), BlogId, PostId));
        var result = Assert.Single(await context.Set<Blog>().Include(b => b.Posts).ToListAsync());
        Assert.Same(blog, result);
        Assert.Empty(blog.Posts);
        Assert.Same(other, post.Blog);
        Assert.Equal("local name", blog.Name);
        Assert.Equal("local title", post.Title);
        Assert.True(context.Entry(blog).Collection(b => b.Posts).IsLoaded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrackAll_does_not_mark_locally_reassigned_unresolved_reference_loaded(bool collection)
    {
        var script = new Script();
        await using var context = Context(script.Driver);
        var blog = new Blog { Id = BlogId };
        var post = new Post { Id = PostId, BlogId = BlogId, Blog = blog };
        context.Attach(post);
        post.BlogId = EmptyBlogId;
        context.ChangeTracker.DetectChanges();
        Assert.Null(post.Blog);
        Assert.False(context.Entry(post).Reference(p => p.Blog).IsLoaded);
        if (collection)
        {
            script.Rows.Add(CollectionRow(Node<Blog>(context), Reference<Post>(context), BlogId, PostId));
            _ = await context.Set<Blog>().Include(b => b.Posts).ToListAsync();
            Assert.True(context.Entry(blog).Collection(b => b.Posts).IsLoaded);
        }
        else
        {
            script.Rows.Add(ReferenceRow(Reference<Post>(context), Node<Blog>(context), PostId));
            _ = await context.Set<Post>().Include(p => p.Blog).ToListAsync();
        }
        Assert.Null(post.Blog);
        Assert.Equal(EmptyBlogId, post.BlogId);
        Assert.False(context.Entry(post).Reference(p => p.Blog).IsLoaded);
        Assert.Empty(blog.Posts);
    }

    [Fact]
    public async Task Early_disposal_and_cancellation_consume_owned_cursor_without_materializing_remaining_roots()
    {
        var script = new Script();
        await using var context = Context(script.Driver);
        script.Rows.Add(CollectionRow(Node<Blog>(context), Reference<Post>(context), BlogId));
        script.Rows.Add(CollectionRow(Node<Blog>(context), Reference<Post>(context), EmptyBlogId));
        await using (var iterator = context.Set<Blog>().Include(b => b.Posts).AsAsyncEnumerable().GetAsyncEnumerator())
            Assert.True(await iterator.MoveNextAsync());
        Assert.Single(context.ChangeTracker.Entries());
        Assert.Equal(1, script.DataConsumes);
        Assert.Equal(1, script.SessionDisposals);

        var cancelled = new Script();
        await using var other = Context(cancelled.Driver);
        cancelled.Rows.Add(CollectionRow(Node<Blog>(other), Reference<Post>(other), BlogId, PostId));
        using var token = new CancellationTokenSource();
        cancelled.OnDataFetch = token.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => other.Set<Blog>().Include(b => b.Posts).ToListAsync(token.Token));
        Assert.Empty(other.ChangeTracker.Entries());
        Assert.Equal(1, cancelled.DataConsumes);
        Assert.Equal(1, cancelled.SessionDisposals);
    }

    [Fact]
    public async Task Borrowed_transaction_is_not_committed_rolled_back_or_disposed_by_Include_enumerator()
    {
        var script = new Script();
        await using var context = Context(script.Driver);
        script.Rows.Add(CollectionRow(Node<Blog>(context), Reference<Post>(context), BlogId, PostId));
        await using var transaction = await context.Database.BeginTransactionAsync();
        _ = await context.Set<Blog>().Include(b => b.Posts).ToListAsync();
        Assert.Equal(2, script.TransactionRuns);
        Assert.Equal(0, script.TransactionDisposals);
        Assert.Equal(0, script.TransactionCommits);
        Assert.Equal(0, script.TransactionRollbacks);
        Assert.Equal(0, script.SessionDisposals);
        await transaction.RollbackAsync();
        Assert.Equal(1, script.TransactionRollbacks);
    }

    [Theory]
    [InlineData("IncludeBlog")]
    [InlineData("IncludePost")]
    public async Task Missing_either_endpoint_constraint_rejects_before_data_command(string missingLabel)
    {
        var script = new Script { MissingConstraint = missingLabel };
        await using var context = Context(script.Driver);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Set<Blog>().Include(b => b.Posts).ToListAsync());
        Assert.Equal(0, script.DataCommands);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public void Unsupported_Include_shapes_reject_before_transport()
    {
        var driver = ProviderTestDriver.Create();
        using var context = Context(driver);
        var blogs = context.Set<Blog>();
        Assert.Throws<Neo4jQueryTranslationException>(() => blogs.Include(b => b.Posts.Where(p => p.Title == "x")).ToQueryString());
        Assert.Throws<Neo4jQueryTranslationException>(() => blogs.Include(b => b.Posts).ThenInclude(p => p.Blog).ToQueryString());
        Assert.Throws<Neo4jQueryTranslationException>(() => blogs.Include(b => b.Posts).Include(b => b.Posts).ToQueryString());
        Assert.Throws<Neo4jQueryTranslationException>(() => blogs.Include("Posts").ToQueryString());
        Assert.Throws<Neo4jQueryTranslationException>(() => blogs.Include(b => b.Posts).Select(b => b.Name).ToQueryString());
        Assert.Throws<Neo4jQueryTranslationException>(() => blogs.Include(b => b.Posts).Count());
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Included_target_filters_and_AutoInclude_reject_while_root_filters_use_EF_preprocessing(bool autoInclude)
    {
        var driver = ProviderTestDriver.Create();
        using var context = Context(driver, model =>
        {
            if (autoInclude) model.Entity<Post>().Navigation(p => p.Blog).AutoInclude();
            else model.Entity<Post>().HasQueryFilter(p => p.Title != "hidden");
        });
        Assert.Throws<Neo4jQueryTranslationException>(() => context.Set<Blog>().Include(b => b.Posts).ToQueryString());
        using var rootFiltered = Context(driver, model => model.Entity<Blog>().HasQueryFilter(b => b.Name != "hidden"));
        var text = rootFiltered.Set<Blog>().Include(b => b.Posts).ToQueryString();
        Assert.Contains("\nWHERE", text);
        Assert.Contains("n.`Name`", text);
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    private static ProviderModelContext Context(IDriver driver, Action<ModelBuilder>? extra = null) => new(driver, model =>
    {
        model.Entity<Blog>().ToGraphNode("IncludeBlog");
        model.Entity<Post>().ToGraphNode("IncludePost").HasOne(p => p.Blog).WithMany(b => b.Posts)
            .HasForeignKey(p => p.BlogId).IsRequired().OnDelete(DeleteBehavior.Restrict).ToGraphReference("INCLUDE_HAS_POST");
        extra?.Invoke(model);
    });

    private static GraphReadBinding Node<T>(DbContext context)
        => GraphReadBinding.FromNode(context.GetService<GraphBindingCompiler>().GetNode(context.Model.FindEntityType(typeof(T))!));
    private static GraphReadBinding Reference<T>(DbContext context)
        => GraphReadBinding.FromNodeReference(Assert.Single(context.GetService<GraphBindingCompiler>().GetNodeReferences(context.Model.FindEntityType(typeof(T))!)));
    private static string Alias(GraphReadBinding binding, string property)
        => binding.Properties.Single(p => p.ModelProperty.Property.Name == property).Column.Alias;

    private static Dictionary<string, object> Row(GraphReadBinding read, Guid id)
    {
        var row = read.Properties.ToDictionary(p => p.Column.Alias, p => p.ModelProperty.Property.Name switch
        {
            "Id" => (object)id.ToString("D"), "BlogId" => BlogId.ToString("D"), "Name" => "blog", "Title" => "post",
            _ => throw new InvalidOperationException("Unexpected test slot")
        });
        row[CypherIncludePlan.IncludedLabelAlias] = true;
        if (read.Root is CypherNodeReferenceRoot)
        {
            row[CypherQueryPlan.NodeReferenceCountAlias] = 1L;
            row[CypherQueryPlan.NodeReferencePrincipalAlias] = true;
        }
        return row;
    }

    private static Dictionary<string, object> CollectionRow(GraphReadBinding root, GraphReadBinding child, Guid blogId, params Guid[] posts)
    {
        var row = Row(root, blogId);
        row[CypherIncludePlan.RowsAlias] = posts.Select(id => (object)Row(child, id)).ToList();
        return row;
    }

    private static Dictionary<string, object> ReferenceRow(GraphReadBinding root, GraphReadBinding parent, Guid postId)
    {
        var row = Row(root, postId);
        row[CypherIncludePlan.RowsAlias] = new List<object> { Row(parent, BlogId) };
        return row;
    }

    private sealed class Script
    {
        public IDriver Driver { get; }
        public List<IReadOnlyDictionary<string, object>> Rows { get; } = [];
        public string? MissingConstraint { get; init; }
        public Action? OnDataFetch { get; set; }
        public int DataCommands { get; private set; }
        public int SchemaCommands { get; private set; }
        public int DataConsumes { get; private set; }
        public int SessionDisposals { get; private set; }
        public int TransactionRuns { get; private set; }
        public int TransactionDisposals { get; private set; }
        public int TransactionCommits { get; private set; }
        public int TransactionRollbacks { get; private set; }

        public Script()
        {
            var transaction = NativeQueryTests.Proxy.For<IAsyncTransaction>((method, args) =>
            {
                switch (method.Name)
                {
                    case "RunAsync": TransactionRuns++; return Run((string)args![0]!);
                    case "CommitAsync": TransactionCommits++; return Complete(method);
                    case "RollbackAsync": TransactionRollbacks++; return Complete(method);
                    case "DisposeAsync": TransactionDisposals++; return Complete(method);
                    default: throw new NotSupportedException(method.Name);
                }
            });
            var session = NativeQueryTests.Proxy.For<IAsyncSession>((method, args) =>
            {
                switch (method.Name)
                {
                    case "RunAsync": return Run((string)args![0]!);
                    case "BeginTransactionAsync": return Task.FromResult(transaction);
                    case "DisposeAsync": SessionDisposals++; return Complete(method);
                    default: throw new NotSupportedException(method.Name);
                }
            });
            Driver = NativeQueryTests.Proxy.For<IDriver>((method, _) => method.Name == "AsyncSession"
                ? session : throw new NotSupportedException(method.Name));
        }

        private Task<IResultCursor> Run(string command)
        {
            var schema = command.StartsWith("SHOW CONSTRAINTS", StringComparison.Ordinal);
            IReadOnlyList<IReadOnlyDictionary<string, object>> values;
            if (schema)
            {
                SchemaCommands++;
                values = new[] { "IncludeBlog", "IncludePost" }.Where(label => label != MissingConstraint)
                    .Select(label => (IReadOnlyDictionary<string, object>)new Dictionary<string, object>
                    {
                        ["type"] = "NODE_PROPERTY_UNIQUENESS", ["entityType"] = "NODE",
                        ["labelsOrTypes"] = new List<string> { label }, ["properties"] = new List<string> { "Id" }
                    }).ToArray();
            }
            else { DataCommands++; values = Rows; }
            var records = values.Select(Record).ToArray();
            var index = -1;
            var open = true;
            return Task.FromResult(NativeQueryTests.Proxy.For<IResultCursor>((method, args) =>
            {
                switch (method.Name)
                {
                    case "GetAsyncEnumerator": return new RecordEnumerator(records, (CancellationToken)args![0]!);
                    case "FetchAsync": if (!schema) OnDataFetch?.Invoke(); return Task.FromResult(++index < records.Length);
                    case "get_Current": return records[index];
                    case "get_IsOpen": return open;
                    case "ConsumeAsync": open = false; if (!schema) DataConsumes++; return Task.FromResult<IResultSummary>(null!);
                    default: throw new NotSupportedException(method.Name);
                }
            }));
        }

        private static IRecord Record(IReadOnlyDictionary<string, object> row)
            => NativeQueryTests.Proxy.For<IRecord>((method, args) => method.Name switch
            {
                "get_Values" => row, "get_Keys" => row.Keys.ToList(),
                "get_Item" => args![0] is string key ? row[key] : row.Values.ElementAt((int)args[0]!),
                _ => throw new NotSupportedException(method.Name)
            });
        private static object Complete(MethodInfo method)
            => method.ReturnType == typeof(ValueTask) ? ValueTask.CompletedTask : Task.CompletedTask;

        private sealed class RecordEnumerator(IReadOnlyList<IRecord> rows, CancellationToken cancellationToken) : IAsyncEnumerator<IRecord>
        {
            private int _index = -1;
            public IRecord Current => rows[_index];
            public ValueTask<bool> MoveNextAsync()
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(++_index < rows.Count);
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    public sealed class Blog
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public List<Post> Posts { get; set; } = null!;
    }
    public sealed class Post
    {
        public Guid Id { get; set; }
        public Guid BlogId { get; set; }
        public string Title { get; set; } = "";
        public Blog Blog { get; set; } = null!;
    }
}
