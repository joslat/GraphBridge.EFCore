using System.Text.RegularExpressions;
using GraphBridge.IntegrationTests;
using GraphBridge.Workshop;
using Neo4j.Driver;

namespace GraphBridge.Workshop.Tests;

/// <summary>The workshop is verified by running its lessons; the lessons themselves contain no assertion code.</summary>
public sealed class WorkshopOfflineTests
{
    [Fact]
    public async Task Every_lesson_runs_offline_and_its_Cypher_matches_the_reviewed_snapshots()
    {
        var output = new StringWriter();
        var runner = new WorkshopRunner(null, new Presenter(output, color: false), SnapshotStore.Default());
        foreach (var lesson in WorkshopRunner.Lessons)
        {
            var outcome = await runner.RunAsync(lesson, CancellationToken.None);
            Assert.True(outcome.Passed, Describe(outcome, output));
            if (lesson.Info.Mode == LessonMode.Offline) Assert.Null(outcome.Skipped);
        }
    }

    [Fact]
    public void Lesson_catalog_is_ordered_and_every_lesson_has_its_script()
    {
        var ids = WorkshopRunner.Lessons.Select(l => l.Info.Id).ToArray();
        Assert.Equal(ids.Distinct().Count(), ids.Length);
        Assert.Equal(ids.Order(StringComparer.Ordinal), ids);
        Assert.Contains(WorkshopRunner.Lessons, l => l.Info.Core);
        foreach (var lesson in WorkshopRunner.Lessons)
        {
            Assert.StartsWith(lesson.Info.Id + "-", lesson.Info.Folder);
            Assert.True(File.Exists(Path.Combine(LessonsRoot(), lesson.Info.Folder, "README.md")), $"{lesson.Info.Folder} has no README.md");
        }
    }

    private static readonly Regex SnapshotExcerpt = new(@"<!-- snapshot: (?<name>[\w.-]+) -->[ \t]*\n```cypher\n(?<code>(?:(?!```).*\n)*?)```");

    [Fact]
    public void Readme_code_excerpts_are_the_code_that_runs()
    {
        // A README block preceded by <!-- region: Name --> or <!-- region: Domain/File.cs#Name --> must equal that source region.
        // Same rule as fill-excerpts.py: the body is whole lines up to the block's own closing fence.
        var excerpt = new Regex(@"<!-- region: (?<ref>\S+) -->[ \t]*\n```csharp\n(?<code>(?:(?!```).*\n)*?)```");
        var checkedExcerpts = 0;
        var checkedSnapshots = 0;
        foreach (var lesson in WorkshopRunner.Lessons)
        {
            var readme = File.ReadAllText(Path.Combine(LessonsRoot(), lesson.Info.Folder, "README.md")).Replace("\r", "");
            foreach (Match match in excerpt.Matches(readme))
            {
                var reference = match.Groups["ref"].Value;
                var (file, region) = reference.Contains('#')
                    ? (reference[..reference.IndexOf('#')], reference[(reference.IndexOf('#') + 1)..])
                    : ($"Lessons/{lesson.Info.Folder}/Lesson.cs", reference);
                Assert.True(SourceRegions.Get(file, region) == match.Groups["code"].Value.TrimEnd('\n'),
                    $"{lesson.Info.Folder}/README.md excerpt '{reference}' differs from the source region.");
                checkedExcerpts++;
            }
            // A ```cypher block preceded by <!-- snapshot: name --> must equal the reviewed snapshots/name.cypher.
            foreach (Match match in SnapshotExcerpt.Matches(readme))
            {
                var snapshot = File.ReadAllText(Path.Combine(LessonsRoot(), "..", "snapshots", match.Groups["name"].Value + ".cypher")).Replace("\r", "");
                Assert.True(snapshot.TrimEnd('\n') == match.Groups["code"].Value.TrimEnd('\n'),
                    $"{lesson.Info.Folder}/README.md Cypher '{match.Groups["name"].Value}' differs from its reviewed snapshot.");
                checkedSnapshots++;
            }
        }
        Assert.True(checkedExcerpts > 0);
        Assert.True(checkedSnapshots > 0);
    }

