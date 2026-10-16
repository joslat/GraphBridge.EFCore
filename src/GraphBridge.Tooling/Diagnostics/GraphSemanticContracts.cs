using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using Neo4j.Driver;

namespace GraphBridge.Tooling.Diagnostics;

/// <summary>Explicit permission to inspect an exclusively owned, separately provisioned disposable fixture.</summary>
/// <remarks>The fingerprint identifies the deterministic fixture definition, not a database content hash.
/// The host provisions GraphBridgeFixture(id=ownerId in N format) and GraphBridgeVerificationFixture
/// (ownerId, fixtureId, fingerprint). Neither marker is created or repaired by Verify. Markers are an
/// accidental-target guard, not authentication or proof of unchanged data. Use fixed reference expectations too.</remarks>
public sealed class GraphVerificationFixture
{
    public GraphVerificationFixture(string fixtureId, Guid ownerId, string fingerprint, bool acknowledgeDisposable)
    {
        EvidenceIdentity.Validate(fixtureId);
        if (ownerId == Guid.Empty) throw new ArgumentException("A nonempty fixture owner is required.", nameof(ownerId));
        if (fingerprint is null || fingerprint.Length != 64 || fingerprint.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("A lowercase SHA-256 fixture definition fingerprint is required.", nameof(fingerprint));
        if (!acknowledgeDisposable) throw new ArgumentException("Semantic verification requires explicit disposable-fixture acknowledgment.", nameof(acknowledgeDisposable));
        (FixtureId, OwnerId, Fingerprint) = (fixtureId, ownerId, fingerprint);
    }
    public string FixtureId { get; }
    public string Fingerprint { get; }
    internal Guid OwnerId { get; }
}

/// <summary>A trusted, independently authored native read query and fixed scalar expectations.</summary>
/// <remarks>Do not derive this query or its expected rows from the Fluent compiler. The application is responsible
/// for reviewing read-only semantics and immutable parameter values; this is not an arbitrary-query sandbox.
/// Text, parameters, row readers and expected values are kept outside exported verification reports.</remarks>
public sealed class GraphNativeReference
{
    private readonly Func<IRecord, IReadOnlyList<object?>> read;
    internal string Statement { get; }
    internal IReadOnlyDictionary<string, object?> Parameters { get; }
    internal IReadOnlyList<IReadOnlyList<object?>> ExpectedRows { get; }
    public GraphNativeReference(string id, string fixtureId, string statement,
        IReadOnlyDictionary<string, object?> parameters, Func<IRecord, IReadOnlyList<object?>> rowReader,
        IEnumerable<IReadOnlyList<object?>> expectedRows)
    {
        EvidenceIdentity.Validate(id);
        EvidenceIdentity.Validate(fixtureId);
        ArgumentException.ThrowIfNullOrWhiteSpace(statement);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(rowReader);
        ArgumentNullException.ThrowIfNull(expectedRows);
        if (statement.Length > 65_536) throw new ArgumentException("Reference statement is too large.", nameof(statement));
        var rows = expectedRows.Take(10_001).ToArray();
        if (rows.Length > 10_000 || rows.Any(r => r is null || r.Count is < 1 or > 64))
            throw new ArgumentException("Reference expectations exceed the supported scalar-row bounds.", nameof(expectedRows));
        ExpectedRows = Array.AsReadOnly(rows.Select(r => (IReadOnlyList<object?>)Array.AsReadOnly(r.ToArray())).ToArray());
        (Id, FixtureId, Statement, read) = (id, fixtureId, statement.ReplaceLineEndings("\n"), rowReader);
        Parameters = new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(parameters, StringComparer.Ordinal));
        StatementFingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Statement)));
    }
    public string Id { get; }
    public string FixtureId { get; }
    public string StatementFingerprint { get; }
    internal IReadOnlyList<object?> Read(IRecord record, int columns)
    {
        var row = read(record) ?? throw new InvalidOperationException("The reference reader returned null.");
        if (row.Count != columns) throw new InvalidOperationException("The reference reader returned the wrong column count.");
        return Array.AsReadOnly(row.ToArray());
    }
}
