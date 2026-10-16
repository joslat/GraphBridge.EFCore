using GraphBridge.EntityFrameworkCore.Diagnostics;
using GraphBridge.EntityFrameworkCore.Metadata;
using GraphBridge.EntityFrameworkCore.Query;
using GraphBridge.EntityFrameworkCore.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Neo4j.Driver;

namespace GraphBridge.UnitTests;

public sealed class NativeTemporalTests
{
    [Fact]
    public void Native_binding_preserves_offset_and_all_ticks_and_detects_offset_only_changes()
    {
        using var db = Context();
        var binding = db.GetService<GraphBindingCompiler>().GetNode(db.Model.FindEntityType(typeof(Row))!);
        var optional = binding.Properties.Single(p => p.Name == nameof(Row.Optional));
        var required = binding.Properties.Single(p => p.Name == nameof(Row.Instant));
        foreach (var value in new[] { DateTimeOffset.MinValue, DateTimeOffset.MaxValue,
            DateTimeOffset.UnixEpoch.AddTicks(-1).ToOffset(TimeSpan.FromMinutes(345)) })
        {
            var stored = Assert.IsType<ZonedDateTime>(required.ToStore(value));
            Assert.True(value.EqualsExact(Assert.IsType<DateTimeOffset>(required.FromStore(stored))));
        }
        Assert.Null(optional.ToStore(null)); Assert.Null(optional.FromStore(null));
        Assert.Throws<InvalidOperationException>(() => required.FromStore(null));
        Assert.Throws<InvalidOperationException>(() => required.FromStore("2026-10-12"));
        Assert.ThrowsAny<Exception>(() => required.FromStore(new ZonedDateTime(0, 1, Zone.Of(0))));
        Assert.ThrowsAny<Exception>(() => required.FromStore(new ZonedDateTime(0, 100, Zone.Of(1))));
        var original = DateTimeOffset.UnixEpoch;
        var row = new Row { Id = 1, Instant = original, Optional = original };
        db.Attach(row);
        row.Instant = original.ToOffset(TimeSpan.FromHours(2));
        row.Optional = original.ToOffset(TimeSpan.FromHours(-2));
        db.ChangeTracker.DetectChanges();
        Assert.True(db.Entry(row).Property(r => r.Instant).IsModified);
        Assert.True(db.Entry(row).Property(r => r.Optional).IsModified);
        Assert.Equal(original, row.Instant); // CLR query equality still compares instants.
        var parameter = new Neo4jQueryParameter("p", "instant", null, required);
        Assert.Equal(7200, Assert.IsType<ZonedDateTime>(parameter.Resolve(new Dictionary<string, object?> { ["instant"] = row.Instant })).OffsetSeconds);
    }

    [Fact]
    public void Comparisons_normalize_but_projection_keeps_original_storage()
    {
        using var db = Context();
        var instant = DateTimeOffset.UnixEpoch.ToOffset(TimeSpan.FromHours(2));
        var query = db.Set<Row>().Where(r => r.Optional == instant || r.Instant > instant)
            .OrderBy(r => r.Optional).ThenBy(r => r.Id).Select(r => new { r.Id, r.Instant });
        var cypher = query.ToQueryString();
        Assert.Contains("datetime({datetime: n.`Optional`, timezone: '+00:00'})", cypher);
        Assert.Contains("datetime({datetime: $p", cypher);
        Assert.Contains("n.`Instant` AS", cypher);
        Assert.Contains("ORDER BY CASE WHEN n.`Optional` IS NULL", cypher);
        Assert.DoesNotContain(instant.ToString("O"), cypher);
        Assert.Contains("datetime({datetime: n.`Instant`,", db.Set<Row>().Where(r => r.Instant == r.Optional).ToQueryString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Explicit_conversion_keeps_existing_wire_and_rejection_contract(bool asString)
    {
        using var db = new ProviderModelContext(ProviderTestDriver.Create(), b =>
        {
            b.Entity<Row>().ToGraphNode("TemporalUnit");
            if (asString)
                b.Entity<Row>().Property(r => r.Instant).HasConversion(v => v.ToString("O"),
                    v => DateTimeOffset.ParseExact(v, "O", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None));
            else
                b.Entity<Row>().Property(r => r.Instant).HasConversion(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
        });
        var value = DateTimeOffset.UnixEpoch;
        var binding = db.GetService<GraphBindingCompiler>().GetNode(db.Model.FindEntityType(typeof(Row))!)
            .Properties.Single(p => p.Name == nameof(Row.Instant));
        Assert.Equal(asString ? typeof(string) : typeof(long), binding.ToStore(value)!.GetType());
        Assert.Equal(value, binding.FromStore(binding.ToStore(value)));
        Assert.DoesNotContain("datetime(", db.Set<Row>().Where(r => r.Instant == value).ToQueryString());
        Assert.Throws<Neo4jQueryTranslationException>(() => db.Set<Row>().OrderBy(r => r.Instant).ToQueryString());
        Assert.Throws<Neo4jQueryTranslationException>(() => db.Set<Row>().Where(r => r.Instant == r.Optional).ToQueryString());
    }

    [Theory]
    [InlineData("key")]
    [InlineData("alternate")]
    [InlineData("concurrency")]
    public void Native_temporal_identity_and_concurrency_roles_reject(string role)
    {
        using var db = new ProviderModelContext(ProviderTestDriver.Create(), b =>
        {
            var row = b.Entity<Row>().ToGraphNode("TemporalUnit");
            if (role == "key") row.HasKey(r => r.Instant);
            else if (role == "alternate") row.HasAlternateKey(r => r.Instant);
            else row.Property(r => r.Instant).IsConcurrencyToken();
        });
        Assert.Contains("payload-only", Assert.Throws<InvalidOperationException>(() => db.Model).Message);
    }

    [Fact]
    public void Schema_manifest_describes_native_storage_without_implying_other_temporal_types()
    {
        using var db = Context();
        var manifest = GraphSchemaManifest.Create(db.Model);
        Assert.Contains("\"providerType\":\"ZONED DATETIME\"", manifest.CanonicalJson);
        Assert.DoesNotContain("\"conversion\":\"opaque\"", manifest.CanonicalJson);
    }

    [Fact]
    public void Unsupported_temporal_methods_and_membership_fail_before_transport()
    {
        var driver = ProviderTestDriver.Create();
        using var db = new ProviderModelContext(driver, Configure);
        var values = new[] { DateTimeOffset.UnixEpoch };
        Assert.Throws<Neo4jQueryTranslationException>(() => db.Set<Row>().Where(r => values.Contains(r.Instant)).ToQueryString());
        Assert.Throws<Neo4jQueryTranslationException>(() => db.Set<Row>().Select(r => r.Instant).Contains(values[0]));
        Assert.Throws<Neo4jQueryTranslationException>(() => db.Set<Row>().Where(r => object.Equals(r.Instant, values[0])).ToQueryString());
        Assert.Throws<Neo4jQueryTranslationException>(() => db.Set<Row>().Where(r => r.Instant.AddDays(1) > values[0]).ToQueryString());
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    private static ProviderModelContext Context() => new(ProviderTestDriver.Create(), Configure);
    private static void Configure(ModelBuilder builder)
    {
        builder.Entity<Row>().ToGraphNode("TemporalUnit");
        builder.Entity<Row>().Property(r => r.Id).ValueGeneratedNever();
    }
    public sealed class Row
    {
        public int Id { get; set; }
        public DateTimeOffset Instant { get; set; }
        public DateTimeOffset? Optional { get; set; }
    }
}
