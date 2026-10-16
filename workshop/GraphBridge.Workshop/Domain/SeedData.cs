namespace GraphBridge.Workshop.Domain;

/// <summary>A small deterministic movie graph with the edge cases the lessons need.</summary>
/// <remarks>Fixed keys make every run and every lesson reproducible. Edge cases on purpose: Hugo Weaving and Tom Hanks
/// each play two roles in Cloud Atlas (parallel ACTED_IN edges); John Wick has no director; Emil Eifrem acts in nothing;
/// Keanu → Carrie-Anne → Laurence → Keanu is a FOLLOWS cycle; reviews use different UTC offsets.</remarks>
public static class SeedData
{
    public const int Version = 1;

    public static readonly Guid Keanu = Id(1, 1), CarrieAnne = Id(1, 2), Laurence = Id(1, 3), Hugo = Id(1, 4),
        TomHanks = Id(1, 5), Lana = Id(1, 6), Lilly = Id(1, 7), TomTykwer = Id(1, 8), Emil = Id(1, 9);
    public static readonly Guid Matrix = Id(2, 1), Reloaded = Id(2, 2), Revolutions = Id(2, 3), CloudAtlas = Id(2, 4),
        JohnWick = Id(2, 5);

    public static IReadOnlyList<Person> People() =>
    [
        new() { Id = Keanu, Name = "Keanu Reeves", Born = 1964 },
        new() { Id = CarrieAnne, Name = "Carrie-Anne Moss", Born = 1967 },
        new() { Id = Laurence, Name = "Laurence Fishburne", Born = 1961, Nickname = "Larry" },
        new() { Id = Hugo, Name = "Hugo Weaving", Born = 1960 },
        new() { Id = TomHanks, Name = "Tom Hanks", Born = 1956 },
        new() { Id = Lana, Name = "Lana Wachowski", Born = 1965 },
        new() { Id = Lilly, Name = "Lilly Wachowski", Born = 1967 },
        new() { Id = TomTykwer, Name = "Tom Tykwer", Born = 1965 },
        new() { Id = Emil, Name = "Emil Eifrem", Born = 1978, Nickname = "Emil" }
    ];

    public static IReadOnlyList<Movie> Movies() =>
    [
        new() { Id = Matrix, Title = "The Matrix", Released = 1999, Tagline = "Welcome to the Real World", DirectorId = Lana },
        new() { Id = Reloaded, Title = "The Matrix Reloaded", Released = 2003, Tagline = "Free your mind", DirectorId = Lilly },
        new() { Id = Revolutions, Title = "The Matrix Revolutions", Released = 2003, Tagline = "Everything that has a beginning has an end", DirectorId = Lana },
        new() { Id = CloudAtlas, Title = "Cloud Atlas", Released = 2012, Tagline = "Everything is connected", DirectorId = TomTykwer },
        new() { Id = JohnWick, Title = "John Wick", Released = 2014 }
    ];

    public static IReadOnlyList<Review> Reviews() =>
    [
        new() { Id = Id(3, 1), MovieId = Matrix, Rating = 5, Text = "Changed how I think about action films.", CreatedAt = new(2021, 3, 4, 18, 0, 0, TimeSpan.Zero) },
        new() { Id = Id(3, 2), MovieId = Matrix, Rating = 4, Text = "Still holds up.", CreatedAt = new(2023, 7, 1, 20, 30, 0, TimeSpan.FromHours(2)) },
        new() { Id = Id(3, 3), MovieId = Reloaded, Rating = 3, Text = "Great freeway chase, heavy middle.", CreatedAt = new(2022, 1, 15, 9, 0, 0, TimeSpan.FromHours(-5)) },
        new() { Id = Id(3, 4), MovieId = CloudAtlas, Rating = 4, Text = "Six stories, one idea.", CreatedAt = new(2024, 11, 2, 12, 0, 0, TimeSpan.Zero) }
    ];

    public static IReadOnlyList<Credit> Credits() =>
    [
        Credit(1, Keanu, Matrix, "Neo", 1), Credit(2, Keanu, Reloaded, "Neo", 1), Credit(3, Keanu, Revolutions, "Neo", 1),
        Credit(4, Keanu, JohnWick, "John Wick", 1),
        Credit(5, CarrieAnne, Matrix, "Trinity", 2), Credit(6, CarrieAnne, Reloaded, "Trinity", 2),
        Credit(7, Laurence, Matrix, "Morpheus", 3), Credit(8, Laurence, Reloaded, "Morpheus", 3),
        Credit(9, Hugo, Matrix, "Agent Smith", 4),
        Credit(10, Hugo, CloudAtlas, "Bill Smoke", 2), Credit(11, Hugo, CloudAtlas, "Nurse Noakes", 3),
        Credit(12, TomHanks, CloudAtlas, "Zachry", 1), Credit(13, TomHanks, CloudAtlas, "Dr. Henry Goose", 4)
    ];

    public static IReadOnlyList<Like> Likes() =>
    [
        new() { PersonId = Keanu, MovieId = CloudAtlas, Stars = 4 },
        new() { PersonId = CarrieAnne, MovieId = Matrix, Stars = 5 },
        new() { PersonId = TomHanks, MovieId = Matrix, Stars = 4 },
        new() { PersonId = Laurence, MovieId = JohnWick, Stars = 3 },
        new() { PersonId = Emil, MovieId = Matrix, Stars = 5 }
    ];

    public static IReadOnlyList<Follow> Follows() =>
    [
        new() { FollowerId = Keanu, FolloweeId = CarrieAnne },
        new() { FollowerId = CarrieAnne, FolloweeId = Laurence },
        new() { FollowerId = Laurence, FolloweeId = Keanu },
        new() { FollowerId = Emil, FolloweeId = Keanu },
        new() { FollowerId = TomHanks, FolloweeId = Hugo },
        new() { FollowerId = Hugo, FolloweeId = Lana }
    ];

    private static Credit Credit(int number, Guid person, Guid movie, string role, int billing)
        => new() { Id = Id(4, number), PersonId = person, MovieId = movie, Role = role, Billing = billing };

    /// <summary>Readable fixed keys: 00000000-0000-0000-KIND-NUMBER.</summary>
    private static Guid Id(int kind, int number) => new($"00000000-0000-0000-{kind:D4}-{number:D12}");
}
