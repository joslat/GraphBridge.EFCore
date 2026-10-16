using Microsoft.EntityFrameworkCore;

namespace GraphBridge.Benchmarks;

public sealed class BenchmarkPerson
{
    public Guid Id { get; set; }
    public string Run { get; set; } = "";
    public int Ordinal { get; set; }
    public string Name { get; set; } = "";
    public long Version { get; set; }
}

public sealed class BenchmarkContext(DbContextOptions<BenchmarkContext> options) : DbContext(options)
{
    public DbSet<BenchmarkPerson> People => Set<BenchmarkPerson>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        var person = model.Entity<BenchmarkPerson>().ToGraphNode("GraphBridgeBenchmarkPerson");
        person.HasKey(p => p.Id);
        person.Property(p => p.Id).ValueGeneratedNever();
        person.Property(p => p.Version).IsRowVersion();
    }
}
