using System.Diagnostics;

namespace GraphBridge.IntegrationTests;

public sealed partial class SemanticVerifyIntegrationTests
{
    [Neo4jIntegrationFact]
    public async Task Editor_host_bridge_observes_live_plans_results_and_fixture_gate_without_changing_graph()
    {
        var run = Guid.NewGuid().ToString("N");
        var owner = Guid.ParseExact(Environment.GetEnvironmentVariable("GRAPHBRIDGE_FIXTURE_ID")!, "N");
        var constraints = new[] { "gb_host_person_" + run, "gb_host_movie_" + run, "gb_host_credit_" + run };
        var baseline = await State(run);
        try
        {
            await Execute($"CREATE CONSTRAINT {constraints[0]} FOR (n:FGQPerson) REQUIRE n.Id IS UNIQUE");
            await Execute($"CREATE CONSTRAINT {constraints[1]} FOR (n:FGQMovie) REQUIRE n.Id IS UNIQUE");
            await Execute($"CREATE CONSTRAINT {constraints[2]} FOR ()-[r:FGQ_ACTED]-() REQUIRE r.Id IS UNIQUE");
            await Seed(run, owner);
            var before = await State(run);
            var root = RepositoryRoot();
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("GRAPHBRIDGE_NODE_EXECUTABLE") ?? "node")
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            start.ArgumentList.Add(Path.Combine(root, "tests/GraphBridge.QueryLab.Tests/live-host.mjs"));
            start.Environment["GRAPHBRIDGE_VERIFY_DISPOSABLE"] = "1";
            start.Environment["GRAPHBRIDGE_VERIFY_RUN_ID"] = run;
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
            var errors = await stderr;
            Assert.True(process.ExitCode == 0, $"live-host.mjs exited with code {process.ExitCode}: {errors}");
            Assert.Contains("Live bridge assertions passed", await stdout);
            Assert.Equal(before, await State(run));
        }
        finally
        {
            await Execute("MATCH (:FGQPerson {RunId:$run})-[r:FGQ_ACTED]->(:FGQMovie {RunId:$run}) DELETE r", new { run });
            await Execute("MATCH (n:FGQPerson {RunId:$run}) DELETE n", new { run });
            await Execute("MATCH (n:FGQMovie {RunId:$run}) DELETE n", new { run });
            await Execute("MATCH (f:GraphBridgeVerificationFixture {runId:$run}) DELETE f", new { run });
            foreach (var name in constraints) await Execute($"DROP CONSTRAINT {name} IF EXISTS");
        }
        Assert.Equal(baseline, await State(run));
    }
}
