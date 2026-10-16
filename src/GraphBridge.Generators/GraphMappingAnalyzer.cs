using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace GraphBridge.Generators;

/// <summary>Finds misplaced mapping declarations even when no GraphModel registration opts into generation.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class GraphMappingAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Misplaced = new("GBSG005", "Graph mapping attribute has no entity owner",
        "{0}", "GraphBridge.Generation", DiagnosticSeverity.Error, isEnabledByDefault: true);
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Misplaced);
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(symbolContext =>
        {
            var property = (IPropertySymbol)symbolContext.Symbol;
            foreach (var attribute in property.GetAttributes().Where(a => a.AttributeClass?.ContainingNamespace.ToDisplayString() == "GraphBridge.Generated"))
                if (ContractReader.Attribute(property.ContainingType, "GraphNode") is null
                    && ContractReader.Attribute(property.ContainingType, "GraphIdentifiedEdge") is null)
                    symbolContext.ReportDiagnostic(Diagnostic.Create(Misplaced,
                        attribute.ApplicationSyntaxReference?.GetSyntax(symbolContext.CancellationToken).GetLocation() ?? property.Locations.FirstOrDefault(),
                        "Graph property/endpoint declarations require a GraphNode or GraphIdentifiedEdge owner."));
        }, SymbolKind.Property);
    }
}
