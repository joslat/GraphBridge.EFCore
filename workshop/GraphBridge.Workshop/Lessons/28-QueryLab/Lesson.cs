using System.Text.Json;
using GraphBridge.Tooling.Diagnostics;

namespace GraphBridge.Workshop.Lessons;

public sealed class Lesson28QueryLab : ILesson
{
    public LessonInfo Info { get; } = new("28", 'F', "Query Lab", "28-QueryLab", LessonMode.Offline, Core: true,
        Requirement.None, "Produce a Query Lab report that shows each query's C#, its Cypher and its typed graph pattern side by side.");

    public async Task RunAsync(LessonContext lesson, CancellationToken cancellationToken)
    {
        await using var db = lesson.CreateContext();
        var folder = Path.Combine(Path.GetTempPath(), "GraphBridgeWorkshop");
        Directory.CreateDirectory(folder);

        lesson.Step("Compile the registered contracts with their statements, offline");
        lesson.Note("GraphVerify.Offline compiles every contract registered in lesson 26 (lesson 27 covers Verify itself). Query Lab only presents that evidence.");
        lesson.Code("Report");
        // workshop:start Report
        var verification = GraphVerify.Offline(db, WorkshopContracts.Create(), includeStatements: true);
        var html = GraphQueryLab.RenderHtml(verification, WorkshopContracts.Sources());
        var htmlPath = Path.Combine(folder, "query-lab.html");
        await File.WriteAllTextAsync(htmlPath, html, cancellationToken);
        // workshop:end Report
        lesson.Value("Open in a browser", htmlPath);
        lesson.Expect("offline verification passes", 0, verification.ExitCode);
        lesson.Check("the report is a self-contained HTML page", html.StartsWith("<!doctype html>", StringComparison.OrdinalIgnoreCase) && !html.Contains("<script src", StringComparison.OrdinalIgnoreCase));
        lesson.Check("it includes both contracts", html.Contains("workshop.coactors.v1", StringComparison.Ordinal) && html.Contains("workshop.recent-work.v1", StringComparison.Ordinal));

        lesson.Step("Export the same evidence for the VS Code Query Lab viewer");
        lesson.Code("Json");
        // workshop:start Json
        var envelope = JsonSerializer.Serialize(new
        {
            kind = "graphbridge-query-lab",
            formatVersion = 1,
            report = JsonSerializer.Deserialize<JsonElement>(verification.ToJson()),
            sources = WorkshopContracts.Sources().Select(s => new { contractId = s.ContractId, text = s.Text })
        }, new JsonSerializerOptions { WriteIndented = true });
        var jsonPath = Path.Combine(folder, "query-lab.json");
        await File.WriteAllTextAsync(jsonPath, envelope, cancellationToken);
        // workshop:end Json
        lesson.Value("Open in VS Code (right-click the file, Open Saved Evidence)", jsonPath);
        using var parsed = JsonDocument.Parse(envelope);
        lesson.Expect("the envelope is a Query Lab document", "graphbridge-query-lab", parsed.RootElement.GetProperty("kind").GetString());
        lesson.Limit("Statements are included on purpose here; parameter values never are. The VS Code extension is built locally (extensions/graphbridge-query-lab); it is not on the Marketplace.");
    }
}
