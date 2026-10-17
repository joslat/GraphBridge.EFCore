namespace GraphBridge.EntityFrameworkCore.FluentCypher;

/// <summary>A Fluent Cypher query uses a shape that GraphBridge refuses to translate.</summary>
/// <remarks>
/// <see cref="Code"/> is the stable GBFC code, which is also in the message and in <c>Data["GraphBridge.FluentDiagnostic"]</c>.
/// Derives from <see cref="NotSupportedException"/>, so existing handlers keep working. Builder misuse, such as a
/// missing <c>Limit</c>, still throws <see cref="InvalidOperationException"/> or <see cref="ArgumentException"/>.
/// </remarks>
public sealed class FluentCypherException : NotSupportedException
{
    public FluentCypherException(string message, string code) : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
        Data[CypherDiagnostics.DataKey] = code;
    }

    /// <summary>The stable diagnostic code, such as <c>GBFC110</c>.</summary>
    public string Code { get; }
}
