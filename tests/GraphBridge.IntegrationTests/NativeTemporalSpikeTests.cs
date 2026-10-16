using Neo4j.Driver;
using Xunit.Abstractions;

namespace GraphBridge.IntegrationTests;

[Collection(Neo4jIntegrationCollection.Name)]
public sealed class NativeTemporalSpikeTests(DisposableNeo4jFixture fixture, ITestOutputHelper output)
{
    [Neo4jIntegrationFact]
    public async Task Native_UTC_normalization_preserves_ticks_instants_nulls_and_boundaries()
    {
        var instant = DateTimeOffset.UnixEpoch.AddTicks(-1);
        var values = new DateTimeOffset?[]
        {
            null, DateTimeOffset.MinValue, DateTimeOffset.MaxValue,
            instant, instant.ToOffset(TimeSpan.FromHours(14)), instant.ToOffset(TimeSpan.FromHours(-14)),
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddTicks(1),
            new DateTimeOffset(2026, 10, 12, 12, 30, 45, TimeSpan.FromMinutes(345)).AddTicks(1234567)
        };
        const string normalize = "CASE WHEN $value IS NULL THEN null ELSE datetime({datetime: $value, timezone: '+00:00'}) END";
        await using var session = fixture.OpenSession();
        foreach (var value in values)
        {
            var row = await (await session.RunAsync($"RETURN $value AS raw, {normalize} AS normalized",
                new { value = value is { } dto ? new ZonedDateTime(dto) : null })).SingleAsync();
            if (value is null)
            {
                Assert.Null(row["raw"]);
                Assert.Null(row["normalized"]);
            }
            else
            {
                Assert.True(value.Value.EqualsExact(row["raw"].As<ZonedDateTime>().ToDateTimeOffset()));
                Assert.True(value.Value.ToUniversalTime().EqualsExact(row["normalized"].As<ZonedDateTime>().ToDateTimeOffset()));
            }
        }
        var compare = await (await session.RunAsync("""
            RETURN $a = $b AS nativeEqual,
              datetime({datetime: $a, timezone: '+00:00'}) = datetime({datetime: $b, timezone: '+00:00'}) AS instantEqual,
              datetime({datetime: $a, timezone: '+00:00'}) < datetime({datetime: $c, timezone: '+00:00'}) AS tickOrder,
              datetime({datetime: datetime('2026-10-12T12:00:00.0000001[Europe/Madrid]'), timezone: '+00:00'}) AS namedZone
            """, new { a = new ZonedDateTime(instant), b = new ZonedDateTime(instant.ToOffset(TimeSpan.FromHours(2))), c = new ZonedDateTime(instant.AddTicks(1)) })).SingleAsync();
        Assert.False(compare["nativeEqual"].As<bool>());
        Assert.True(compare["instantEqual"].As<bool>());
        Assert.True(compare["tickOrder"].As<bool>());
        Assert.True(new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero).AddTicks(1)
            .EqualsExact(compare["namedZone"].As<ZonedDateTime>().ToDateTimeOffset()));
        foreach (var text in new[] { "2026-10-12T00:00:00.000000001Z", "2026-10-12T00:00:00+18:00", "2026-10-12T00:00:00+00:00:01", "+10000-01-01T00:00:00Z" })
        {
            var native = (await (await session.RunAsync("RETURN datetime($text) AS value", new { text })).SingleAsync())["value"].As<ZonedDateTime>();
            Assert.ThrowsAny<Exception>(() => native.ToDateTimeOffset());
        }
        output.WriteLine($"PASS {fixture.ServerVersion}: nine null/offset/tick/boundary round trips; offset-sensitive native equality versus instant equality; 100ns ordering; named-zone normalization; four unrepresentable native values reject. RETURN-only; no schema/data writes.");
    }
}
