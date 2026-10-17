namespace GraphBridge.EntityFrameworkCore.FluentCypher;

/// <summary>Coded Fluent Cypher failures: a FluentCypherException with the code in Code, the message and Data.</summary>
internal static class CypherDiagnostics
{
    internal const string DataKey = "GraphBridge.FluentDiagnostic";

    internal static FluentCypherException Create(string code, string message)
        => new($"GraphBridge fluent Cypher [{code}]: {message}", code);
}
