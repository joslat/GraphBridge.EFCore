using Neo4j.Driver;

namespace GraphBridge.Workshop;

/// <summary>Finds lessons and runs them with their prerequisites, presentation and snapshot checks.</summary>
public sealed class WorkshopRunner(WorkshopConnection? connection, Presenter presenter, SnapshotStore snapshots)
{
    private bool _guarded, _schema, _seeded;

    public static IReadOnlyList<ILesson> Lessons { get; } = typeof(WorkshopRunner).Assembly.GetTypes()
        .Where(t => typeof(ILesson).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
        .Select(t => (ILesson)Activator.CreateInstance(t)!)
        .OrderBy(l => l.Info.Id, StringComparer.Ordinal).ToArray();

    public static IReadOnlyList<ILesson> CoreTrack => Lessons.Where(l => l.Info.Core).ToArray();

    public async Task<LessonOutcome> RunAsync(ILesson lesson, CancellationToken cancellationToken)
    {
        var outcome = new LessonOutcome(lesson.Info.Id);
        presenter.LessonHeader(lesson.Info, connection is not null);
        if (lesson.Info.Mode == LessonMode.Connected && connection is null)
        {
            outcome.Skip(LessonOutcome.NeedsDatabase);
            presenter.Note("This lesson needs Neo4j. Set GRAPHBRIDGE_WORKSHOP_URI and GRAPHBRIDGE_WORKSHOP_PASSWORD (see the workshop README).");
            presenter.Summary(outcome);
            return outcome;
        }
        try
        {
            if (connection is not null) await PrepareAsync(lesson.Info.Requires, cancellationToken);
            await lesson.RunAsync(new LessonContext(lesson.Info, connection, presenter, outcome), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (WorkshopConfigurationException) { throw; }
        catch (Exception error) when (ConnectionProblem(error) is { } problem)
        {
            // Every later lesson would fail the same way, so stop with the fix instead of repeating the error.
            throw new WorkshopConnectionException(problem, error);
        }
        catch (Exception error)
        {
            // EF wraps provider failures ("see the inner exception"), so show the root cause too.
            var root = error.GetBaseException();
            var detail = $"{error.GetType().Name}: {error.Message}" + (root == error ? "" : $" → {root.GetType().Name}: {root.Message}");
            var checkpoint = new Checkpoint("lesson completed without an unexpected error", false, detail);
            outcome.Add(checkpoint);
            presenter.Checkpoint(checkpoint);
        }
        foreach (var checkpoint in snapshots.Verify(outcome))
        {
            outcome.Add(checkpoint);
            presenter.Checkpoint(checkpoint);
        }
        presenter.Summary(outcome);
        if (!outcome.Passed && connection is not null && lesson.Info.Mode != LessonMode.Offline)
            presenter.Note("If an earlier run was interrupted or you changed the lesson's code, run reset (R in the menu) and try again.");
        return outcome;
    }

    private string? ConnectionProblem(Exception error)
    {
        if (connection is null) return null;
        var chain = new List<Exception>();
        for (var current = error; current is not null; current = current.InnerException) chain.Add(current);
        if (chain.OfType<AuthenticationException>().Any())
            return $"Neo4j at {connection.Uri} rejected the user name or password. Check GRAPHBRIDGE_WORKSHOP_PASSWORD, and GRAPHBRIDGE_WORKSHOP_USERNAME if it is not neo4j.";
        if (chain.OfType<ServiceUnavailableException>().Any())
            return $"Neo4j is not reachable at {connection.Uri}. Start the database (see 'Get a database' in the workshop README) and check the port, or add --offline to run only the offline parts.";
        if (chain.OfType<FatalDiscoveryException>().Any())
            return $"Neo4j at {connection.Uri} has no database '{connection.Database}'. Unset GRAPHBRIDGE_WORKSHOP_DATABASE to use the default database, neo4j.";
        return null;
    }

    private async Task PrepareAsync(Requirement requirement, CancellationToken cancellationToken)
    {
        if (requirement == Requirement.None) return;
        if (!_guarded) { await WorkshopData.GuardAsync(connection!, cancellationToken); _guarded = true; }
        if (requirement >= Requirement.Schema && !_schema)
        {
            var applied = await WorkshopData.EnsureSchemaAsync(connection!, cancellationToken);
            if (applied.Count > 0) presenter.Note($"Prepared {applied.Count} reviewed uniqueness constraints (lesson 03 explains this step).");
            _schema = true;
        }
        if (requirement >= Requirement.Seed && !_seeded)
        {
            if (await WorkshopData.EnsureSeedAsync(connection!, cancellationToken))
                presenter.Note("Loaded the workshop movie graph through EF SaveChangesAsync.");
            else if (!await WorkshopData.MatchesSeedAsync(connection!, cancellationToken))
                presenter.Warning("The movie graph differs from the seed, usually after an interrupted lesson or an experiment. "
                    + "Lessons expect the seed: run reset (R in the menu), then run the lesson again.");
            _seeded = true;
        }
    }

    /// <summary>Lessons can change data the seed relies on; a reset makes the next preparation reload it.</summary>
    public void ForgetPreparation() => (_schema, _seeded) = (false, false);
}

/// <summary>Reviewed generated Cypher. A lesson's Cypher must match its snapshot, so changes are always reviewed.</summary>
public sealed class SnapshotStore(string directory, string? updateDirectory = null)
{
    public static SnapshotStore Default(string? updateDirectory = null)
        => new(Path.Combine(AppContext.BaseDirectory, "snapshots"), updateDirectory);

    public IEnumerable<Checkpoint> Verify(LessonOutcome outcome)
    {
        foreach (var (name, text) in outcome.Cypher)
        {
            var file = $"{outcome.LessonId}-{name}.cypher";
            var actual = Normalize(text);
            if (updateDirectory is not null)
            {
                Directory.CreateDirectory(updateDirectory);
                File.WriteAllText(Path.Combine(updateDirectory, file), actual + "\n");
                yield return new($"snapshot {file} written for review", true, null);
                continue;
            }
            var path = Path.Combine(directory, file);
            if (!File.Exists(path))
                yield return new($"Cypher '{name}' has a reviewed snapshot", false, $"missing snapshots/{file}");
            else if (Normalize(File.ReadAllText(path)) != actual)
                yield return new($"Cypher '{name}' matches snapshots/{file}", false, "generated Cypher changed; review it and update the snapshot");
            else
                yield return new($"Cypher '{name}' matches its reviewed snapshot", true, null);
        }
    }

    private static string Normalize(string text) => text.Replace("\r", "").TrimEnd();
}
