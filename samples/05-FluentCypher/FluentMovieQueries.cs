using GraphBridge.EntityFrameworkCore.FluentCypher;
using GraphBridge.EntityFrameworkCore.Paths;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;

namespace GraphBridge.Samples.FluentCypher;

/// <summary>Compiled sample queries; the provider's disposable live suite executes these exact methods.</summary>
public static class FluentMovieQueries
{
    // query-lab:start Coactors
    public static CypherResultQuery<string> Coactors(DbContext db, string runId, string actorName)
    {
        var actor = Graph.Node<Person>();
        var coactor = Graph.Node<Person>();
        var movie = Graph.Node<Movie>();
        return db.Cypher()
            .Match(actor.Out<Credit>(movie).In<Credit>(coactor))
            .Where(() => actor.Value.RunId == runId
                && actor.Value.Name == actorName
                && coactor.Value.Id != actor.Value.Id)
            .ReturnDistinct(() => coactor.Value.Name)
            .OrderBy(() => coactor.Value.Name)
            .Limit(100);
    }
    // query-lab:end Coactors

    // query-lab:start SharedMovies
    public static CypherResultQuery<ScoreRow> SharedMovies(DbContext db, string runId, string actorName)
    {
        var actor = Graph.Node<Person>();
        var coactor = Graph.Node<Person>();
        var movie = Graph.Node<Movie>();
        var count = Graph.CountDistinct(movie);
        var rows = Graph.CountRows();
        return db.Cypher()
            .Match(actor.Out<Credit>(movie).In<Credit>(coactor))
            .Where(() => actor.Value.RunId == runId
                && actor.Value.Name == actorName
                && actor.Value.Id != coactor.Value.Id)
            .With(coactor, count, rows)
            .Where(() => count.Value >= 1L)
            .Return(() => new ScoreRow(coactor.Value.Name, count.Value, rows.Value))
            .OrderByDescending(() => count.Value)
            .ThenBy(() => coactor.Value.Name)
            .Limit(100);
    }
    // query-lab:end SharedMovies

    // query-lab:start OptionalMovies
    /// <summary>WHERE stays attached to OPTIONAL MATCH, preserving people without a qualifying movie.</summary>
    public static CypherResultQuery<OptionalMovieRow> OptionalMovies(DbContext db, string runId, int afterYear)
    {
        var actor = Graph.Node<Person>();
        var movie = Graph.Node<Movie>();
        return db.Cypher()
            .Match(actor)
            .Where(() => actor.Value.RunId == runId)
            .OptionalMatch(actor.Out<Credit>(movie), where: () => movie.Value.Year > afterYear)
            .Return(() => new OptionalMovieRow(actor.Value.Name, movie.Value.Title, (int?)movie.Value.Year))
            .OrderBy(() => actor.Value.Name)
            .Limit(100);
    }
    // query-lab:end OptionalMovies

    /// <summary>The same movie predicate after WITH removes rows whose optional movie is absent.</summary>
    public static CypherResultQuery<OptionalMovieRow> FilteredOptionalMovies(DbContext db, string runId, int afterYear)
    {
        var actor = Graph.Node<Person>();
        var movie = Graph.Node<Movie>();
        return db.Cypher()
            .Match(actor)
            .Where(() => actor.Value.RunId == runId)
            .OptionalMatch(actor.Out<Credit>(movie))
            .With(actor, movie)
            .Where(() => movie.Value.Year > afterYear)
            .Return(() => new OptionalMovieRow(actor.Value.Name, movie.Value.Title, (int?)movie.Value.Year))
            .OrderBy(() => actor.Value.Name)
            .Limit(100);
    }

    /// <summary>Nested EXISTS binds local aliases without exporting them to the outer query.</summary>
    /// <remarks>EXISTS takes a lambda for its inner condition. <c>Graph.Is</c> places the EXISTS predicate inside the outer lambda;
    /// it is translated, never called.</remarks>
    public static CypherResultQuery<string> FriendsReaching(DbContext db, string runId, string targetName)
    {
        var actor = Graph.Node<Person>();
        var friend = Graph.Node<Person>();
        var candidate = Graph.Node<Person>();
        var inner = Graph.Exists(friend.Out<Friendship>(candidate), () => candidate.Value.Name == targetName);
        var outer = Graph.Exists(actor.Out<Friendship>(friend), inner);
        return db.Cypher()
            .Match(actor)
            .Where(() => actor.Value.RunId == runId && Graph.Is(outer))
            .Return(() => actor.Value.Name)
            .OrderBy(() => actor.Value.Name)
            .Limit(100);
    }

