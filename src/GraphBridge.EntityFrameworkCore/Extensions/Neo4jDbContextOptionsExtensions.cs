using GraphBridge.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Neo4j.Driver;

namespace Microsoft.EntityFrameworkCore;

public static class Neo4jDbContextOptionsExtensions
{
    /// <summary>Configures Neo4j with a caller-owned driver that GraphBridge does not dispose.</summary>
    public static DbContextOptionsBuilder UseNeo4j(this DbContextOptionsBuilder builder, IDriver driver,
        string databaseName = "neo4j")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(
            new Neo4jDbContextOptionsExtension(driver, databaseName));
        return builder;
    }

    public static DbContextOptionsBuilder<TContext> UseNeo4j<TContext>(this DbContextOptionsBuilder<TContext> builder,
        IDriver driver, string databaseName = "neo4j") where TContext : DbContext
    {
        UseNeo4j((DbContextOptionsBuilder)builder, driver, databaseName);
        return builder;
    }

    /// <summary>Configures Neo4j with a caller-owned driver and GraphBridge options, for example
    /// <c>neo4j => neo4j.CommandTimeout(TimeSpan.FromSeconds(30))</c>.</summary>
    public static DbContextOptionsBuilder UseNeo4j(this DbContextOptionsBuilder builder, IDriver driver,
        string databaseName, Action<Neo4jDbContextOptionsBuilder> neo4jOptions)
    {
        ArgumentNullException.ThrowIfNull(neo4jOptions);
        UseNeo4j(builder, driver, databaseName);
        neo4jOptions(new Neo4jDbContextOptionsBuilder(builder));
        return builder;
    }

    public static DbContextOptionsBuilder<TContext> UseNeo4j<TContext>(this DbContextOptionsBuilder<TContext> builder,
        IDriver driver, string databaseName, Action<Neo4jDbContextOptionsBuilder> neo4jOptions) where TContext : DbContext
    {
        UseNeo4j((DbContextOptionsBuilder)builder, driver, databaseName, neo4jOptions);
        return builder;
    }
}
