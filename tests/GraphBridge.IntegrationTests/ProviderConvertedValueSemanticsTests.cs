using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Neo4j.Driver;
using Xunit.Abstractions;

namespace GraphBridge.IntegrationTests;

[Collection(Neo4jIntegrationCollection.Name)]
public sealed class ProviderConvertedValueSemanticsTests(DisposableNeo4jFixture fixture, ITestOutputHelper output)
{
    [Neo4jIntegrationFact]
    public async Task Nullable_time_Guid_enum_parameter_values_and_negation_match_the_explicit_model_contract()
    {
        var runId = Guid.NewGuid().ToString("N");
        var timestamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var instant = new DateTimeOffset(timestamp);
        var optionalKey = Guid.NewGuid();
        var rows = new[]
        {
            new ConvertedRow
            {
                Id = Guid.NewGuid(), RunId = runId, Timestamp = timestamp, Instant = instant,
                Date = new DateOnly(2026, 1, 2), Time = new TimeOnly(3, 4, 5).Add(TimeSpan.FromTicks(7)),
                Duration = TimeSpan.FromTicks(-12345), OptionalKey = optionalKey, State = ConvertedState.Large
            },
            new ConvertedRow
            {
                Id = Guid.NewGuid(), RunId = runId, Timestamp = DateTime.SpecifyKind(timestamp, DateTimeKind.Unspecified),
                Instant = instant.ToOffset(TimeSpan.FromHours(2)), Date = new DateOnly(2025, 12, 31),
                Time = TimeOnly.MinValue, Duration = TimeSpan.Zero, OptionalKey = Guid.Empty, State = ConvertedState.Negative
            },
            new ConvertedRow { Id = Guid.NewGuid(), RunId = runId }
        };
        try
        {
            await using (var session = fixture.OpenSession())
                await (await session.RunAsync("CREATE CONSTRAINT graphbridge_converted_row_key IF NOT EXISTS FOR (n:GraphBridgeConvertedRow) REQUIRE n.Id IS UNIQUE")).ConsumeAsync();
            await using (var seed = Context())
            {
                seed.AddRange(rows);
                Assert.Equal(3, await seed.SaveChangesAsync());
            }
            await using var context = Context();
            var root = context.Rows.AsNoTracking().Where(r => r.RunId == runId);
            var stored = await root.ToListAsync();
            Assert.Equal(3, stored.Count);
            foreach (var expected in rows)
            {
                var actual = Assert.Single(stored, r => r.Id == expected.Id);
                Assert.Equal(expected.Timestamp, actual.Timestamp);
                Assert.Equal(expected.Instant, actual.Instant);
                Assert.Equal(expected.Date, actual.Date);
                Assert.Equal(expected.Time, actual.Time);
                Assert.Equal(expected.Duration, actual.Duration);
                Assert.Equal(expected.OptionalKey, actual.OptionalKey);
                Assert.Equal(expected.State, actual.State);
                if (actual.Timestamp is { } dateTime) Assert.Equal(DateTimeKind.Unspecified, dateTime.Kind);
                if (actual.Instant is { } offset) Assert.Equal(TimeSpan.Zero, offset.Offset);
            }

            var comparisons = 0;
            async Task CheckAsync(Expression<Func<ConvertedRow, bool>> predicate)
            {
                var expected = rows.Where(predicate.Compile()).Select(r => r.Id).OrderBy(id => id).ToArray();
                var query = root.Where(predicate);
                Assert.Equal(expected, (await query.ToListAsync()).Select(r => r.Id).OrderBy(id => id));
                Assert.DoesNotContain(runId, query.ToQueryString());
                Assert.DoesNotContain(optionalKey.ToString(), query.ToQueryString());
                comparisons++;
            }
            foreach (var row in rows)
            {
                var dateTime = row.Timestamp;
                var dateTimeOffset = row.Instant;
                var date = row.Date;
                var time = row.Time;
                var duration = row.Duration;
                var key = row.OptionalKey;
                var state = row.State;
                await CheckAsync(r => r.Timestamp == dateTime);
                await CheckAsync(r => r.Timestamp != dateTime);
                await CheckAsync(r => !(r.Timestamp == dateTime));
                await CheckAsync(r => r.Instant == dateTimeOffset);
                await CheckAsync(r => r.Instant != dateTimeOffset);
                await CheckAsync(r => !(r.Instant == dateTimeOffset));
                await CheckAsync(r => r.Date == date);
                await CheckAsync(r => r.Date != date);
                await CheckAsync(r => !(r.Date == date));
                await CheckAsync(r => r.Time == time);
                await CheckAsync(r => r.Time != time);
                await CheckAsync(r => !(r.Time == time));
                await CheckAsync(r => r.Duration == duration);
                await CheckAsync(r => r.Duration != duration);
                await CheckAsync(r => !(r.Duration == duration));
                await CheckAsync(r => r.OptionalKey == key);
                await CheckAsync(r => r.OptionalKey != key);
                await CheckAsync(r => !(r.OptionalKey == key));
                await CheckAsync(r => r.State == state);
                await CheckAsync(r => r.State != state);
                await CheckAsync(r => !(r.State == state));
            }
            Assert.Equal(63, comparisons);
            Assert.Empty(context.ChangeTracker.Entries());
            output.WriteLine("63 equality/inequality/negation checks match CLR across five explicit temporal conversions, nullable Guid and Int64 enum, including changed and null parameters.");
            output.WriteLine(root.Where(r => r.Timestamp == timestamp).ToQueryString());
        }
        finally
        {
            await using var session = fixture.OpenSession();
            await (await session.RunAsync("MATCH (n:GraphBridgeConvertedRow {RunId: $runId}) DELETE n", new { runId })).ConsumeAsync();
        }
    }

    private ConvertedContext Context() => new(new DbContextOptionsBuilder<ConvertedContext>().UseNeo4j(fixture.Driver, fixture.Database).Options);
    public enum ConvertedState : long { Negative = -1, Large = 4_294_967_296 }
    public sealed class ConvertedRow
    {
        public Guid Id { get; set; }
        public string RunId { get; set; } = "";
        public DateTime? Timestamp { get; set; }
        public DateTimeOffset? Instant { get; set; }
        public DateOnly? Date { get; set; }
        public TimeOnly? Time { get; set; }
        public TimeSpan? Duration { get; set; }
        public Guid? OptionalKey { get; set; }
        public ConvertedState? State { get; set; }
    }
    public sealed class ConvertedContext(DbContextOptions<ConvertedContext> options) : DbContext(options)
    {
        public DbSet<ConvertedRow> Rows => Set<ConvertedRow>();
        protected override void OnModelCreating(ModelBuilder builder)
        {
            var row = builder.Entity<ConvertedRow>();
            row.ToGraphNode("GraphBridgeConvertedRow");
            row.Property(r => r.Id).ValueGeneratedNever();
            // Explicit application representations: DateTime equality compares ticks, while
            // DateTimeOffset equality compares instants. Kind/offset are deliberately normalized.
            row.Property(r => r.Timestamp).HasConversion(new DateTimeToTicksConverter());
            row.Property(r => r.Instant).HasConversion(new ValueConverter<DateTimeOffset, long>(
                value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero)));
            row.Property(r => r.Date).HasConversion(new ValueConverter<DateOnly, int>(
                value => value.DayNumber, value => DateOnly.FromDayNumber(value)));
            row.Property(r => r.Time).HasConversion(new TimeOnlyToTicksConverter());
            row.Property(r => r.Duration).HasConversion(new TimeSpanToTicksConverter());
        }
    }
}
