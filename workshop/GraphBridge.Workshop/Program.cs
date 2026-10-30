using System.Text;
using GraphBridge.Workshop;
using Neo4j.Driver;

// GraphBridge workshop: a menu of lessons, one topic each. See README.md for setup and the lesson scripts.
Console.OutputEncoding = Encoding.UTF8;
var arguments = args.ToList();
var presenter = Presenter.ForConsole();
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

try
{
    var options = new WorkshopOptions(Take("--allow-remote"), Take("--allow-other-data"));
    var offline = Take("--offline");
    var update = TakeValue("--update-snapshots");
    var command = arguments.FirstOrDefault() ?? (Console.IsInputRedirected ? "list" : "menu");
    // Only commands that can use a database read the connection settings; list and check never do.
    await using var connection = offline || command is not ("run" or "reset" or "menu") ? null : WorkshopConnection.FromEnvironment(options);
    using var exclusive = connection is null ? null : WorkshopLock.Acquire(connection.Uri, connection.Database);
    var runner = new WorkshopRunner(connection, presenter, SnapshotStore.Default(update));
    return command switch
    {
        "list" => List(),
        "run" => await RunAsync(runner, Select(arguments.Skip(1).ToArray())),
        "check" => await RunAsync(runner, WorkshopRunner.Lessons),
        "reset" => await ResetAsync(connection, arguments.Contains("--confirm")),
        "menu" => await MenuAsync(runner, connection),
        _ => Usage($"Unknown command '{command}'.")
    };
}
catch (WorkshopConnectionException error)
{
    Console.Error.WriteLine(presenter.Paint("Cannot connect: ", "\u001b[31m") + error.Message);
    Console.Error.WriteLine($"  Driver: {error.InnerException?.GetBaseException().Message}");
    return 2;
}
catch (WorkshopConfigurationException error)
{
    Console.Error.WriteLine(presenter.Paint("Refused: ", "\u001b[31m") + error.Message);
    return 2;
}
catch (Neo4jException error)
{
    // Lessons record their own failures; this covers reset and the safety guard outside a lesson.
    Console.Error.WriteLine(presenter.Paint("Neo4j: ", "\u001b[31m") + $"{error.GetType().Name}: {error.Message}");
    return 3;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.Error.WriteLine("Canceled.");
    return 130;
}

