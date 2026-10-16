namespace GraphBridge.EntityFrameworkCore.Diagnostics;

/// <summary>A query uses a shape that GraphBridge cannot execute on Neo4j.</summary>
public sealed class Neo4jQueryTranslationException(string message) : InvalidOperationException(message);
