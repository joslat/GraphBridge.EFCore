using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

if (args is ["--child"]) { await Task.Delay(Timeout.Infinite); return; }
var json = await Console.In.ReadToEndAsync();
var mode = Environment.GetEnvironmentVariable("GRAPHBRIDGE_FAKE_MODE");
if (mode == "hang")
{
    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add("--child");
    using var child = Process.Start(start)!;
    File.WriteAllText(Environment.GetEnvironmentVariable("GRAPHBRIDGE_FAKE_PID")!, child.Id.ToString());
    await Task.Delay(Timeout.Infinite);
}
else if (mode == "overflow") await Console.Out.WriteAsync(new string('x', 4_000_001));
else if (mode == "stderr") { await Console.Error.WriteAsync(new string('s', 16_385)); await Task.Delay(Timeout.Infinite); }
else if (mode == "malformed") { await Console.Error.WriteAsync("SECRET-ERROR-CONTENT"); await Console.Out.WriteAsync("not-json"); }
else
{
    using var request = JsonDocument.Parse(json);
    using var result = JsonDocument.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("GRAPHBRIDGE_FAKE_REPORT")!));
    await Console.Out.WriteAsync(JsonSerializer.Serialize(new { formatVersion = 1,
        requestId = mode == "stale" ? new string('0', 32) : request.RootElement.GetProperty("requestId").GetString(), result = result.RootElement }));
}
