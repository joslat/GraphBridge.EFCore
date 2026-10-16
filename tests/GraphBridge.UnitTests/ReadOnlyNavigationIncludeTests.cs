using Microsoft.EntityFrameworkCore;

namespace GraphBridge.UnitTests;

public sealed class ReadOnlyNavigationIncludeTests
{
    [Theory]
    [InlineData(true, QueryTrackingBehavior.TrackAll)]
    [InlineData(false, QueryTrackingBehavior.TrackAll)]
    [InlineData(true, QueryTrackingBehavior.NoTracking)]
    [InlineData(false, QueryTrackingBehavior.NoTracking)]
    [InlineData(true, QueryTrackingBehavior.NoTrackingWithIdentityResolution)]
    [InlineData(false, QueryTrackingBehavior.NoTrackingWithIdentityResolution)]
    public void Direct_include_accepts_EF_reference_navigation_backed_by_readonly_field(
        bool collection, QueryTrackingBehavior tracking)
    {
        var driver = ProviderTestDriver.Create();
        using var context = new ProviderModelContext(driver, model =>
        {
            model.Entity<Blog>().ToGraphNode("ReadOnlyIncludeBlog");
            model.Entity<Post>().ToGraphNode("ReadOnlyIncludePost")
                .HasOne(post => post.Blog).WithMany(blog => blog.Posts).HasForeignKey(post => post.BlogId)
                .OnDelete(DeleteBehavior.Restrict).ToGraphReference("READONLY_INCLUDE_HAS_POST");
            model.Entity<Post>().Navigation(post => post.Blog)
                .HasField("_blog").UsePropertyAccessMode(PropertyAccessMode.Field);
        });
        context.ChangeTracker.QueryTrackingBehavior = tracking;
        var navigation = context.Model.FindEntityType(typeof(Post))!.FindNavigation(nameof(Post.Blog))!;
        Assert.True(navigation.FieldInfo!.IsInitOnly);
        Assert.Equal(PropertyAccessMode.Field, navigation.GetPropertyAccessMode());

        var text = collection
            ? context.Set<Blog>().Include(blog => blog.Posts).ToQueryString()
            : context.Set<Post>().Include(post => post.Blog).ToQueryString();

        Assert.Contains("READONLY_INCLUDE_HAS_POST", text);
        Assert.Contains("__gb_include_rows", text);
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    public sealed class Blog
    {
        public Guid Id { get; set; }
        public List<Post> Posts { get; } = [];
    }

    public sealed class Post
    {
        private readonly Blog _blog = null!;
        public Guid Id { get; set; }
        public Guid BlogId { get; set; }
        public Blog Blog => _blog;
    }
}
