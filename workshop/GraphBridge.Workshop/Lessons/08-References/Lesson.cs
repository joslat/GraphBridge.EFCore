using GraphBridge.EntityFrameworkCore.Diagnostics;
using GraphBridge.Workshop.Domain;
using Microsoft.EntityFrameworkCore;

namespace GraphBridge.Workshop.Lessons;

public sealed class Lesson08References : ILesson
{
    public LessonInfo Info { get; } = new("08", 'B', "One-to-many references and Include", "08-References", LessonMode.Connected, Core: true,
        Requirement.Seed, "Store an ordinary EF one-to-many as a native relationship and load it with Include.");

    public async Task RunAsync(LessonContext lesson, CancellationToken cancellationToken)
    {
        lesson.Step("Map Review.MovieId as a relationship, not a property");
        lesson.Code("References", "Domain/MoviesContext.cs");

        lesson.Step("Load a movie with its reviews");
        await using (var db = lesson.CreateContext())
        {
            lesson.Code("Include");
            // workshop:start Include
            var query = db.Movies
                .Where(m => m.Title == "The Matrix")
                .Include(m => m.Reviews);
            var matrix = await query.SingleAsync(cancellationToken);
            // workshop:end Include
            lesson.Cypher("movie-with-reviews", query.ToQueryString());
            lesson.Table(matrix.Reviews.OrderByDescending(r => r.Rating).Select(r => new { r.Rating, r.Text }));
            lesson.Expect("The Matrix has two reviews", 2, matrix.Reviews.Count);
            lesson.Check("the collection is marked loaded", db.Entry(matrix).Collection(m => m.Reviews).IsLoaded);
            lesson.Check("each review points back to the same tracked movie", matrix.Reviews.All(r => ReferenceEquals(r.Movie, matrix) && r.MovieId == matrix.Id));
        }

        lesson.Step("The other direction: Include(r => r.Movie) meets a documented limit");
        await using (var db = lesson.CreateContext())
        {
            lesson.Code("Reverse");
            // workshop:start Reverse
            lesson.ExpectFailure<Neo4jQueryTranslationException>("Include of a target that has its own native parent is refused",
                () => db.Reviews.Include(r => r.Movie).ToQueryString());
            // workshop:end Reverse
            lesson.Note("Movie is both a parent (of reviews) and a child (of its optional director). Including it from a review would need a second membership check, so the provider refuses instead of guessing.");

            lesson.Step("The supported alternative: load both sides; EF connects them");
            lesson.Code("Fixup");
            // workshop:start Fixup
            var reviews = await db.Reviews.Where(r => r.Rating >= 4).OrderBy(r => r.Text).ToListAsync(cancellationToken);
            Guid[] movieIds = [.. reviews.Select(r => r.MovieId).Distinct()];
            await db.Movies.Where(m => movieIds.Contains(m.Id)).ToListAsync(cancellationToken);   // EF fixes up review.Movie
            // workshop:end Fixup
            lesson.Table(reviews.Select(r => new { r.Rating, Movie = r.Movie?.Title }));
            lesson.Expect("three reviews rated 4 or 5", 3, reviews.Count);
            var matrixReviews = reviews.Where(r => r.MovieId == SeedData.Matrix).ToList();
            lesson.Check("both Matrix reviews now share one tracked Movie instance",
                matrixReviews.Count == 2 && matrixReviews[0].Movie is not null && ReferenceEquals(matrixReviews[0].Movie, matrixReviews[1].Movie));
        }

        lesson.Step("Add a review through the navigation, then remove it");
        var reviewId = Guid.NewGuid();
        await using (var db = lesson.CreateContext())
        {
            lesson.Code("Add");
            // workshop:start Add
            var movie = await db.Movies.Where(m => m.Id == SeedData.JohnWick).Include(m => m.Reviews).SingleAsync(cancellationToken);
            movie.Reviews.Add(new Review { Id = reviewId, Rating = 5, Text = "Workshop review", CreatedAt = DateTimeOffset.UtcNow });
            var saved = await db.SaveChangesAsync(cancellationToken);   // one node + one HAS_REVIEW relationship
            // workshop:end Add
            lesson.Expect("one EF entry saved (the link is not an entry)", 1, saved);
        }
        lesson.Expect("the database holds the new HAS_REVIEW relationship", 1L,
            await Count(lesson, "MATCH (:Movie {id: $movie})-[:HAS_REVIEW]->(:Review {id: $review}) RETURN count(*) AS value", reviewId, cancellationToken));
        lesson.Expect("no review node stores a movie key", 0L,
            await Count(lesson, "MATCH (r:Review) WHERE r.movieId IS NOT NULL OR r.MovieId IS NOT NULL RETURN count(r) AS value", reviewId, cancellationToken));
        await using (var db = lesson.CreateContext())
        {
            db.Reviews.Remove(await db.Reviews.SingleAsync(r => r.Id == reviewId, cancellationToken));
            await db.SaveChangesAsync(cancellationToken);
        }
        lesson.Expect("removing the review also removed its link", 0L,
            await Count(lesson, "MATCH (:Movie {id: $movie})-[:HAS_REVIEW]->(:Review {id: $review}) RETURN count(*) AS value", reviewId, cancellationToken));
        lesson.Limit("One direct Include per query. Filtered, nested and string Includes are not supported, and a review cannot be moved to another movie.");
    }

    private static Task<long> Count(LessonContext lesson, string cypher, Guid review, CancellationToken cancellationToken)
        => WorkshopData.ScalarAsync(lesson.Connection!, cypher, cancellationToken,
            ("movie", SeedData.JohnWick.ToString("D")), ("review", review.ToString("D")));
}
