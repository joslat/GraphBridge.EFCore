using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Neo4j.Driver;

namespace GraphBridge.Benchmarks;

/// <summary>Offline warm EF preparation experiment. The driver rejects every transport call.</summary>
public static class QueryPreparationProfile
{
    public static void Run()
    {
        const int iterations = 10000;
        var driver = DispatchProxy.Create<IDriver, NoTransport>();
        var compilations = 0;
        using var context = new BenchmarkContext(new DbContextOptionsBuilder<BenchmarkContext>()
            .UseNeo4j(driver, "neo4j")
            .LogTo((id, _) => id == CoreEventId.QueryCompilationStarting, _ => compilations++).Options);
        var run = "preparation-profile";
        var minimum = 0;
        var keys = new[] { 1, 3, 5 };
        var query = context.People.AsNoTracking()
            .Where(p => p.Run == run && p.Ordinal >= minimum && keys.Contains(p.Ordinal))
            .OrderBy(p => p.Ordinal).Take(100).Select(p => new { p.Id, p.Name, p.Ordinal });
        var expected = query.ToQueryString();
        for (var i = 0; i < iterations; i++) Check(i);
        var samples = new List<object>();
        for (var sample = 0; sample < 10; sample++)
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            for (var i = 0; i < iterations; i++) Check(i);
            var elapsed = Stopwatch.GetElapsedTime(start);
            samples.Add(new { sample, iterations, elapsedMilliseconds = elapsed.TotalMilliseconds,
                allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated });
        }
        if (compilations != 1) throw new InvalidOperationException("Expected one EF compilation for changing parameter values.");
        var assembly = typeof(Neo4jDbContextOptionsExtensions).Assembly;
        Console.WriteLine(JsonSerializer.Serialize(new { scope = "Offline warm ToQueryString preparation only; no database, streaming or latency SLO claim.",
            compilations, iterations, samples, framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            providerSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
            tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "runtime default",
            harnessSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(QueryPreparationProfile).Assembly.Location))),
            commandSha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(expected))) }));

        void Check(int value)
        {
            minimum = value;
            keys = value % 2 == 0 ? [value] : [value, value + 1, value + 2];
            if (query.ToQueryString() != expected) throw new InvalidOperationException("Changing values changed Cypher shape.");
        }
    }

    public class NoTransport : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new InvalidOperationException("Offline preparation attempted driver access: " + targetMethod?.Name);
    }
}
