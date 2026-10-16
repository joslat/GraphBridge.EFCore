using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.Workshop.Domain;

/// <summary>The workshop's EF Core context. The application owns the Neo4j driver and lends it to EF.</summary>
public sealed class MoviesContext(IDriver driver, string database) : DbContext
{
    public DbSet<Person> People => Set<Person>();
    public DbSet<Movie> Movies => Set<Movie>();
    public DbSet<Review> Reviews => Set<Review>();
    public DbSet<Credit> Credits => Set<Credit>();
    public DbSet<Like> Likes => Set<Like>();
    public DbSet<Follow> Follows => Set<Follow>();

    // workshop:start Configure
    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseNeo4j(driver, database);
    // workshop:end Configure

    protected override void OnModelCreating(ModelBuilder model)
    {
        // workshop:start Nodes
        // Nodes: a label per entity, an application key, and camelCase property names (Neo4j's convention).
        model.Entity<Person>(person =>
        {
            person.ToGraphNode("Person");
            person.Property(p => p.Id).ValueGeneratedNever().HasGraphPropertyName("id");
            person.Property(p => p.Name).HasGraphPropertyName("name");
            person.Property(p => p.Born).HasGraphPropertyName("born");
            person.Property(p => p.Nickname).HasGraphPropertyName("nickname");
            person.Property(p => p.Version).HasGraphPropertyName("version").IsRowVersion();
        });
        model.Entity<Movie>(movie =>
        {
            movie.ToGraphNode("Movie");
            movie.Property(m => m.Id).ValueGeneratedNever().HasGraphPropertyName("id");
            movie.Property(m => m.Title).HasGraphPropertyName("title");
            movie.Property(m => m.Released).HasGraphPropertyName("released");
            movie.Property(m => m.Tagline).HasGraphPropertyName("tagline");
        });
        model.Entity<Review>(review =>
        {
            review.ToGraphNode("Review");
            review.Property(r => r.Id).ValueGeneratedNever().HasGraphPropertyName("id");
            review.Property(r => r.Rating).HasGraphPropertyName("rating");
            review.Property(r => r.Text).HasGraphPropertyName("text");
            review.Property(r => r.CreatedAt).HasGraphPropertyName("createdAt");
        });
        // workshop:end Nodes

        // workshop:start References
        // References: an ordinary EF one-to-many, stored as a relationship instead of a foreign-key property.
        model.Entity<Review>()
            .HasOne(r => r.Movie).WithMany(m => m.Reviews).HasForeignKey(r => r.MovieId)
            .OnDelete(DeleteBehavior.Restrict)
            .ToGraphReference("HAS_REVIEW");          // (:Movie)-[:HAS_REVIEW]->(:Review)
        model.Entity<Movie>()
            .HasOne(m => m.Director).WithMany(p => p.Directed).HasForeignKey(m => m.DirectorId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict)
            .ToGraphReference("DIRECTED");            // (:Person)-[:DIRECTED]->(:Movie), optional
        // workshop:end References

        // workshop:start Credits
        // Identified relationship: each ACTED_IN edge has its own key, so parallel edges are allowed.
        model.Entity<Credit>(credit =>
        {
            credit.HasOne<Person>().WithMany().HasForeignKey(c => c.PersonId);
            credit.HasOne<Movie>().WithMany().HasForeignKey(c => c.MovieId);
            credit.Property(c => c.Id).ValueGeneratedNever().HasGraphPropertyName("id");
            credit.Property(c => c.Role).HasGraphPropertyName("role");
            credit.Property(c => c.Billing).HasGraphPropertyName("billing");
            credit.ToGraphIdentifiedRelationship("ACTED_IN", nameof(Credit.Id), [nameof(Credit.PersonId)], [nameof(Credit.MovieId)]);
        });
        // workshop:end Credits

        // workshop:start Associations
        // Association: an EF many-to-many join entity stored as one directed LIKES edge with a payload.
        model.Entity<Person>()
            .HasMany(p => p.LikedMovies).WithMany(m => m.LikedBy)
            .UsingEntity<Like>(
                right => right.HasOne(l => l.Movie).WithMany(m => m.Likes).HasForeignKey(l => l.MovieId),
                left => left.HasOne(l => l.Person).WithMany(p => p.Likes).HasForeignKey(l => l.PersonId),
                join =>
                {
                    join.HasKey(l => new { l.PersonId, l.MovieId });
                    join.Property(l => l.Stars).HasGraphPropertyName("stars");
                    join.ToGraphRelationship("LIKES", [nameof(Like.PersonId)], [nameof(Like.MovieId)]);
                });
        // An association between two people, with no navigations: (:Person)-[:FOLLOWS]->(:Person).
        model.Entity<Follow>(follows =>
        {
            follows.HasKey(f => new { f.FollowerId, f.FolloweeId });
            follows.HasOne<Person>().WithMany().HasForeignKey(f => f.FollowerId);
            follows.HasOne<Person>().WithMany().HasForeignKey(f => f.FolloweeId);
            follows.ToGraphRelationship("FOLLOWS", [nameof(Follow.FollowerId)], [nameof(Follow.FolloweeId)]);
        });
        // workshop:end Associations
    }
}
