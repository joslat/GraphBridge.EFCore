using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;

namespace GraphBridge.UnitTests;

public sealed class CompiledCommandTests
{
    [Fact]
    public void Warm_parameter_changes_reuse_command_text_until_EF_cache_eviction()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 10240 });
        var driver = ProviderTestDriver.Create();
        var compilations = 0;
        var events = new List<string>();
        var options = new DbContextOptionsBuilder<CommandContext>().UseNeo4j(driver, "neo4j").UseMemoryCache(cache)
            .LogTo((id, _) => id == CoreEventId.QueryCompilationStarting, message => { compilations++; events.Add(message.ToString()); }).Options;
        using var context = new CommandContext(options);
        var threshold = 1;
        var values = new[] { 1 };
        var query = context.Set<Row>().AsNoTracking().Where(p => p.Age >= threshold && values.Contains(p.Age));
        var command = query.ToQueryString();
        for (var i = 0; i < 100; i++)
        {
            threshold = i;
            values = i % 2 == 0 ? [] : [i, i + 1];
            Assert.Same(command, query.ToQueryString());
        }
        Assert.True(compilations == 1, string.Join("\n", events));
        cache.Compact(1);
        var recompiled = query.ToQueryString();
        Assert.Equal(command, recompiled);
        // Equal command literals can share runtime string storage after recompilation.
        // The compilation event, rather than string identity, proves cache eviction.
        Assert.Equal(2, compilations);
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Fact]
    public void Many_distinct_shapes_remain_within_the_supplied_EF_cache_size_limit()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 256, TrackStatistics = true });
        var driver = ProviderTestDriver.Create();
        using var context = new CommandContext(new DbContextOptionsBuilder<CommandContext>()
            .UseNeo4j(driver, "neo4j").UseMemoryCache(cache).Options);
        var untagged = context.Set<Row>().ToQueryString();
        for (var i = 0; i < 100; i++)
        {
            // Each tag is a distinct cached shape; ADR 0050 writes it as a comment before the same command.
            var text = context.Set<Row>().TagWith("cache-shape-" + i).ToQueryString();
            Assert.Equal("// cache-shape-" + i + "\n\n" + untagged, text);
            Assert.InRange(cache.GetCurrentStatistics()!.CurrentEstimatedSize!.Value, 0, 256);
        }
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    public sealed class Row { public int Id { get; set; } public int Age { get; set; } }
    public sealed class CommandContext(DbContextOptions<CommandContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model) => model.Entity<Row>().ToGraphNode("CompiledCommandRow");
    }
}
