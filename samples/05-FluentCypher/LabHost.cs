using System.Text;
using System.Text.Json;
using GraphBridge.Tooling.Diagnostics;
using Neo4j.Driver;

namespace GraphBridge.Samples.FluentCypher;

/// <summary>One-shot application-owned sample adapter, not an assembly loader or query executor service.</summary>
internal static class LabHost
{
    public static async Task<int> RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        string? requestId = null;
        try
        {
            using var input = Console.OpenStandardInput();
            var bytes = new byte[4097];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = await input.ReadAsync(bytes.AsMemory(length), deadline.Token);
                if (read == 0) break;
                length += read;
            }
            if (length > 4096) return 2;
            using var request = JsonDocument.Parse(bytes.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 8 });
            var root = request.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return 2;
            var expected = new HashSet<string>(["formatVersion", "requestId", "operation", "includeStatements", "acknowledgeDatabase", "acknowledgeDisposable"], StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject()) if (!expected.Remove(property.Name)) return 2;
            if (expected.Count != 0 || root.GetProperty("formatVersion").GetInt32() != 1) return 2;
            requestId = root.GetProperty("requestId").GetString();
            if (!Guid.TryParseExact(requestId, "N", out var id) || id == Guid.Empty) return 2;
            var operation = root.GetProperty("operation").GetString();
            var disclose = root.GetProperty("includeStatements").GetBoolean();
            var acknowledgeDatabase = root.GetProperty("acknowledgeDatabase").GetBoolean();
            var acknowledgeDisposable = root.GetProperty("acknowledgeDisposable").GetBoolean();
            if (operation is not ("offline" or "inspect" or "semantic")
                || operation != "offline" && !acknowledgeDatabase || operation == "semantic" && !acknowledgeDisposable)
                return await ErrorAsync(requestId, "invalid-request");
            SampleEnvironment settings;
            try { settings = SampleEnvironment.Read(operation != "offline", operation == "semantic"); }
            catch (InvalidOperationException) { return await ErrorAsync(requestId, "configuration"); }
            using var driver = GraphDatabase.Driver(settings.Uri, settings.Auth, c => c.WithConnectionTimeout(TimeSpan.FromSeconds(10)));
            using var context = new InspectionContext(driver, settings.Database);
            string report;
            if (operation == "semantic")
            {
                var result = await GraphVerify.SemanticAsync(() => new InspectionContext(driver, settings.Database),
                    FluentMovieContracts.Create(settings.RunId!, afterYear: 2000), "sample.disposable.v1", settings.Fixture!,
                    FluentMovieReferences.Create(settings.RunId!), includeStatements: disclose, cancellationToken: deadline.Token);
                report = result.ToJson();
            }
            else if (operation == "inspect")
                report = (await GraphVerify.InspectAsync(context, FluentMovieContracts.Create("inspection-run"),
                    "sample.configured.v1", includeStatements: disclose, cancellationToken: deadline.Token)).ToJson();
            else report = GraphVerify.Offline(context, FluentMovieContracts.Create("inspection-run"), disclose, deadline.Token).ToJson();
            using var parsed = JsonDocument.Parse(report);
            // A failed V2 may have no inspection/queries; excerpts must match actual retained query IDs.
            var evidence = operation == "semantic"
                ? parsed.RootElement.TryGetProperty("inspection", out var inspection) ? inspection.GetProperty("evidence") : default
                : parsed.RootElement.GetProperty("evidence");
            var ids = evidence.ValueKind == JsonValueKind.Object
                ? evidence.GetProperty("queries").EnumerateArray().Select(q => q.GetProperty("contractId").GetString()).ToHashSet()
                : [];
            var response = JsonSerializer.Serialize(new {
                formatVersion = 1, requestId, result = new {
                    kind = "graphbridge-query-lab", formatVersion = 1, report = parsed.RootElement,
                    sources = disclose ? FluentMovieSources.Create().Where(s => ids.Contains(s.ContractId))
                        .Select(s => new { contractId = s.ContractId, text = s.Text }).ToArray() : []
                }
            });
            if (Encoding.UTF8.GetByteCount(response) > 4_000_000) return await ErrorAsync(requestId, "output-limit");
            await Console.Out.WriteAsync(response);
            return 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return requestId is null ? 2 : await ErrorAsync(requestId, "capture-failed");
        }
    }

    private static async Task<int> ErrorAsync(string requestId, string error)
    {
        await Console.Out.WriteAsync(JsonSerializer.Serialize(new { formatVersion = 1, requestId, error }));
        return 0;
    }
}
