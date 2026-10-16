using GraphBridge.EntityFrameworkCore.FluentCypher;

namespace Microsoft.EntityFrameworkCore;

/// <summary>The explicit read-only, Cypher-semantic query surface alongside ordinary EF LINQ.</summary>
public static class Neo4jFluentCypherExtensions
{
    public static CypherQuery Cypher(this DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new(context);
    }
}
