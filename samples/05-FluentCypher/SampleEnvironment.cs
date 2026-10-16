using GraphBridge.Tooling.Diagnostics;
using Neo4j.Driver;

namespace GraphBridge.Samples.FluentCypher;

internal sealed record SampleEnvironment(string Uri, string Database, IAuthToken Auth,
    GraphVerificationFixture? Fixture = null, string? RunId = null)
{
    public static SampleEnvironment Read(bool connected, bool semantic)
    {
        if (!connected) return new("bolt://127.0.0.1:1", "neo4j", AuthTokens.None);
        var names = new[] { "GRAPHBRIDGE_NEO4J_URI", "GRAPHBRIDGE_NEO4J_DATABASE", "GRAPHBRIDGE_NEO4J_USERNAME", "GRAPHBRIDGE_NEO4J_PASSWORD" };
        var values = names.Select(Environment.GetEnvironmentVariable).ToArray();
        if (values.Any(string.IsNullOrWhiteSpace) || !System.Uri.TryCreate(values[0], UriKind.Absolute, out var configured)
            || configured.Scheme is not ("bolt" or "bolt+s" or "bolt+ssc" or "neo4j" or "neo4j+s" or "neo4j+ssc")
            || !string.IsNullOrEmpty(configured.UserInfo))
            throw new InvalidOperationException("Connected inspection requires the four GRAPHBRIDGE_NEO4J_* variables and a supported URI without embedded credentials.");
        var result = new SampleEnvironment(values[0]!, values[1]!, AuthTokens.Basic(values[2]!, values[3]!));
        if (!semantic) return result;
        var runId = Environment.GetEnvironmentVariable("GRAPHBRIDGE_VERIFY_RUN_ID");
        if (result.Uri != "bolt://127.0.0.1:17687" || result.Database != "neo4j"
            || Environment.GetEnvironmentVariable("GRAPHBRIDGE_VERIFY_DISPOSABLE") != "1"
            || !Guid.TryParseExact(Environment.GetEnvironmentVariable("GRAPHBRIDGE_FIXTURE_ID"), "N", out var owner) || owner == Guid.Empty
            || !Guid.TryParseExact(runId, "N", out var run) || run == Guid.Empty)
            throw new InvalidOperationException("Semantic verification requires the disposable loopback fixture, GRAPHBRIDGE_VERIFY_DISPOSABLE=1, its fixture ID and a provisioned GRAPHBRIDGE_VERIFY_RUN_ID (nonempty GUID N format).");
        return result with { RunId = runId, Fixture = new(FluentMovieReferences.FixtureId, owner,
            FluentMovieReferences.FixtureFingerprint, acknowledgeDisposable: true) };
    }
}
