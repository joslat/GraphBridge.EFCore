namespace GraphBridge.EntityFrameworkCore.FluentCypher;

/// <summary>Coded Fluent Cypher failures. The thrown type stays NotSupportedException; the code is in the message and Data.</summary>
internal static class CypherDiagnostics
{
    internal const string DataKey = "GraphBridge.FluentDiagnostic";

    internal static NotSupportedException Create(string code, string message)
    {
        var error = new NotSupportedException($"GraphBridge fluent Cypher [{code}]: {message}");
        error.Data[DataKey] = code;
        return error;
    }
}
