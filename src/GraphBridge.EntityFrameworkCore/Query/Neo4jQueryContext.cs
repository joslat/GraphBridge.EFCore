using GraphBridge.EntityFrameworkCore.Infrastructure;
using GraphBridge.EntityFrameworkCore.Transport;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;

namespace GraphBridge.EntityFrameworkCore.Query;

internal sealed class Neo4jQueryContext(
    QueryContextDependencies dependencies,
    Neo4jDbContextOptionsExtension options,
    Neo4jTransactionManager transactions) : QueryContext(dependencies)
{
    public Neo4jDbContextOptionsExtension Options { get; } = options;
    public Neo4jTransactionManager Transactions { get; } = transactions;
}

internal sealed class Neo4jQueryContextFactory(
    QueryContextDependencies dependencies,
    IDbContextOptions options,
    Neo4jTransactionManager transactions) : IQueryContextFactory
{
    public QueryContext Create() => new Neo4jQueryContext(
        dependencies,
        options.FindExtension<Neo4jDbContextOptionsExtension>()
            ?? throw new InvalidOperationException("UseNeo4j must be configured before querying."),
        transactions);
}