bool Take(string flag) => arguments.Remove(flag);
string? TakeValue(string flag)
{
    var index = arguments.IndexOf(flag);
    if (index < 0) return null;
    if (index + 1 >= arguments.Count || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
        throw new WorkshopConfigurationException($"{flag} needs a directory, for example {flag} snapshots-new.");
    var value = arguments[index + 1];
    arguments.RemoveRange(index, 2);
    return value;
}

IReadOnlyList<ILesson> Select(string[] selection)
{
    if (selection is ["--track", "core"]) return WorkshopRunner.CoreTrack;
    if (selection is [] or ["--track", "full"]) return WorkshopRunner.Lessons;
    if (selection.Contains("--track")) throw new WorkshopConfigurationException("Use run --track core or run --track full.");
    if (selection.FirstOrDefault(s => s.StartsWith('-')) is { } unknown)
        throw new WorkshopConfigurationException($"Unknown option {unknown}. Options: --offline, --allow-remote, --allow-other-data, --update-snapshots <dir>.");
    var lessons = selection.Select(id => WorkshopRunner.Lessons.FirstOrDefault(l => l.Info.Id == id.PadLeft(2, '0'))
        ?? throw new WorkshopConfigurationException($"There is no lesson {id}. Use 'list' to see the lessons.")).ToArray();
    return lessons;
}

async Task<int> RunAsync(WorkshopRunner runner, IReadOnlyList<ILesson> lessons)
{
    var outcomes = new List<LessonOutcome>();
    foreach (var lesson in lessons) outcomes.Add(await runner.RunAsync(lesson, cancellation.Token));
    var failed = outcomes.Where(o => !o.Passed).Select(o => o.LessonId).ToArray();
    string Ids(Func<LessonOutcome, bool> select) => string.Join(", ", outcomes.Where(o => o.Passed && select(o)).Select(o => o.LessonId));
    var offlineOnly = Ids(o => o.Skipped == LessonOutcome.NeedsDatabase && o.Checkpoints.Count > 0);
    var notRun = Ids(o => o.Skipped == LessonOutcome.NeedsDatabase && o.Checkpoints.Count == 0);
    var other = string.Join("; ", outcomes.Where(o => o.Skipped is { } reason && reason != LessonOutcome.NeedsDatabase)
        .Select(o => $"{o.LessonId} ({o.Skipped})"));
    if (outcomes.Count > 1)
    {
        var ran = outcomes.Count(o => o.Passed && !(o.Skipped == LessonOutcome.NeedsDatabase && o.Checkpoints.Count == 0));
        Console.WriteLine();
        Console.WriteLine($"{ran}/{outcomes.Count} lessons passed" +
            (failed.Length > 0 ? $"; failed: {string.Join(", ", failed)}" : "") +
            (offlineOnly.Length > 0 ? $"; offline part only: {offlineOnly}" : "") +
            (notRun.Length > 0 ? $"; not run without a database: {notRun}" : "") +
            (other.Length > 0 ? $"; partly skipped: {other}" : ""));
    }
    return failed.Length > 0 ? 1 : 0;
}

int List()
{
    foreach (var part in WorkshopRunner.Lessons.GroupBy(l => l.Info.Part))
    {
        Console.WriteLine();
        Console.WriteLine($" {part.Key} · {PartName(part.Key)}");
        foreach (var lesson in part)
        {
            var mode = lesson.Info.Mode switch { LessonMode.Offline => "◇ ", LessonMode.Connected => "● ", _ => "◇●" };
            Console.WriteLine($"   {(lesson.Info.Core ? "★" : " ")} {lesson.Info.Id}  {mode}  {lesson.Info.Title}");
        }
    }
    Console.WriteLine();
    Console.WriteLine(" ★ core track   ◇ offline   ● needs Neo4j");
    return 0;
}

async Task<int> ResetAsync(WorkshopConnection? connection, bool confirmed)
{
    if (connection is null) throw new WorkshopConfigurationException("Reset needs a database. Set GRAPHBRIDGE_WORKSHOP_URI and GRAPHBRIDGE_WORKSHOP_PASSWORD.");
    var done = await WorkshopData.ResetAsync(connection, (nodes, relationships) =>
    {
        Console.WriteLine($"Reset deletes {nodes} Person/Movie/Review nodes and their {relationships} relationships in '{connection.Database}'. Constraints stay. GraphRAG lessons (31 to 35) remove their own Workshop* nodes and schema when they end; after a stopped one, run it again to clean up.");
        if (confirmed) return true;
        if (Console.IsInputRedirected) { Console.WriteLine("Not confirmed. Pass --confirm in scripts."); return false; }
        Console.Write("Type reset to confirm: ");
        return Console.ReadLine()?.Trim() == "reset";
    }, cancellation.Token);
    Console.WriteLine(done ? "Workshop data deleted. The next lesson reloads the movie graph." : "Nothing was deleted.");
    return done ? 0 : 1;
}

async Task<int> MenuAsync(WorkshopRunner runner, WorkshopConnection? connection)
{
    Console.WriteLine(connection is null
        ? "GraphBridge workshop · offline (set GRAPHBRIDGE_WORKSHOP_URI and GRAPHBRIDGE_WORKSHOP_PASSWORD for Neo4j lessons)"
        : $"GraphBridge workshop · {connection.Uri} · database '{connection.Database}'");
    while (true)
    {
        List();
        Console.Write("Lesson number, C for the core track, R to reset, Q to quit: ");
        var input = Console.ReadLine()?.Trim();
        if (input is null || input.Equals("q", StringComparison.OrdinalIgnoreCase)) return 0;
        var lesson = WorkshopRunner.Lessons.FirstOrDefault(l => l.Info.Id == input.PadLeft(2, '0'));
        try
        {
            if (input.Equals("c", StringComparison.OrdinalIgnoreCase)) await RunAsync(runner, WorkshopRunner.CoreTrack);
            else if (input.Equals("r", StringComparison.OrdinalIgnoreCase))
            {
                if (connection is null) Console.WriteLine("Reset needs a database. Set GRAPHBRIDGE_WORKSHOP_URI and GRAPHBRIDGE_WORKSHOP_PASSWORD, then restart the workshop.");
                else if (await ResetAsync(connection, confirmed: false) == 0) runner.ForgetPreparation();
            }
            else if (lesson is not null) await runner.RunAsync(lesson, cancellation.Token);
            else Console.WriteLine($"Unknown choice '{input}'.");
        }
        catch (Exception error) when (error is WorkshopConfigurationException or Neo4jException)
        {
            // Stay in the menu: the learner can fix the database or settings and choose again.
            Console.WriteLine(presenter.Paint(error is WorkshopConnectionException ? "Cannot connect: " : "Refused: ", "\u001b[31m") + error.Message);
        }
        Console.WriteLine();
        Console.Write("Press Enter for the menu…");
        if (Console.ReadLine() is null) return 0;
    }
}

static string PartName(char part) => part switch
{
    'A' => "Foundations", 'B' => "Relationships", 'C' => "Correctness", 'D' => "Graph-native querying",
    'E' => "Operating the graph", 'F' => "Developer extensions", 'G' => "GraphRAG (experimental)", _ => "Other"
};

static int Usage(string? problem = null)
{
    if (problem is not null) Console.Error.WriteLine(problem);
    Console.Error.WriteLine("""
        Usage: dotnet run --project workshop/GraphBridge.Workshop -- [command] [options]
          (none)                 interactive menu
          list                   lessons by part
          run <id>...            run lessons, e.g. run 01 08
          run --track core|full  run a track in order
          check                  run every lesson offline and compare generated Cypher with reviewed snapshots
          reset [--confirm]      delete workshop data after confirmation
        Options: --offline  --allow-remote  --allow-other-data  --update-snapshots <dir>
        """);
    return 2;
}
