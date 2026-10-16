using System.Text.Json;
using GraphBridge.Samples.FluentCypher;
using GraphBridge.Tooling.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

using var driver = GraphDatabase.Driver("bolt://127.0.0.1:1", AuthTokens.None);
using var context = new ExportContext(driver);
var report = GraphVerify.Offline(context, FluentMovieContracts.Create("inspection-run"), includeStatements: true);
var envelope = new {
    kind = "graphbridge-query-lab", formatVersion = 1,
    report = JsonSerializer.Deserialize<JsonElement>(report.ToJson()),
    sources = FluentMovieSources.Create().Select(s => new { contractId = s.ContractId, text = s.Text })
};
File.WriteAllText(args[0], JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Exported {report.Contracts.Count} real compiled contracts; status {report.Status}; no database access.");
internal sealed class ExportContext(IDriver driver) : DbContext(new DbContextOptionsBuilder().UseNeo4j(driver, "neo4j").Options)
{
    protected override void OnModelCreating(ModelBuilder builder) => FluentMovieQueries.Configure(builder);
}