    /// <summary>Lists and text: <c>Contains</c> on a captured array becomes IN with one list parameter, and ordinal
    /// <c>StartsWith</c> becomes STARTS WITH. Both are accepted because names and titles are required properties.</summary>
    public static CypherResultQuery<string> CastSearch(DbContext db, string runId, string[] actorNames, string titlePrefix)
    {
        var actor = Graph.Node<Person>();
        var movie = Graph.Node<Movie>();
        return db.Cypher()
            .Match(actor.Out<Credit>(movie))
            .Where(() => actor.Value.RunId == runId
                && actorNames.Contains(actor.Value.Name)
                && movie.Value.Title.StartsWith(titlePrefix, StringComparison.Ordinal))
            .ReturnDistinct(() => actor.Value.Name)
            .OrderBy(() => actor.Value.Name)
            .Limit(100);
    }

    /// <summary>Native aggregates returned directly: Cypher groups by the returned name. People without credits keep their
    /// row with null years and an empty title list, because the movie belongs to OPTIONAL MATCH.</summary>
    public static CypherResultQuery<CareerRow> Careers(DbContext db, string runId)
    {
        var actor = Graph.Node<Person>();
        var movie = Graph.Node<Movie>();
        var first = Graph.Min(movie.Property(m => m.Year));
        var last = Graph.Max(movie.Property(m => m.Year));
        var titles = Graph.CollectDistinct(movie.Property(m => m.Title), max: 10);
        return db.Cypher()
            .Match(actor)
            .Where(() => actor.Value.RunId == runId)
            .OptionalMatch(actor.Out<Credit>(movie))
            .Return(() => new CareerRow(actor.Value.Name, (int?)first.Value, (int?)last.Value, titles.Value))
            .OrderBy(() => actor.Value.Name)
            .Limit(100);
    }

    /// <summary>Finite homogeneous paths. The row/depth limits do not bound all server computation.</summary>
    public static CypherResultQuery<GraphPath<INode, IRelationship>> FriendPaths(DbContext db, string runId, string actorName,
        GraphTraversalDirection direction = GraphTraversalDirection.Outgoing)
    {
        var start = Graph.Node<Person>();
        var end = Graph.Node<Person>();
        var edge = Graph.Edge<Friendship>();
        var path = Graph.Path();
        var pattern = direction switch
        {
            GraphTraversalDirection.Outgoing => start.Out(edge, end),
            GraphTraversalDirection.Incoming => start.In(edge, end),
            GraphTraversalDirection.Both => start.Undirected(edge, end),
            _ => throw new ArgumentOutOfRangeException(nameof(direction))
        };
        return db.Cypher()
            .Match(path, pattern.Repeat(1, 3))
            .Where(() => start.Value.RunId == runId && start.Value.Name == actorName)
            .Return(() => path.Value)
            .Limit(500);
    }

    public static void Configure(ModelBuilder b)
    {
        b.Entity<Person>().ToGraphNode("FGQPerson"); b.Entity<Movie>().ToGraphNode("FGQMovie");
        b.Entity<Credit>().HasOne<Person>().WithMany().HasForeignKey(e => e.PersonId);
        b.Entity<Credit>().HasOne<Movie>().WithMany().HasForeignKey(e => e.MovieId);
        b.Entity<Credit>().ToGraphIdentifiedRelationship("FGQ_ACTED", nameof(Credit.Id), [nameof(Credit.PersonId)], [nameof(Credit.MovieId)]);
        b.Entity<Friendship>().HasKey(e => new { e.SourceId, e.TargetId });
        b.Entity<Friendship>().HasOne<Person>().WithMany().HasForeignKey(e => e.SourceId);
        b.Entity<Friendship>().HasOne<Person>().WithMany().HasForeignKey(e => e.TargetId);
        b.Entity<Friendship>().ToGraphRelationship("FGQ_FRIEND", [nameof(Friendship.SourceId)], [nameof(Friendship.TargetId)]);
    }
}

public sealed record ScoreRow(string Name, long Score, long Rows);
public sealed record OptionalMovieRow(string Name, string? Title, int? Year);
/// <summary>Collected titles are unordered; at most ten per person.</summary>
public sealed record CareerRow(string Name, int? FirstYear, int? LastYear, IReadOnlyList<string> Titles);
public sealed class Person { public Guid Id { get; set; } public string RunId { get; set; } = ""; public string Name { get; set; } = ""; public string? Nickname { get; set; } }
public sealed class Movie { public Guid Id { get; set; } public string RunId { get; set; } = ""; public string Title { get; set; } = ""; public int Year { get; set; } }
public sealed class Credit { public Guid Id { get; set; } public Guid PersonId { get; set; } public Guid MovieId { get; set; } public string Role { get; set; } = ""; public int Billing { get; set; } }
public sealed class Friendship { public Guid SourceId { get; set; } public Guid TargetId { get; set; } }