    [Fact]
    public void Remote_databases_are_refused_unless_explicitly_allowed()
    {
        var remote = new Uri("bolt://graph.example.com:7687");
        Assert.Throws<WorkshopConfigurationException>(() => WorkshopConnection.ForDriver(OfflineDriver.Instance, remote, "neo4j", new()));
        Assert.NotNull(WorkshopConnection.ForDriver(OfflineDriver.Instance, remote, "neo4j", new(AllowRemote: true)));
        Assert.True(WorkshopConnection.IsLoopback(new Uri("bolt://127.0.0.1:7687")));
        Assert.True(WorkshopConnection.IsLoopback(new Uri("bolt://localhost:7687")));
        Assert.True(WorkshopConnection.IsLoopback(new Uri("bolt://[::1]:7687")));
    }

    [Fact]
    public void A_second_process_lock_on_the_same_database_is_refused()
    {
        var uri = new Uri("bolt://localhost:7687");
        var database = "lock-test-shared-" + Guid.NewGuid().ToString("N");
        using var first = WorkshopLock.Acquire(uri, database);
        // Mutexes belong to threads; another thread stands in for a second workshop process.
        Exception? refused = null;
        var other = new Thread(() =>
        {
            try { WorkshopLock.Acquire(uri, database).Dispose(); }
            catch (Exception error) { refused = error; }
        });
        other.Start();
        other.Join();
        Assert.IsType<WorkshopConfigurationException>(refused);
    }

    internal static string Describe(LessonOutcome outcome, StringWriter output)
        => $"Lesson {outcome.LessonId} failed: "
           + string.Join("; ", outcome.Checkpoints.Where(c => !c.Passed).Select(c => $"{c.Name} ({c.Detail})"))
           + "\n--- output ---\n" + output;

    internal static string LessonsRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GraphBridge.sln"))) directory = directory.Parent;
        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Repository root not found."),
            "workshop", "GraphBridge.Workshop", "Lessons");
    }
}

/// <summary>Runs every lesson against the repository's disposable fixture, then removes the workshop data.</summary>
[Collection(Neo4jIntegrationCollection.Name)]
public sealed class WorkshopLiveTests(DisposableNeo4jFixture fixture)
{
    [Neo4jIntegrationFact]
    public async Task Every_lesson_passes_against_the_disposable_fixture()
    {
        // Other suites may leave their own labels on the shared fixture; the workshop only touches its own.
        await using var connection = Connection(allowOtherData: true);
        await WorkshopData.ResetAsync(connection, (_, _) => true, CancellationToken.None);
        try
        {
            var output = new StringWriter();
            var runner = new WorkshopRunner(connection, new Presenter(output, color: false), SnapshotStore.Default());
            foreach (var lesson in WorkshopRunner.Lessons)
            {
                var outcome = await runner.RunAsync(lesson, CancellationToken.None);
                Assert.True(outcome.Passed, WorkshopOfflineTests.Describe(outcome, output));
                Assert.Null(outcome.Skipped);
            }
            // Lessons clean up what they create: the graph still equals the seed, and the core track can be repeated on it.
            Assert.True(await WorkshopData.MatchesSeedAsync(connection, CancellationToken.None), "the full track left the graph different from the seed");
            var again = new WorkshopRunner(connection, new Presenter(new StringWriter(), color: false), SnapshotStore.Default());
            foreach (var lesson in WorkshopRunner.CoreTrack)
                Assert.True((await again.RunAsync(lesson, CancellationToken.None)).Passed, $"core lesson {lesson.Info.Id} failed on a second run");
        }
        finally
        {
            await WorkshopData.ResetAsync(connection, (_, _) => true, CancellationToken.None);
        }
    }

