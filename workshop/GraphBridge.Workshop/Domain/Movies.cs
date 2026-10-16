namespace GraphBridge.Workshop.Domain;

// The one workshop domain. Every lesson uses these types; later lessons explore more of their mapping.
// Plain CLR classes: no GraphBridge base types, attributes or interfaces.

/// <summary>A person node. Directs movies, acts in them through credits, likes them and follows other people.</summary>
public sealed class Person
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public int? Born { get; set; }
    public string? Nickname { get; set; }
    /// <summary>Provider-generated row version (lesson 12).</summary>
    public long Version { get; set; }
    public List<Movie> Directed { get; set; } = [];
    public List<Movie> LikedMovies { get; set; } = [];
    public List<Like> Likes { get; set; } = [];
}

/// <summary>A movie node with an optional director and any number of reviews (each review requires its movie).</summary>
public sealed class Movie
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public int Released { get; set; }
    public string? Tagline { get; set; }
    /// <summary>Derived from the incoming DIRECTED relationship; never stored on the node (lessons 08–09).</summary>
    public Guid? DirectorId { get; set; }
    public Person? Director { get; set; }
    public List<Review> Reviews { get; set; } = [];
    public List<Person> LikedBy { get; set; } = [];
    public List<Like> Likes { get; set; } = [];
}

/// <summary>A review node that always belongs to exactly one movie through HAS_REVIEW.</summary>
public sealed class Review
{
    public Guid Id { get; set; }
    public Guid MovieId { get; set; }
    public Movie? Movie { get; set; }
    public int Rating { get; set; }
    public string Text { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>An ACTED_IN relationship with its own identity, so one person can hold two roles in one movie (lesson 11).</summary>
public sealed class Credit
{
    public Guid Id { get; set; }
    public Guid PersonId { get; set; }
    public Guid MovieId { get; set; }
    public string Role { get; set; } = "";
    public int Billing { get; set; }
}

/// <summary>A LIKES relationship: a many-to-many join entity stored as one directed edge with a payload (lesson 10).</summary>
public sealed class Like
{
    public Guid PersonId { get; set; }
    public Guid MovieId { get; set; }
    public Person? Person { get; set; }
    public Movie? Movie { get; set; }
    public int Stars { get; set; }
}

/// <summary>A FOLLOWS relationship between two people, used for patterns, existence checks and paths.</summary>
public sealed class Follow
{
    public Guid FollowerId { get; set; }
    public Guid FolloweeId { get; set; }
}
