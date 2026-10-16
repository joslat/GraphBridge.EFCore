using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

namespace GraphBridge.EntityFrameworkCore.Infrastructure;

public sealed class Neo4jDbContextOptionsExtension : IDbContextOptionsExtension
{
    private readonly DbContextOptionsExtensionInfo _info;

    public Neo4jDbContextOptionsExtension(IDriver driver, string databaseName)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        Driver = driver;
        DatabaseName = databaseName;
        _info = new ExtensionInfo(this);
    }

    /// <summary>The caller-owned driver; GraphBridge never disposes it.</summary>
    public IDriver Driver { get; }
    public string DatabaseName { get; }
    public DbContextOptionsExtensionInfo Info => _info;

    public void ApplyServices(IServiceCollection services) => services.AddEntityFrameworkNeo4j();
    public void Validate(IDbContextOptions options) { }

    private sealed class ExtensionInfo(Neo4jDbContextOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => true;
        public override string LogFragment => "using Neo4j ";
        // All singletons are independent of the borrowed driver/database, which remain context options.
        public override int GetServiceProviderHashCode() => 0;
        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;
        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) => debugInfo["GraphBridge:Neo4j"] = "1";
    }
}
