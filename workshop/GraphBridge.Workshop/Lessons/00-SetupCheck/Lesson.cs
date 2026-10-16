using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.Workshop.Lessons;

public sealed class Lesson00SetupCheck : ILesson
{
    public LessonInfo Info { get; } = new("00", 'A', "Setup check", "00-SetupCheck", LessonMode.Mixed, Core: true,
        Requirement.None, "Confirm the pinned packages and, if configured, a reachable and safe Neo4j database.");

    public async Task RunAsync(LessonContext lesson, CancellationToken cancellationToken)
    {
        lesson.Step("The packages this workshop runs on");
        lesson.Value(".NET runtime", Environment.Version);
        lesson.Value("GraphBridge provider", Version(typeof(GraphModelBuilderExtensions)));
        lesson.Value("EF Core", Version(typeof(DbContext)));
        lesson.Value("Neo4j.Driver", Version(typeof(IDriver)));
        lesson.Check("GraphBridge provider assembly loaded", typeof(GraphModelBuilderExtensions).Assembly.GetName().Name == "GraphBridge.EntityFrameworkCore");

        if (!lesson.NeedsDatabase("Connecting to Neo4j")) return;

        lesson.Step("Reach the database");
        lesson.Code("Server");
        // workshop:start Server
        var server = await WorkshopData.ReadAsync(lesson.Connection!,
            "CALL dbms.components() YIELD name, versions, edition RETURN name, versions[0] AS version, edition",
            record => new { Name = record["name"].As<string>(), Version = record["version"].As<string>(), Edition = record["edition"].As<string>() },
            cancellationToken);
        // workshop:end Server
        lesson.Table(server);
        lesson.Check("connected to Neo4j", server.Count > 0);

        lesson.Step("Check that the database is safe for the workshop");
        lesson.Note("The workshop writes Person, Movie and Review nodes and can reset them. It refuses a database that holds other data.");
        await WorkshopData.GuardAsync(lesson.Connection!, cancellationToken);
        lesson.Check("database passes the workshop safety guard", true);
    }

    private static string Version(Type type)
        => type.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? type.Assembly.GetName().Version?.ToString() ?? "unknown";
}
