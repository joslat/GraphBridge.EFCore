using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.Samples.Blogging;

public sealed class Blog
{
    public Guid Id { get; set; }
    public string Url { get; set; } = "";
    public long Version { get; set; }
    public List<Post> Posts { get; set; } = [];
}

public sealed class Post
{
    public Guid Id { get; set; }
    public Guid BlogId { get; set; }
    public string Title { get; set; } = "";
    public string? Content { get; set; }
    public long Version { get; set; }
    public Blog Blog { get; set; } = null!;
}

public sealed class BloggingContext(IDriver driver, string database)
    : DbContext(new DbContextOptionsBuilder<BloggingContext>().UseNeo4j(driver, database).Options)
{
    public DbSet<Blog> Blogs => Set<Blog>();
    public DbSet<Post> Posts => Set<Post>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Blog>(blog =>
        {
            blog.ToGraphNode("GraphBridgeBlog");
            blog.HasKey(p => p.Id);
            blog.Property(p => p.Url).HasGraphPropertyName("url");
            blog.Property(p => p.Version).IsRowVersion();
        });
        model.Entity<Post>(post =>
        {
            post.ToGraphNode("GraphBridgePost");
            post.HasKey(p => p.Id);
            post.Property(p => p.Title).HasGraphPropertyName("title");
            post.Property(p => p.Content).HasGraphPropertyName("content");
            post.Property(p => p.Version).IsRowVersion();
            post.HasOne(p => p.Blog).WithMany(b => b.Posts).HasForeignKey(p => p.BlogId)
                .IsRequired().OnDelete(DeleteBehavior.Restrict).ToGraphReference("HAS_POST");
        });
    }
}
