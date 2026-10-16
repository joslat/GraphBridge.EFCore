using Neo4j.Driver;

namespace GraphBridge.EntityFrameworkCore.Diagnostics;

/// <summary>A server planning result, not execution statistics or a cached execution plan.</summary>
/// <remarks>Plan arguments may expose identifiers and literal values from caller-authored Cypher.
/// No plan text or parameter values are logged automatically. The fingerprint hashes exact statement
/// text (including any literals), excludes parameter values, and is a diagnostic identity only.</remarks>
public sealed record GraphQueryExplanation(string StatementFingerprint, IPlan Plan,
    TimeSpan ResultAvailableAfter, TimeSpan ResultConsumedAfter);