    [Neo4jIntegrationFact]
    public async Task A_graph_that_drifted_from_the_seed_is_detected_until_reset()
    {
        await using var connection = Connection(allowOtherData: true);
        await WorkshopData.ResetAsync(connection, (_, _) => true, CancellationToken.None);
        try
        {
            await WorkshopData.EnsureSchemaAsync(connection, CancellationToken.None);
            Assert.True(await WorkshopData.EnsureSeedAsync(connection, CancellationToken.None));
            Assert.True(await WorkshopData.MatchesSeedAsync(connection, CancellationToken.None));

            // What a hard kill in lesson 11 leaves behind: a third role that later runs would count.
            await Execute("MATCH (p:Person {name: 'Hugo Weaving'}), (m:Movie {title: 'Cloud Atlas'}) CREATE (p)-[:ACTED_IN {id: 'left-behind', role: 'Old Georgie', billing: 9}]->(m)");
            Assert.False(await WorkshopData.MatchesSeedAsync(connection, CancellationToken.None));
            var output = new StringWriter();
            var next = new WorkshopRunner(connection, new Presenter(output, color: false), SnapshotStore.Default());
            await next.RunAsync(WorkshopRunner.Lessons.Single(l => l.Info.Id == "16"), CancellationToken.None);
            Assert.Contains("differs from the seed", output.ToString());

            await WorkshopData.ResetAsync(connection, (_, _) => true, CancellationToken.None);
            await WorkshopData.EnsureSeedAsync(connection, CancellationToken.None);
            Assert.True(await WorkshopData.MatchesSeedAsync(connection, CancellationToken.None));
        }
        finally
        {
            await WorkshopData.ResetAsync(connection, (_, _) => true, CancellationToken.None);
        }
    }

    [Neo4jIntegrationFact]
    public async Task Safety_guard_refuses_foreign_data_and_never_touches_unowned_workshop_labels()
    {
        await using var strict = Connection(allowOtherData: false);
        await using var permissive = Connection(allowOtherData: true);
        await Execute("MATCH (n) WHERE n:Person OR n:Movie OR n:Review DETACH DELETE n");
        await Execute("MATCH (m:GraphBridgeWorkshop) DELETE m");
        try
        {
            await Execute("CREATE (:WorkshopGuardProbe {id: 1})");
            await Assert.ThrowsAsync<WorkshopConfigurationException>(() => WorkshopData.GuardAsync(strict, CancellationToken.None));
            await WorkshopData.GuardAsync(permissive, CancellationToken.None);

            // A Person node the workshop did not create: refused even with --allow-other-data, and reset deletes nothing.
            await Execute("MATCH (m:GraphBridgeWorkshop) DELETE m");
            await Execute("CREATE (:Person {id: 'not-the-workshop'})");
            await Assert.ThrowsAsync<WorkshopConfigurationException>(() => WorkshopData.GuardAsync(permissive, CancellationToken.None));
            await Assert.ThrowsAsync<WorkshopConfigurationException>(() => WorkshopData.ResetAsync(permissive, (_, _) => true, CancellationToken.None));
            Assert.Equal(1L, await Count("MATCH (p:Person {id: 'not-the-workshop'}) RETURN count(p) AS value"));

            // An unconfirmed reset deletes nothing.
            await Execute("MATCH (p:Person {id: 'not-the-workshop'}) DELETE p");
            await WorkshopData.GuardAsync(permissive, CancellationToken.None);
            await Execute("CREATE (:Movie {id: 'owned-by-workshop-now'})");
            Assert.False(await WorkshopData.ResetAsync(permissive, (_, _) => false, CancellationToken.None));
            Assert.Equal(1L, await Count("MATCH (m:Movie {id: 'owned-by-workshop-now'}) RETURN count(m) AS value"));
        }
        finally
        {
            await Execute("MATCH (n:WorkshopGuardProbe) DELETE n");
            await Execute("MATCH (n) WHERE n:Person OR n:Movie OR n:Review DETACH DELETE n");
            await Execute("MATCH (m:GraphBridgeWorkshop) DELETE m");
        }
    }

    private WorkshopConnection Connection(bool allowOtherData)
        => WorkshopConnection.ForDriver(fixture.Driver, new Uri(Environment.GetEnvironmentVariable("GRAPHBRIDGE_NEO4J_URI")!),
            fixture.Database, new(AllowOtherData: allowOtherData));

    private async Task Execute(string cypher)
    {
        await using var session = fixture.OpenSession();
        await (await session.RunAsync(cypher)).ConsumeAsync();
    }

    private async Task<long> Count(string cypher)
    {
        await using var session = fixture.OpenSession();
        return (await (await session.RunAsync(cypher)).SingleAsync())["value"].As<long>();
    }
}
