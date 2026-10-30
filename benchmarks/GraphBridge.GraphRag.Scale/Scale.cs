using System.Runtime.InteropServices;
using System.Text;
using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.GraphRag;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.GraphRag.Scale;

/// <summary>The harness's own layout: labels with a run suffix, so it never touches other data in the database.</summary>
public static class Scale
{
    public static string Suffix { get; } = Guid.NewGuid().ToString("N")[..8];
    public static int Dimensions { get; set; } = 256;

    public static GraphRagLayout Layout { get; } = GraphRagLayout.Default with
    {
        DocumentLabel = "ScaleDocument_" + Suffix, SectionLabel = "ScaleSection_" + Suffix, ChunkLabel = "ScaleChunk_" + Suffix,
        RunLabel = "ScaleRun_" + Suffix, LeaseLabel = "ScaleLease_" + Suffix, SchemaPrefix = "gbscale_" + Suffix
    };

    /// <summary>Runs schema steps as rendered, then waits until their indexes are online.</summary>
    public static async Task CreateAsync(IDriver driver, string database, IEnumerable<GraphMigrationStep> steps)
    {
        await using var session = driver.AsyncSession(o => o.WithDatabase(database));
        foreach (var step in steps)
        {
            await (await session.RunAsync(GraphMigrationStepRenderer.Render(step).Cypher)).ConsumeAsync();
            if (step.Schema is { IsIndex: true } index)
                await (await session.RunAsync("CALL db.awaitIndex($index, 300)", new { index = index.Name })).ConsumeAsync();
        }
    }

    /// <summary>Drops the run's constraints and indexes, including the re-embedding's staging index.</summary>
    public static async Task DropAsync(IDriver driver, string database, GraphRagEmbeddingSet set)
    {
        await using var session = driver.AsyncSession(o => o.WithDatabase(database));
        var staging = GraphRagReembedding.Plan(Layout, set, new GraphRagEmbeddingSet("graphbridge-sample-hashing-128", 128), "EmbeddingNext");
        var names = GraphRagSchema.Definitions(Layout, set).Select(d => (d.Name, d.IsIndex))
            .Concat(staging.PrepareSteps.Where(s => s.Schema is not null).Select(s => (s.Schema!.Name, IsIndex: true)));
        foreach (var (name, isIndex) in names)
            await (await session.RunAsync($"DROP {(isIndex ? "INDEX" : "CONSTRAINT")} `{name}` IF EXISTS")).ConsumeAsync();
        await (await session.RunAsync($"MATCH (n:`{Layout.RunLabel}`) DELETE n")).ConsumeAsync();
    }

    /// <summary>The repository root, found from the harness's output folder.</summary>
    public static string Repository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GraphBridge.sln"))) directory = directory.Parent;
        return directory?.FullName ?? Environment.CurrentDirectory;
    }
}

public sealed class ScaleContext(DbContextOptions<ScaleContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddGraphRag(Scale.Dimensions, o => o.Layout = Scale.Layout);
}

/// <summary>Stops the run when free memory falls below the 5 GiB reserve another developer's work on this machine needs.</summary>
public static class Memory
{
    private const double ReserveGiB = 5;

    public static void Require(string when)
    {
        var free = FreeGiB();
        Console.WriteLine($"[{when}] free memory {free:0.0} GiB");
        if (free is { } value && value < ReserveGiB)
            throw new InvalidOperationException($"Free memory is {value:0.0} GiB, below the {ReserveGiB} GiB reserve; stopping at '{when}'.");
    }

    // The operating system's available physical memory now: GlobalMemoryStatusEx on Windows, MemAvailable on Linux.
    private static double? FreeGiB()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            return GlobalMemoryStatusEx(ref status) ? status.AvailablePhysical / (double)(1L << 30) : null;
        }
        if (OperatingSystem.IsLinux() && File.Exists("/proc/meminfo"))
        {
            var line = File.ReadLines("/proc/meminfo").FirstOrDefault(l => l.StartsWith("MemAvailable:", StringComparison.Ordinal));
            if (line is not null && long.TryParse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], out var kib)) return kib / (double)(1 << 20);
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}

/// <summary>
/// A deterministic corpus: Markdown documents with a title, four sections and plain sentences over a made-up vocabulary.
/// About 5 KB per document, so 2,000 documents are about 10 MB and, at the default 2 KB passages, about 20,000 chunks.
/// </summary>
public static class GeneratedCorpus
{
    private static readonly string[] Syllables = ["ka", "lo", "mi", "ne", "ru", "sa", "to", "vi", "be", "da", "fo", "gu", "ha", "ji", "pe", "zo"];

    private static string Word(Random random) => string.Concat(Enumerable.Range(0, random.Next(2, 4)).Select(_ => Syllables[random.Next(Syllables.Length)]));

    /// <summary>The UTF-8 bytes of the last corpus created.</summary>
    public static long TotalBytes { get; private set; }

    public static IReadOnlyList<GraphRagSource> Create(int documents)
    {
        var random = new Random(20261026);
        TotalBytes = 0;
        var sources = new GraphRagSource[documents];
        for (var d = 0; d < documents; d++)
        {
            var text = new StringBuilder("# Report ").Append(d).Append(' ').Append(Word(random)).Append("\n\n");
            for (var s = 0; s < 4; s++)
            {
                text.Append("## Part ").Append(s + 1).Append(' ').Append(Word(random)).Append("\n\n");
                for (var p = 0; p < 3; p++)
                {
                    for (var w = 0; w < 7; w++)
                        text.Append(string.Join(' ', Enumerable.Range(0, random.Next(6, 12)).Select(_ => Word(random)))).Append(". ");
                    text.Append("\n\n");
                }
            }
            TotalBytes += Encoding.UTF8.GetByteCount(text.ToString());
            sources[d] = GraphRagSources.Text($"scale-{d:D5}", text.ToString(), "text/markdown");
        }
        return sources;
    }

    public static string[] Queries(int count)
    {
        var random = new Random(7);
        return [.. Enumerable.Range(0, count).Select(_ => string.Join(' ', Enumerable.Range(0, random.Next(3, 7)).Select(_ => Word(random))))];
    }
}
