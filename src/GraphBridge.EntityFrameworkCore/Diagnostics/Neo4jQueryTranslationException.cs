namespace GraphBridge.EntityFrameworkCore.Diagnostics;

/// <summary>A query uses a shape that GraphBridge cannot execute on Neo4j.</summary>
/// <remarks><see cref="Code"/> is the stable GBQ code, which is also in the message and in <c>Data["GraphBridge.QueryDiagnostic"]</c>.</remarks>
public sealed class Neo4jQueryTranslationException : InvalidOperationException
{
    public Neo4jQueryTranslationException(string message) : base(message) { }

    public Neo4jQueryTranslationException(string message, string code) : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
        Data[Neo4jQueryRefusals.DataKey] = code;
    }

    /// <summary>The stable diagnostic code, such as <c>GBQ101</c>; null when the exception was created without one.</summary>
    public string? Code { get; }
}
