using System.Diagnostics;
using Neo4j.Driver;
using Neo4j.Driver.Mapping;
using Xunit.Abstractions;

namespace GraphBridge.IntegrationTests;

/// <summary>Direct Neo4j.Driver reference only; these tests do not prove EF query support.</summary>
[Collection(Neo4jIntegrationCollection.Name)]
public sealed class DriverBaselineTests(DisposableNeo4jFixture fixture, ITestOutputHelper output)
{
    private const string Create = "CREATE (p:Person:GraphBridgeBaseline {runId: $runId, id: $id, name: $name, age: $age}) RETURN p.id AS Id";
    private const string Read = "MATCH (p:Person:GraphBridgeBaseline {runId: $runId, id: $id}) RETURN p.id AS Id, p.name AS Name, p.age AS Age";

    [Neo4jIntegrationFact]
    public async Task Parameterized_crud_and_driver_dto_mapping_round_trip()
    {
        var runId = Guid.NewGuid().ToString("N");
        var id = Guid.NewGuid().ToString("D");
        const string name = "O'Reilly `$value` \\ MATCH (unrelated) DELETE unrelated //";
        output.WriteLine($"Server: {fixture.ServerVersion}; edition: {fixture.ServerEdition}; advertised Cypher: {string.Join(", ", fixture.CypherVersions)}; driver: {typeof(IDriver).Assembly.GetName().Version}");
        try
        {
            await using var session = fixture.OpenSession();
            var created = await session.RunAsync(Create, new { runId, id, name, age = 29 });
            Assert.Equal(id, (await created.SingleAsync())["Id"].As<string>());
            var cursor = await session.RunAsync(Read, new { runId, id });
            var person = (await cursor.SingleAsync()).AsObject<BaselinePerson>();
            Assert.Equal(id, person.Id);
            Assert.Equal(name, person.Name);
            Assert.Equal(29, person.Age);

            var updated = await session.RunAsync(
                "MATCH (p:Person:GraphBridgeBaseline {runId: $runId, id: $id}) SET p.age = $age RETURN p.age AS age",
                new { runId, id, age = 30 });
            Assert.Equal(30, (await updated.SingleAsync())["age"].As<int>());
            var deleted = await session.RunAsync(
                "MATCH (p:Person:GraphBridgeBaseline {runId: $runId, id: $id}) DELETE p", new { runId, id });
            Assert.Equal(1, (await deleted.ConsumeAsync()).Counters.NodesDeleted);
            var missing = await session.RunAsync(Read, new { runId, id });
            Assert.Empty(await missing.ToListAsync());
        }
        finally
        {
            await fixture.CleanupAsync(runId);
        }
    }

    [Neo4jIntegrationFact]
    public async Task Explicit_transaction_rolls_back_two_successful_writes()
    {
        var runId = Guid.NewGuid().ToString("N");
        try
        {
            await using (var session = fixture.OpenSession())
            await using (var transaction = await session.BeginTransactionAsync())
            {
                foreach (var name in new[] { "first", "second" })
                {
                    var cursor = await transaction.RunAsync(Create,
                        new { runId, id = Guid.NewGuid().ToString("D"), name, age = 21 });
                    await cursor.ConsumeAsync();
                }

                var before = await transaction.RunAsync(
                    "MATCH (p:Person:GraphBridgeBaseline {runId: $runId}) RETURN count(p) AS count", new { runId });
                Assert.Equal(2, (await before.SingleAsync())["count"].As<long>());
                await transaction.RollbackAsync();
            }

            await using var verification = fixture.OpenSession();
            var after = await verification.RunAsync(
                "MATCH (p:Person:GraphBridgeBaseline {runId: $runId}) RETURN count(p) AS count", new { runId });
            Assert.Equal(0, (await after.SingleAsync())["count"].As<long>());
        }
        finally
        {
            await fixture.CleanupAsync(runId);
        }
    }

    [Neo4jIntegrationFact]
    public async Task Server_error_rolls_back_earlier_write()
    {
        var runId = Guid.NewGuid().ToString("N");
        try
        {
            await using (var session = fixture.OpenSession())
            await using (var transaction = await session.BeginTransactionAsync())
            {
                var first = await transaction.RunAsync(Create,
                    new { runId, id = Guid.NewGuid().ToString("D"), name = "must roll back", age = 21 });
                await first.ConsumeAsync();
                await Assert.ThrowsAsync<TypeException>(async () =>
                {
                    var invalid = await transaction.RunAsync(
                        "CREATE (p:Person:GraphBridgeBaseline {runId: $runId, invalid: $invalid})",
                        new { runId, invalid = new Dictionary<string, object> { ["nested"] = "unsupported property map" } });
                    await invalid.ConsumeAsync();
                });
            }

            await using var verification = fixture.OpenSession();
            var after = await verification.RunAsync(
                "MATCH (p:Person:GraphBridgeBaseline {runId: $runId}) RETURN count(p) AS count", new { runId });
            Assert.Equal(0, (await after.SingleAsync())["count"].As<long>());
        }
        finally
        {
            await fixture.CleanupAsync(runId);
        }
    }

    [Neo4jIntegrationFact]
    public async Task Repeated_parameterized_read_records_latency_and_process_allocations()
    {
        var runId = Guid.NewGuid().ToString("N");
        var id = Guid.NewGuid().ToString("D");
        const int samples = 50;
        try
        {
            await using var session = fixture.OpenSession();
            var created = await session.RunAsync(Create, new { runId, id, name = "baseline", age = 30 });
            await created.ConsumeAsync();
            for (var index = 0; index < 5; index++)
            {
                var warmup = await session.RunAsync(Read, new { runId, id });
                await warmup.SingleAsync();
            }

            var timings = new double[samples];
            var allocationStart = GC.GetTotalAllocatedBytes(precise: true);
            for (var index = 0; index < samples; index++)
            {
                var start = Stopwatch.GetTimestamp();
                var cursor = await session.RunAsync(Read, new { runId, id });
                var record = await cursor.SingleAsync();
                Assert.Equal(id, record["Id"].As<string>());
                timings[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }

            var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocationStart;
            Array.Sort(timings);
            output.WriteLine($"Direct driver, one-node buffered read, {samples} warm sequential samples, 5 warmups.");
            output.WriteLine($"Server {fixture.ServerVersion} {fixture.ServerEdition}; runtime {Environment.Version}; OS {Environment.OSVersion}.");
            output.WriteLine($"Latency ms: p50={Percentile(timings, .50):F3}, p95={Percentile(timings, .95):F3}, p99={Percentile(timings, .99):F3}; mean={timings.Average():F3}.");
            output.WriteLine($"Process-wide managed allocation delta={allocated} bytes; approximate bytes/read={allocated / samples}. Includes runner/background allocations; async thread-local measurements would be misleading. No performance threshold asserted.");
        }
        finally
        {
            await fixture.CleanupAsync(runId);
        }
    }

    private static double Percentile(double[] sorted, double percentile) => sorted[(int)Math.Ceiling(sorted.Length * percentile) - 1];

    public sealed class BaselinePerson
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public int Age { get; set; }
    }
}
