using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace GraphBridge.EntityFrameworkCore.Infrastructure;

/// <summary>GraphBridge-specific options passed to UseNeo4j, such as the command timeout.</summary>
public sealed class Neo4jDbContextOptionsBuilder
{
    private readonly DbContextOptionsBuilder _builder;

    internal Neo4jDbContextOptionsBuilder(DbContextOptionsBuilder builder) => _builder = builder;

    /// <summary>The default timeout of the transactions GraphBridge runs commands in: queries, saves, bulk mutations and
    /// native Cypher (ADR 0050). Positive and at most 24 hours; null uses the server's default, whose own limit still applies.</summary>
    public Neo4jDbContextOptionsBuilder CommandTimeout(TimeSpan? timeout)
    {
        var extension = _builder.Options.FindExtension<Neo4jDbContextOptionsExtension>()
            ?? throw new InvalidOperationException("UseNeo4j must be configured before its options.");
        ((IDbContextOptionsBuilderInfrastructure)_builder).AddOrUpdateExtension(extension.WithCommandTimeout(timeout));
        return this;
    }
}
