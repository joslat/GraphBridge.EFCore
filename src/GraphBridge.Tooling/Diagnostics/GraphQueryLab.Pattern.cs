using System.Text;
using GraphBridge.EntityFrameworkCore.FluentCypher;

namespace GraphBridge.Tooling.Diagnostics;

public static partial class GraphQueryLab
{
    private static void RenderPattern(StringBuilder html, CypherPatternSnapshot pattern, string key)
    {
        if (pattern.PathAlias is { } path) html.Append("<p>Named path <code>").Append(H(path)).Append("</code> · ")
            .Append(N(pattern.MinimumHops!.Value)).Append("–").Append(N(pattern.MaximumHops!.Value)).Append(" hops</p>");
        if (pattern.Nodes.Count <= 32 && pattern.Steps.Count <= 64)
        {
            var positions = pattern.Nodes.Select((n, index) => (n.Alias, X: 90 + index % 3 * 220, Y: 90 + index / 3 * 150))
                .ToDictionary(n => n.Alias, n => (n.X, n.Y), StringComparer.Ordinal);
            var height = 175 + (pattern.Nodes.Count - 1) / 3 * 150;
            html.Append("<svg class=\"pattern\" role=\"img\" aria-labelledby=\"").Append(key).Append("-title\" viewBox=\"0 0 620 ").Append(N(height))
                .Append("\"><title id=\"").Append(key).Append("-title\">Compiler query pattern; equivalent node and relationship tables follow</title><defs><marker id=\"")
                .Append(key).Append("-arrow\" viewBox=\"0 0 10 10\" refX=\"9\" refY=\"5\" markerWidth=\"7\" markerHeight=\"7\" orient=\"auto-start-reverse\"><path d=\"M 0 0 L 10 5 L 0 10 z\"/></marker></defs>");
            foreach (var step in pattern.Steps)
            {
                var start = positions[step.StartAlias]; var end = positions[step.EndAlias];
                if (step.Direction == CypherStepDirection.Incoming) (start, end) = (end, start);
                var dx = end.X - start.X; var dy = end.Y - start.Y;
                var distance = Math.Sqrt(dx * dx + dy * dy);
                string edgePath;
                if (distance == 0) edgePath = $"M {N(start.X - 20)} {N(start.Y - 20)} C {N(start.X - 80)} {N(start.Y - 90)} {N(start.X + 80)} {N(start.Y - 90)} {N(start.X + 20)} {N(start.Y - 20)}";
                else edgePath = FormattableString.Invariant($"M {start.X + dx / distance * 28:F2} {start.Y + dy / distance * 28:F2} L {end.X - dx / distance * 31:F2} {end.Y - dy / distance * 31:F2}");
                html.Append("<path class=\"edge\" d=\"").Append(edgePath).Append("\" marker-end=\"url(#").Append(key).Append("-arrow)\"");
                if (step.Direction == CypherStepDirection.Both) html.Append(" marker-start=\"url(#").Append(key).Append("-arrow)\"");
                html.Append("><title>").Append(H(step.Alias + ": " + step.RelationshipType)).Append("</title></path><text class=\"edge-label\" x=\"")
                    .Append(N((start.X + end.X) / 2)).Append("\" y=\"").Append(N((start.Y + end.Y) / 2 - (distance == 0 ? 66 : 14)))
                    .Append("\">").Append(H(step.Alias + " · " + Short(step.RelationshipType))).Append("</text>");
            }
            foreach (var node in pattern.Nodes)
            {
                var point = positions[node.Alias];
                html.Append("<g class=\"node\"><circle cx=\"").Append(N(point.X)).Append("\" cy=\"").Append(N(point.Y)).Append("\" r=\"27\"/>")
                    .Append("<text class=\"alias\" x=\"").Append(N(point.X)).Append("\" y=\"").Append(N(point.Y + 5)).Append("\">").Append(H(node.Alias))
                    .Append("</text><text x=\"").Append(N(point.X)).Append("\" y=\"").Append(N(point.Y + 53)).Append("\">").Append(H(Short(node.Label)))
                    .Append("</text><title>").Append(H(node.Alias + ": " + node.Label)).Append("</title></g>");
            }
            html.Append("</svg>");
        }
        else html.Append("<p class=\"empty\">Diagram exceeds the 32-node / 64-step display budget. Exact pattern data remains in the tables below.</p>");
        html.Append("<details open class=\"pattern-data\"><summary>Accessible pattern data · ").Append(N(pattern.Nodes.Count)).Append(" nodes, ").Append(N(pattern.Steps.Count))
            .Append(" steps</summary><table><thead><tr><th>Node alias</th><th>Mapped label</th></tr></thead><tbody>");
        foreach (var node in pattern.Nodes) html.Append("<tr><td>").Append(H(node.Alias)).Append("</td><td>").Append(H(node.Label)).Append("</td></tr>");
        html.Append("</tbody></table>");
        if (pattern.Steps.Count != 0)
        {
            html.Append("<table><thead><tr><th>Edge</th><th>Traversal</th><th>Mapping / physical endpoints</th></tr></thead><tbody>");
            foreach (var edge in pattern.Steps)
            {
                var direction = edge.Direction == CypherStepDirection.Outgoing ? " → " : edge.Direction == CypherStepDirection.Incoming ? " ← " : " ↔ ";
                html.Append("<tr><td>").Append(H(edge.Alias + ": " + edge.RelationshipType)).Append("</td><td>").Append(H(edge.StartAlias + direction + edge.EndAlias))
                    .Append("</td><td>").Append(edge.Identified ? "Identified relationship" : "Association relationship").Append(" · ")
                    .Append(H(edge.MappedSourceAlias is null ? "Individual endpoints vary along the path" : edge.MappedSourceAlias + " → " + edge.MappedTargetAlias)).Append("</td></tr>");
            }
            html.Append("</tbody></table>");
        }
        html.Append("</details>");
    }
    private static string Short(string text) => text.Length <= 22 ? text : text[..21] + "…";
}
