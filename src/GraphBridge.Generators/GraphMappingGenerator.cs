using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Text;

namespace GraphBridge.Generators;

[Generator(LanguageNames.CSharp)]
public sealed class GraphMappingGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(output => output.AddSource("GraphBridge.Attributes.g.cs", SourceText.From(AttributeSource.Text, Encoding.UTF8)));
        var declarations = context.SyntaxProvider.ForAttributeWithMetadataName(ContractReader.Prefix + "GraphModelAttribute",
            static (syntax, _) => syntax is ClassDeclarationSyntax,
            static (input, cancellationToken) => ContractReader.Read(input, cancellationToken))
            .WithTrackingName("GraphMappingContract");
        context.RegisterSourceOutput(declarations, static (output, result) =>
        {
            foreach (var diagnostic in result.Diagnostics) output.ReportDiagnostic(diagnostic);
            if (result.Source is not null) output.AddSource(result.Hint, SourceText.From(result.Source, Encoding.UTF8));
        });
    }
}
