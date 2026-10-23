using GraphBridge.EntityFrameworkCore.Transport;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Microsoft.EntityFrameworkCore;

/// <summary>The command timeout of one context instance (ADR 0050).</summary>
public static class Neo4jCommandTimeoutDatabaseFacadeExtensions
{
    /// <summary>Sets the timeout of the transactions this context begins from now on, overriding UseNeo4j's CommandTimeout
    /// until the context is reset. Null uses the server's default. A transaction already begun keeps its timeout.</summary>
    /// <remarks>Named differently from relational EF's SetCommandTimeout so both providers can be referenced together.</remarks>
    public static void SetGraphCommandTimeout(this DatabaseFacade database, TimeSpan? timeout)
        => Manager(database).CommandTimeout = timeout;

    /// <summary>The timeout transactions this context begins will have; null means the server's default.</summary>
    public static TimeSpan? GetGraphCommandTimeout(this DatabaseFacade database) => Manager(database).CommandTimeout;

    private static Neo4jTransactionManager Manager(DatabaseFacade database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return ((IDatabaseFacadeDependenciesAccessor)database).Context.GetService<Neo4jTransactionManager>();
    }
}
