using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

if (args.Length != 2) throw new ArgumentException("Usage: AllocationReport TRACE.nettrace OUTPUT_DIRECTORY");
var tracePath = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new InvalidOperationException("Report output must be empty.");
Directory.CreateDirectory(output);
long lost = 0;
var converted = TraceLog.CreateFromEventPipeDataFile(tracePath, options: new TraceLogOptions
{
    OnLostEvents = (_, count, _) => lost += count
});
using var trace = new TraceLog(converted);
var stacks = new Dictionary<string, (string[] Frames, long Bytes, int Ticks)>(StringComparer.Ordinal);
var methods = new Dictionary<string, long>(StringComparer.Ordinal);
var types = new Dictionary<string, long>(StringComparer.Ordinal);
var frameIndices = new Dictionary<string, int>(StringComparer.Ordinal);
var frames = new List<object>();
var samples = new List<int[]>();
var weights = new List<long>();
var windows = new List<object>();
bool inWarm = false;
int pid = -1, iteration = -1;
double start = 0;
string backend = "";
long total = 0, withoutStack = 0;
foreach (var data in trace.Events)
{
    if (data.ProviderName == "GraphBridge-AllocationProfile")
    {
        if ((int)data.ID == 1)
        {
            if (inWarm) throw new InvalidOperationException("Overlapping profiling windows.");
            if ((string)data.PayloadByName("phase") != "warm") continue;
            inWarm = true;
            pid = data.ProcessID;
            backend = (string)data.PayloadByName("backend");
            iteration = (int)data.PayloadByName("iteration");
            start = data.TimeStampRelativeMSec;
        }
        else if ((int)data.ID == 2 && inWarm)
        {
            if (data.ProcessID != pid || (int)data.PayloadByName("iteration") != iteration
                || (string)data.PayloadByName("backend") != backend) throw new InvalidOperationException("Mismatched profiling window.");
            windows.Add(new { Backend = backend, Iteration = iteration, StartMilliseconds = start, EndMilliseconds = data.TimeStampRelativeMSec });
            inWarm = false;
        }
        continue;
    }
    if (!inWarm || data.ProcessID != pid || data is not GCAllocationTickTraceData allocation) continue;
    var bytes = checked((long)allocation.AllocationAmount64);
    if (bytes <= 0) throw new InvalidOperationException("Allocation sample has no weight.");
    var chain = new List<string>();
    for (var stack = data.CallStack(); stack is not null; stack = stack.Caller)
    {
        var name = stack.CodeAddress.FullMethodName;
        chain.Add(string.IsNullOrEmpty(name) ? "[unresolved]" : name);
    }
    if (chain.Count == 0) { chain.Add("[missing stack]"); withoutStack += bytes; }
    chain.Reverse();
    var type = allocation.TypeName ?? "[unknown type]";
    foreach (var method in chain.Distinct(StringComparer.Ordinal)) methods[method] = methods.GetValueOrDefault(method) + bytes;
    chain.Add("allocated type: " + type);
    types[type] = types.GetValueOrDefault(type) + bytes;
    var key = string.Join("\n", chain);
    var previous = stacks.GetValueOrDefault(key);
    stacks[key] = (chain.ToArray(), previous.Bytes + bytes, previous.Ticks + 1);
    var indices = new List<int>();
    foreach (var name in chain)
    {
        if (!frameIndices.TryGetValue(name, out var index))
        {
            index = frames.Count;
            frameIndices[name] = index;
            frames.Add(new { name });
        }
        indices.Add(index);
    }
    samples.Add(indices.ToArray());
    weights.Add(bytes);
    total += bytes;
}
if (inWarm || windows.Count == 0 || samples.Count == 0 || lost != 0) throw new InvalidOperationException("Incomplete/empty trace or lost events; no accepted allocation report.");
var json = new JsonSerializerOptions { WriteIndented = true };
var ranked = stacks.Values.OrderByDescending(s => s.Bytes).Select(s => new { s.Bytes, s.Ticks, s.Frames }).ToArray();
await File.WriteAllTextAsync(Path.Combine(output, "allocation-report.json"), JsonSerializer.Serialize(new
{
    TraceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(tracePath))).ToLowerInvariant(),
    Backend = backend, WarmWindows = windows, SampledEstimatedBytes = total, AllocationTicks = samples.Count,
    BytesWithoutStack = withoutStack, LostEvents = lost,
    Scope = "Process-wide GC allocation ticks during warm read windows only. Sample weights estimate bytes; not exact object/type counts. Inclusive methods overlap. Startup, seed and warmup excluded.",
    TopStacks = ranked.Take(50), InclusiveMethods = methods.OrderByDescending(p => p.Value).Take(100),
    AllocatedTypes = types.OrderByDescending(p => p.Value).Take(50)
}, json));
var speedscope = new Dictionary<string, object>
{
    ["$schema"] = "https://www.speedscope.app/file-format-schema.json",
    ["shared"] = new { frames },
    ["profiles"] = new[] { new { type = "sampled", name = backend + " warm allocation samples", unit = "bytes", startValue = 0L, endValue = total, samples, weights } },
    ["activeProfileIndex"] = 0,
    ["exporter"] = "GraphBridge allocation report / TraceEvent 3.2.6"
};
await File.WriteAllTextAsync(Path.Combine(output, "allocations.speedscope.json"), JsonSerializer.Serialize(speedscope));
using var tree = new StreamWriter(Path.Combine(output, "top-allocation-stacks.txt"));
foreach (var stack in ranked.Take(5))
{
    await tree.WriteLineAsync($"{stack.Bytes / 1048576d:F2} estimated MiB / {stack.Ticks} allocation ticks (root to allocation):");
    foreach (var frame in stack.Frames) await tree.WriteLineAsync("  " + frame);
    await tree.WriteLineAsync();
}
Console.WriteLine($"Accepted {windows.Count} warm windows, {samples.Count} allocation ticks, {total / 1048576d:F2} estimated MiB; lost events: {lost}.");
