using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using GraphBridge.EntityFrameworkCore.FluentCypher;

namespace GraphBridge.Tooling.Diagnostics;

/// <summary>An explicitly disclosed source excerpt, displayed as data and never compiled or executed by Query Lab.</summary>
public sealed class GraphQuerySource
{
    public GraphQuerySource(string contractId, string text)
    {
        EvidenceIdentity.Validate(contractId);
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 32_768) throw new ArgumentException("Source excerpts allow at most 32,768 characters.", nameof(text));
        ContractId = contractId;
        Text = text.ReplaceLineEndings("\n");
        Fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Text)));
    }
    public string ContractId { get; }
    public string Text { get; }
    public string Fingerprint { get; }
}

/// <summary>Deterministic, self-contained HTML presentation of already captured evidence. Performs no query or file I/O.</summary>
/// <remarks>Optional source excerpts are an explicit disclosure supplied by the host, not independently verified source maps.
/// Reports contain no scripts, external resources or live controls. Save the returned HTML to share it. Statement disclosure
/// must have occurred during capture; rendering cannot recover redacted text. Query variables are not database result nodes.</remarks>
public static partial class GraphQueryLab
{
    private const int MaxHtmlCharacters = 4_000_000;
    private static readonly Lazy<string> Styles = new(() =>
    {
        using var stream = typeof(GraphQueryLab).Assembly.GetManifestResourceStream("GraphBridge.QueryLab.css")
            ?? throw new InvalidOperationException("Query Lab stylesheet is missing from the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    });

    public static string RenderHtml(GraphEvidenceReport report, IEnumerable<GraphQuerySource>? sources = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        return Render(new(report, null, null, report.Mode.ToString(), report.Status), sources);
    }
    public static string RenderHtml(GraphVerificationReport report, IEnumerable<GraphQuerySource>? sources = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        return Render(new(report.Evidence, report, null, report.Tier.ToString(), report.Status), sources);
    }
    public static string RenderHtml(GraphSemanticVerificationReport report, IEnumerable<GraphQuerySource>? sources = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        return Render(new(report.Inspection?.Evidence, report.Inspection, report, report.Tier, report.Status), sources);
    }

    private sealed record Input(GraphEvidenceReport? Evidence, GraphVerificationReport? Verification,
        GraphSemanticVerificationReport? Semantic, string Tier, GraphEvidenceStatus Status);

    private static string Render(Input input, IEnumerable<GraphQuerySource>? sources)
    {
        var supplied = (sources ?? []).Take(257).ToArray();
        var queries = input.Evidence?.Queries ?? [];
        if (supplied.Length > 256 || supplied.Any(s => s is null)
            || supplied.Select(s => s.ContractId).Distinct(StringComparer.Ordinal).Count() != supplied.Length
            || supplied.Any(s => !queries.Any(q => q.ContractId == s.ContractId)))
            throw new ArgumentException("Source excerpts must identify distinct queries in this captured report.", nameof(sources));
        if (queries.Any(q => q.Statement?.Length > 65_536))
            throw new ArgumentException("A query statement exceeds the 65,536-character report budget.");
        var excerpts = supplied.ToDictionary(s => s.ContractId, StringComparer.Ordinal);
        var html = new StringBuilder("<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; script-src 'none'; base-uri 'none'; form-action 'none'\">")
            .Append("<title>GraphBridge Query Lab</title><style>").Append(Styles.Value).Append("</style></head><body><main>")
            .Append("<header class=\"hero\"><p class=\"eyebrow\">GRAPHBRIDGE FOR NEO4J · QUERY LAB</p><h1>See the query.<br>Inspect the evidence.</h1>")
            .Append("<p class=\"lead\">C# source → generated Cypher → typed graph semantics → observed outcomes</p><div class=\"chips\">")
            .Append(Badge(input.Status)).Append("<span class=\"chip\">Capture: ").Append(H(input.Tier))
            .Append("</span><span class=\"chip\">").Append(N(queries.Count)).Append(" queries</span><span class=\"chip\">Offline HTML · no scripts</span></div></header>")
            .Append("<p class=\"notice\">This is saved evidence, not a live database view. Compilation, EXPLAIN and result comparisons prove different things. Opening this report executes no queries.</p>");
        if (input.Evidence?.SensitiveTextIncluded == true || supplied.Length != 0)
            html.Append("<p class=\"notice disclosure\"><strong>Explicit disclosure:</strong> mapped statement/snapshot text or source excerpts are included. Review before sharing. Captured parameter values and result values are excluded; host-supplied source may contain literals.</p>");
        RenderOverview(html, input);
        if (queries.Count == 0) html.Append("<p class=\"empty\">No compiled query snapshots were observed. Missing evidence is not success.</p>");
        else
        {
            html.Append("<nav aria-label=\"Queries\" class=\"query-nav\">");
            for (var i = 0; i < queries.Count; i++) html.Append("<a href=\"#query-").Append(N(i)).Append("\">").Append(H(queries[i].ContractId)).Append("</a>");
            html.Append("</nav>");
        }
        for (var i = 0; i < queries.Count; i++)
        {
            var query = queries[i];
            var key = "q" + N(i);
            var verification = input.Verification?.Contracts.SingleOrDefault(c => c.ContractId == query.ContractId);
            var semantics = input.Semantic?.Cases.SingleOrDefault(c => c.ContractId == query.ContractId);
            html.Append("<article class=\"query\" id=\"query-").Append(N(i)).Append("\"><header class=\"query-heading\"><p class=\"eyebrow\">QUERY ")
                .Append(N(i + 1)).Append("</p><h2>").Append(H(query.ContractId)).Append("</h2><p>").Append(H(query.Definition?.Description ?? "Captured query"))
                .Append("</p><div class=\"stages\"><span>Compile ").Append(Badge(query.Compilation)).Append("</span><span>Plan ")
                .Append(Badge(verification?.Planning ?? GraphEvidenceStatus.NotRun)).Append("</span><span>Semantics ")
                .Append(Badge(semantics?.Status ?? GraphEvidenceStatus.NotRun)).Append("</span></div></header><div class=\"query-grid\">");
            RenderSource(html, query, excerpts.GetValueOrDefault(query.ContractId));
            RenderCypher(html, query, key);
            html.Append("</div><section class=\"panel\"><h3><span class=\"number\">03</span> Graph &amp; clause scope</h3><p class=\"muted\">Variables and patterns from the compiler. These circles are not observed database nodes.</p>");
            RenderSemantics(html, query, key);
            html.Append("</section><section class=\"panel\"><h3><span class=\"number\">04</span> Evidence &amp; limits</h3>");
            RenderQueryEvidence(html, query, verification, semantics);
            html.Append("</section></article>");
            CheckSize(html);
        }
        html.Append("<footer>GraphBridge Query Lab · report format 1 · saved evidence only. Native result values, credentials and parameter values are not embedded. Save this HTML file to share it; use your browser’s print command for a paper/PDF copy.</footer></main></body></html>");
        CheckSize(html);
        return html.ToString();
    }

    private static void RenderOverview(StringBuilder html, Input input)
    {
        var evidence = input.Evidence;
        html.Append("<section class=\"overview\"><h2>Capture context</h2><dl class=\"facts\">");
        Fact(html, "Database observation", evidence?.DatabaseObservation.ToString() ?? "NotRun");
        Fact(html, "Server / edition", evidence?.Database is { } database ? database.ServerVersion + " / " + database.Edition : "Not observed");
        Fact(html, "Selected profile", evidence?.Database?.ProfileId ?? "Not observed");
        Fact(html, "Provider", evidence?.Versions.Provider ?? "Not observed");
        Fact(html, "Tooling", evidence?.Versions.Tooling ?? "Not observed");
        Fact(html, "EF Core / driver", evidence is null ? "Not observed" : evidence.Versions.EfCore + " / " + evidence.Versions.Driver);
        Fact(html, "Model fingerprint", evidence?.Model?.Fingerprint ?? "Not observed");
        if (input.Semantic is { } semantic)
        {
            Fact(html, "Fixture recipe", semantic.FixtureId + " · " + semantic.FixtureFingerprint);
            Fact(html, "Fixture marker observation", semantic.FixtureStatus.ToString());
            Fact(html, "Context / transaction cleanup", semantic.Cleanup.ToString());
            Fact(html, "Semantic run issue", semantic.Issue.ToString());
        }
        html.Append("</dl>");
        foreach (var diagnosis in (evidence?.Diagnostics ?? []).Concat(input.Semantic?.Diagnostics ?? [])) Diagnosis(html, diagnosis);
        if (evidence?.SearchIndexes is { } indexes)
        {
            html.Append("<table><caption>Declared search indexes</caption><thead><tr><th>Index</th><th>Kind</th><th>Outcome</th><th>Findings</th><th>Sampled / unusable</th></tr></thead><tbody>");
            foreach (var index in indexes)
                html.Append("<tr><td>").Append(H(index.Id)).Append("</td><td>").Append(H(index.Kind + " " + index.EntityKind.ToString().ToLowerInvariant()))
                    .Append("</td><td>").Append(Badge(index.Status)).Append("</td><td>").Append(H(index.Findings.Count == 0 ? "none" : string.Join(", ", index.Findings)))
                    .Append("</td><td>").Append(H(index.SampledValues is { } sampled ? sampled + " / " + (index.UnusableValues ?? 0) : "not sampled")).Append("</td></tr>");
            html.Append("</tbody></table>");
        }
        if (evidence is null && input.Semantic is { } absent)
        {
            html.Append("<table><caption>Required cases without compilation snapshots</caption><thead><tr><th>Contract</th><th>Outcome</th></tr></thead><tbody>");
            foreach (var item in absent.Cases) html.Append("<tr><td>").Append(H(item.ContractId)).Append("</td><td>").Append(Badge(item.Status)).Append("</td></tr>");
            html.Append("</tbody></table>");
        }
        html.Append("</section>");
    }

    private static void RenderSource(StringBuilder html, GraphQueryEvidence query, GraphQuerySource? source)
    {
        html.Append("<section class=\"panel\"><h3><span class=\"number\">01</span> Source &amp; contract</h3><p class=\"source-ref\">")
            .Append(H(query.Definition?.SourceReference ?? "Source reference not supplied")).Append("</p>");
        if (source is null) html.Append("<p class=\"empty\">No C# excerpt supplied. Query Lab does not infer or execute source code.</p>");
        else html.Append("<p class=\"muted\">Host-supplied excerpt · displayed as text</p><pre class=\"source-code\"><code>").Append(H(source.Text))
            .Append("</code></pre><details><summary>Source excerpt fingerprint</summary><p class=\"fingerprint\">").Append(H(source.Fingerprint)).Append("</p></details>");
        if (query.Definition is { } definition)
        {
            html.Append("<dl class=\"facts compact\">");
            Fact(html, "Comparison", definition.Comparison.ToString());
            Fact(html, "Duplicate policy", definition.Duplicates.ToString());
            Fact(html, "Declared row budget", N(definition.Budget.MaxRows));
            Fact(html, "Cooperative deadline", definition.Budget.Deadline.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s");
            html.Append("</dl><p class=\"muted\">Budgets do not bound server computation or individual payload size.</p>");
        }
        html.Append("</section>");
    }

    private static void RenderCypher(StringBuilder html, GraphQueryEvidence query, string key)
    {
        html.Append("<section class=\"panel\"><h3><span class=\"number\">02</span> Generated Cypher</h3>");
        if (query.Statement is not { } statement) html.Append("<p class=\"empty\">Statement text was not disclosed or compilation did not complete. Rendering cannot recover it.</p>");
        else
        {
            html.Append("<pre class=\"cypher-code\"><code>");
            var lines = statement.Split('\n');
            for (var line = 0; line < lines.Length; line++) html.Append("<span class=\"code-line\" id=\"").Append(key).Append("-line-").Append(N(line + 1))
                .Append("\" data-line=\"").Append(N(line + 1)).Append("\">").Append(H(lines[line])).Append("</span>");
            html.Append("</code></pre><details><summary>Copy / export query text</summary><p>Select the text below to copy it. Parameter values are deliberately absent.</p><textarea readonly aria-label=\"Copy generated Cypher\" rows=\"8\">")
                .Append(H(statement)).Append("</textarea></details>");
        }
        html.Append("<p class=\"muted\">Parameters: ").Append(N(query.ParameterCount)).Append(" · values excluded</p><p class=\"parameter-kinds\">");
        foreach (var group in query.Parameters.GroupBy(p => p.Kind).OrderBy(g => g.Key, StringComparer.Ordinal))
            html.Append("<span class=\"chip\">").Append(H(group.Key)).Append(" × ").Append(N(group.Count())).Append("</span>");
        html.Append("</p><details><summary>Statement fingerprint</summary><p class=\"fingerprint\">").Append(H(query.StatementFingerprint ?? "Not observed")).Append("</p></details></section>");
    }

    private static void RenderSemantics(StringBuilder html, GraphQueryEvidence query, string key)
    {
        if (query.Semantics is not { } snapshot)
        { html.Append("<p class=\"empty\">Typed snapshot not captured/disclosed. No graph is inferred from query text.</p>"); return; }
        if (snapshot.Status != CypherSnapshotStatus.Complete)
        { html.Append("<p class=\"empty\">Diagram unavailable: ").Append(H(snapshot.Status.ToString())).Append(". The incomplete graph is intentionally omitted; inspect the statement above.</p>"); return; }
        for (var index = 0; index < snapshot.Clauses.Count; index++)
        {
            var clause = snapshot.Clauses[index];
            html.Append("<section class=\"clause ").Append(clause.Kind == CypherClauseKind.OptionalMatch ? "optional" : "")
                .Append("\"><header><h4>").Append(N(index + 1)).Append(" · ").Append(H(clause.Kind.ToString()))
                .Append(clause.Distinct ? " DISTINCT" : "").Append("</h4><a href=\"#").Append(key).Append("-line-").Append(N(clause.FirstLine))
                .Append("\">Cypher lines ").Append(N(clause.FirstLine)).Append("–").Append(N(clause.LastLine)).Append("</a></header>");
            if (clause.HasPredicate) html.Append("<p class=\"attachment\">WHERE belongs to this ").Append(H(clause.Kind.ToString())).Append(" clause.</p>");
            if (clause.Kind == CypherClauseKind.OptionalMatch && clause.HasPredicate)
                html.Append("<p class=\"meaning\">This predicate restricts the optional match. Unmatched outer rows can survive with null optional values; later clauses can still remove them.</p>");
            if (clause.Kind == CypherClauseKind.With && clause.HasPredicate)
                html.Append("<p class=\"meaning\">This predicate filters rows after WITH. It is not a predicate attached to an earlier OPTIONAL MATCH.</p>");
            for (var pattern = 0; pattern < clause.Patterns.Count; pattern++)
                RenderPattern(html, clause.Patterns[pattern], key + "-c" + N(index) + "-p" + N(pattern));
            html.Append("<dl class=\"scope\">");
            Fact(html, "Scope before", clause.ScopeBefore.Count == 0 ? "Empty" : string.Join(", ", clause.ScopeBefore));
            Fact(html, "Scope after", string.Join(", ", clause.ScopeAfter));
            html.Append("</dl>");
            if (clause.Aggregates.Count != 0)
            {
                html.Append("<ul class=\"aggregates\">");
                foreach (var aggregate in clause.Aggregates) html.Append("<li><code>").Append(H(aggregate.Expression)).Append(" AS ").Append(H(aggregate.Alias)).Append("</code></li>");
                html.Append("</ul><p class=\"muted\">DISTINCT aggregation and physical row counts have different multiplicity semantics.</p>");
            }
            html.Append("</section>");
            CheckSize(html);
        }
    }

    private static void RenderQueryEvidence(StringBuilder html, GraphQueryEvidence query, GraphContractVerification? verification, GraphSemanticCase? semantic)
    {
        if (query.Diagnostic is { } diagnosis) Diagnosis(html, diagnosis);
        if (verification?.Diagnostic is { } planDiagnosis) Diagnosis(html, planDiagnosis);
        if (semantic?.Diagnostic is { } resultDiagnosis) Diagnosis(html, resultDiagnosis);
        if (verification?.Plan is { } plan)
        {
            html.Append("<h4>Observed EXPLAIN operator tree</h4><p class=\"muted\">Planning only. No PROFILE, runtime timings, arguments or identifiers are shown.</p><ol class=\"plan\">");
            var depths = new Dictionary<int, int>();
            foreach (var node in plan.Operators)
            {
                var depth = node.Parent is { } parent ? depths[parent] + 1 : 0;
                depths[node.Index] = depth;
                html.Append("<li style=\"margin-inline-start:").Append(N(Math.Min(depth, 12) * 14)).Append("px\"><code>").Append(H(node.Operator))
                    .Append("</code><span class=\"muted\"> #").Append(N(node.Index)).Append(" · parent ").Append(node.Parent is { } p ? N(p) : "root").Append("</span></li>");
            }
            html.Append("</ol><details><summary>Redacted plan shape fingerprint</summary><p class=\"fingerprint\">").Append(H(plan.ShapeFingerprint))
                .Append("</p><p class=\"muted\">This identifies only the displayed redacted shape, not the complete plan or its performance.</p></details>");
        }
        else html.Append("<p class=\"empty\">No EXPLAIN plan captured. Opening this report does not request one.</p>");
        if (semantic is null) html.Append("<p class=\"empty\">Semantic comparison not run. Compilation or a plan is not evidence of correct result rows.</p>");
        else
        {
            html.Append("<h4>Independent semantic checks</h4><p>Native reference: <code>").Append(H(semantic.ReferenceId ?? "Not registered"))
                .Append("</code></p><table><thead><tr><th>Comparison</th><th>Status</th><th>Difference</th><th>Expected / observed rows</th></tr></thead><tbody>");
            Comparison(html, "Native vs fixed expectations", semantic.ReferenceComparison);
            Comparison(html, "Fluent vs native", semantic.QueryComparison);
            html.Append("</tbody></table><p class=\"muted\">Named fixture case only. Raw result values are excluded. A passing case does not override a blocked fixture or cleanup outcome.</p>");
            html.Append("<details><summary>Native reference fingerprint</summary><p class=\"fingerprint\">").Append(H(semantic.ReferenceFingerprint ?? "Not observed")).Append("</p></details>");
        }
    }
    private static void Comparison(StringBuilder html, string name, GraphResultComparisonReport? comparison)
        => html.Append("<tr><th scope=\"row\">").Append(H(name)).Append("</th><td>").Append(Badge(comparison?.Status ?? GraphEvidenceStatus.NotRun))
            .Append("</td><td>").Append(H(comparison?.Difference.ToString() ?? "Not observed"))
            .Append(comparison?.FirstDifferentRow is { } row ? " · first differing row " + N(row + 1) : "").Append("</td><td>")
            .Append(comparison?.ExpectedRows is { } expected ? N(expected) : "—").Append(" / ")
            .Append(comparison?.ActualRows is { } actual ? N(actual) : "—").Append("</td></tr>");
    private static void Diagnosis(StringBuilder html, GraphDiagnosticResult diagnosis)
        => html.Append("<div class=\"diagnosis\">").Append(Badge(diagnosis.Status)).Append(" <strong>").Append(H(diagnosis.Code)).Append("</strong><p>")
            .Append(H(diagnosis.Explanation)).Append("</p><p>").Append(H(diagnosis.SuggestedAction)).Append("</p></div>");
    private static void Fact(StringBuilder html, string label, string value)
        => html.Append("<div><dt>").Append(H(label)).Append("</dt><dd>").Append(H(value)).Append("</dd></div>");
    private static string H(string value) => HtmlEncoder.Default.Encode(value);
    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Badge(GraphEvidenceStatus status) => "<span class=\"badge " + status.ToString().ToLowerInvariant() + "\">" + (status switch
    {
        GraphEvidenceStatus.Pass => "✓ Pass</span>", GraphEvidenceStatus.Fail => "✕ Fail</span>",
        GraphEvidenceStatus.Blocked => "⏳ Blocked</span>", GraphEvidenceStatus.Unsupported => "Unsupported</span>", _ => "○ Not run</span>"
    });
    private static void CheckSize(StringBuilder html)
    {
        if (html.Length > MaxHtmlCharacters) throw new InvalidOperationException("Query Lab HTML exceeds its 4,000,000-character budget. Select a smaller catalog.");
    }
}
