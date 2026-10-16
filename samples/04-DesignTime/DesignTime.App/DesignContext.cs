using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Neo4j.Driver;

namespace GraphBridge.DesignTime.Sample;

public sealed class DesignPerson
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class DesignContext(DbContextOptions<DesignContext> options, IDriver ownedDriver) : DbContext(options)
{
    private IDriver? _ownedDriver = ownedDriver;
    public DbSet<DesignPerson> People => Set<DesignPerson>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<DesignPerson>().ToGraphNode("GraphBridgeDesignPerson");
        model.Entity<DesignPerson>().Property(p => p.Id).ValueGeneratedNever();
    }
    public override void Dispose()
    {
        try { base.Dispose(); }
        finally { Interlocked.Exchange(ref _ownedDriver, null)?.Dispose(); }
    }
    public override async ValueTask DisposeAsync()
    {
        try { await base.DisposeAsync(); }
        finally { if (Interlocked.Exchange(ref _ownedDriver, null) is { } driver) await driver.DisposeAsync(); }
    }
}

/// <summary>EF CLI metadata discovery is offline; no environment credentials are read by this factory.</summary>
public sealed class DesignContextFactory : IDesignTimeDbContextFactory<DesignContext>
{
    public DesignContext CreateDbContext(string[] args)
    {
        if (args.Length != 0) throw new ArgumentException("No design-time arguments are supported.");
        // Driver construction is lazy. Metadata-only commands never connect to this reserved local endpoint.
        var driver = GraphDatabase.Driver("bolt://127.0.0.1:1", AuthTokens.None);
        return new DesignContext(new DbContextOptionsBuilder<DesignContext>().UseNeo4j(driver).Options, driver);
    }

    public static DesignContext CreateStatusContext()
    {
        static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value : throw new InvalidOperationException("Missing live status setting: " + name);
        var uri = new Uri(Required("GRAPHBRIDGE_URI"), UriKind.Absolute);
        if (uri.Scheme is not ("bolt" or "bolt+s" or "bolt+ssc" or "neo4j" or "neo4j+s" or "neo4j+ssc")
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath is not ("" or "/"))
            throw new ArgumentException("Use an explicit Neo4j URI without embedded credentials, query, fragment or path.");
        var database = Required("GRAPHBRIDGE_DATABASE");
        var driver = GraphDatabase.Driver(uri, AuthTokens.Basic(Required("GRAPHBRIDGE_USERNAME"), Required("GRAPHBRIDGE_PASSWORD")));
        try { return new DesignContext(new DbContextOptionsBuilder<DesignContext>().UseNeo4j(driver, database).Options, driver); }
        catch { driver.Dispose(); throw; }
    }
}
