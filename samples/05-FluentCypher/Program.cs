using System.Text.Json;
using GraphBridge.Samples.FluentCypher;
using GraphBridge.Tooling.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

// Offline is the default. Connected inspection and disposable semantic checks are separate explicit modes.
if (args is ["--lab-host"]) return await LabHost.RunAsync();
if (args.Length > 1 || args.Length == 1 && args[0] is not ("--check" or "--doctor" or "--doctor-text" or "--doctor-inspect" or "--verify" or "--verify-text" or "--verify-inspect" or "--verify-semantic" or "--verify-semantic-text" or "--lab" or "--lab-json" or "--lab-inspect" or "--lab-semantic"))
{
    Console.Error.WriteLine("Usage: dotnet run --project samples/05-FluentCypher/FluentCypher.Sample.csproj -- [--check|--doctor|--doctor-text|--doctor-inspect|--verify|--verify-text|--verify-inspect|--verify-semantic|--verify-semantic-text|--lab|--lab-json|--lab-inspect|--lab-semantic]");
    return 2;
}
var lab = args.Length == 1 && args[0] is "--lab" or "--lab-json" or "--lab-inspect" or "--lab-semantic";
var semantic = args.Length == 1 && args[0] is "--verify-semantic" or "--verify-semantic-text" or "--lab-semantic";
var connected = semantic || args.Length == 1 && args[0] is "--doctor-inspect" or "--verify-inspect" or "--lab-inspect";
SampleEnvironment settings;
try { settings = SampleEnvironment.Read(connected, semantic); }
catch (InvalidOperationException error)
{
    Console.Error.WriteLine(error.Message);
    return 2;
}
var database = settings.Database;
var runId = settings.RunId;
using var driver = GraphDatabase.Driver(settings.Uri, settings.Auth, options => options.WithConnectionTimeout(TimeSpan.FromSeconds(10)));
using var db = new InspectionContext(driver, database);
if (connected)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    if (semantic)
    {
        var verification = await GraphVerify.SemanticAsync(() => new InspectionContext(driver, database),
            FluentMovieContracts.Create(runId!, afterYear: 2000), "sample.disposable.v1", settings.Fixture!,
            FluentMovieReferences.Create(runId!), includeStatements: lab, cancellationToken: deadline.Token);
        Console.WriteLine(lab ? GraphQueryLab.RenderHtml(verification,
            FluentMovieSources.Create().Where(s => verification.Inspection?.Evidence.Queries.Any(q => q.ContractId == s.ContractId) == true))
            : args[0] == "--verify-semantic-text" ? verification.ToText() : verification.ToJson());
        return verification.ExitCode;
    }
    if (args[0] is "--verify-inspect" or "--lab-inspect")
    {
        var verification = await GraphVerify.InspectAsync(db, FluentMovieContracts.Create("inspection-run"),
            "sample.configured.v1", includeStatements: lab, cancellationToken: deadline.Token);
        Console.WriteLine(lab ? GraphQueryLab.RenderHtml(verification, FluentMovieSources.Create()) : verification.ToJson());
        return verification.ExitCode;
    }
    var report = await GraphDoctor.InspectAsync(db, FluentMovieContracts.Create("inspection-run"),
        "sample.configured.v1", cancellationToken: deadline.Token);
    Console.WriteLine(report.ToJson());
    return report.Status == GraphEvidenceStatus.Pass ? 0 : 1;
}
if (lab)
{
    var verification = GraphVerify.Offline(db, FluentMovieContracts.Create("inspection-run"), includeStatements: true);
    Console.WriteLine(args[0] == "--lab-json"
        ? JsonSerializer.Serialize(new {
            kind = "graphbridge-query-lab", formatVersion = 1,
            report = JsonSerializer.Deserialize<JsonElement>(verification.ToJson()),
            sources = FluentMovieSources.Create().Select(s => new { contractId = s.ContractId, text = s.Text })
        }, new JsonSerializerOptions { WriteIndented = true })
        : GraphQueryLab.RenderHtml(verification, FluentMovieSources.Create()));
    return verification.ExitCode;
}
if (args.Length == 1 && args[0] is "--verify" or "--verify-text")
{
    var verification = GraphVerify.Offline(db, FluentMovieContracts.Create("inspection-run"));
    Console.WriteLine(args[0] == "--verify-text" ? verification.ToText() : verification.ToJson());
    return verification.ExitCode;
}
if (args.Length == 1 && args[0] is "--doctor" or "--doctor-text")
{
    var report = GraphDoctor.InspectOffline(db, FluentMovieContracts.Create("inspection-run"));
    Console.WriteLine(args[0] == "--doctor-text" ? report.ToText() : report.ToJson());
    return report.Status == GraphEvidenceStatus.Pass ? 0 : 1;
}
var statements = new SortedDictionary<string, string>(StringComparer.Ordinal)
{
    ["coactors"] = FluentMovieQueries.Coactors(db, "inspection-run", "Ada").ToQueryString(),
    ["shared-movies"] = FluentMovieQueries.SharedMovies(db, "inspection-run", "Ada").ToQueryString(),
    ["optional-movies"] = FluentMovieQueries.OptionalMovies(db, "inspection-run", 2020).ToQueryString(),
    ["filtered-optional-movies"] = FluentMovieQueries.FilteredOptionalMovies(db, "inspection-run", 2020).ToQueryString(),
    ["nested-exists"] = FluentMovieQueries.FriendsReaching(db, "inspection-run", "Dan").ToQueryString(),
    ["friend-paths"] = FluentMovieQueries.FriendPaths(db, "inspection-run", "Ada").ToQueryString(),
    ["cast-search"] = FluentMovieQueries.CastSearch(db, "inspection-run", ["Ada", "Cara"], "Fir").ToQueryString(),
    ["careers"] = FluentMovieQueries.Careers(db, "inspection-run").ToQueryString(),
    ["filmographies"] = FluentMovieQueries.Filmographies(db, "inspection-run").ToQueryString()
};
if (args.Length == 0)
{
    Console.WriteLine(JsonSerializer.Serialize(statements, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
foreach (var (name, actual) in statements)
{
    var file = Path.Combine(AppContext.BaseDirectory, "queries", name + ".cypher");
    if (!File.Exists(file) || File.ReadAllText(file).ReplaceLineEndings("\n").TrimEnd() != actual.ReplaceLineEndings("\n").TrimEnd())
    {
        Console.Error.WriteLine($"Compiled gallery snapshot differs: {name}. Review the generated query and native acceptance before updating it.");
        return 1;
    }
}
Console.WriteLine("Nine compiled gallery snapshots match. No database query executed; live semantic acceptance is separate.");
return 0;

internal sealed class InspectionContext(IDriver driver, string database) : DbContext(new DbContextOptionsBuilder().UseNeo4j(driver, database).Options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => FluentMovieQueries.Configure(modelBuilder);
}
