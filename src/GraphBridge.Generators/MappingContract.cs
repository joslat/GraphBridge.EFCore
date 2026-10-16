using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace GraphBridge.Generators;

internal sealed class MappingContract
{
    internal MappingContract(INamedTypeSymbol owner, ImmutableArray<EntityContract> entities)
        => (Owner, Entities) = (owner, entities);
    internal INamedTypeSymbol Owner { get; }
    internal ImmutableArray<EntityContract> Entities { get; }
}

internal sealed class EntityContract
{
    internal EntityContract(INamedTypeSymbol symbol, string token, string? iri, bool edge, string[] keys)
        => (Symbol, Token, Iri, Edge, Keys) = (symbol, token, iri, edge, keys);
    internal INamedTypeSymbol Symbol { get; }
    internal string Token { get; }
    internal string? Iri { get; }
    internal bool Edge { get; }
    internal string[] Keys { get; }
    internal List<PropertyContract> Properties { get; } = new();
    internal List<string> Ignored { get; } = new();
    internal EndpointContract? Source { get; set; }
    internal EndpointContract? Target { get; set; }
}

internal sealed class PropertyContract
{
    internal PropertyContract(IPropertySymbol symbol, string name, bool required, string scalar, int conversion, int concurrency, string? iri)
        => (Symbol, Name, Required, Scalar, Conversion, Concurrency, Iri) = (symbol, name, required, scalar, conversion, concurrency, iri);
    internal IPropertySymbol Symbol { get; }
    internal string Name { get; }
    internal bool Required { get; }
    internal string Scalar { get; }
    internal int Conversion { get; }
    internal int Concurrency { get; }
    internal string? Iri { get; }
}

internal sealed class EndpointContract
{
    internal EndpointContract(IPropertySymbol symbol, INamedTypeSymbol node, string[] keys)
        => (Symbol, Node, Keys) = (symbol, node, keys);
    internal IPropertySymbol Symbol { get; }
    internal INamedTypeSymbol Node { get; }
    internal string[] Keys { get; }
}

internal sealed class GenerationResult : IEquatable<GenerationResult>
{
    internal GenerationResult(string hint, string? source, ImmutableArray<Diagnostic> diagnostics)
        => (Hint, Source, Diagnostics) = (hint, source, diagnostics);
    internal string Hint { get; }
    internal string? Source { get; }
    internal ImmutableArray<Diagnostic> Diagnostics { get; }
    // Diagnostics retain locations. Valid results can be cached across unrelated syntax changes.
    public bool Equals(GenerationResult? other) => ReferenceEquals(this, other) || other is not null && Diagnostics.IsEmpty && other.Diagnostics.IsEmpty
        && Hint == other.Hint && Source == other.Source;
    public override bool Equals(object? obj) => obj is GenerationResult value && Equals(value);
    public override int GetHashCode() => Hint.GetHashCode() ^ (Source?.GetHashCode() ?? 0);
}
