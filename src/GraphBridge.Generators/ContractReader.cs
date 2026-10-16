using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace GraphBridge.Generators;

internal static class ContractReader
{
    internal const string Prefix = "GraphBridge.Generated.";
    internal static readonly DiagnosticDescriptor Invalid = new("GBSG001", "Unsupported graph mapping declaration",
        "{0}", "GraphBridge.Generation", DiagnosticSeverity.Error, isEnabledByDefault: true);
    internal static readonly DiagnosticDescriptor Key = new("GBSG002", "Explicit graph key required",
        "{0}", "GraphBridge.Generation", DiagnosticSeverity.Error, isEnabledByDefault: true);
    internal static readonly DiagnosticDescriptor Endpoint = new("GBSG003", "Invalid graph endpoint",
        "{0}", "GraphBridge.Generation", DiagnosticSeverity.Error, isEnabledByDefault: true);
    internal static readonly DiagnosticDescriptor Token = new("GBSG004", "Invalid or ambiguous graph identifier",
        "{0}", "GraphBridge.Generation", DiagnosticSeverity.Error, isEnabledByDefault: true);
    internal static readonly DiagnosticDescriptor Property = new("GBSG006", "Unsupported graph property mapping",
        "{0}", "GraphBridge.Generation", DiagnosticSeverity.Error, isEnabledByDefault: true);
    internal static readonly DiagnosticDescriptor KeyConflict = new("GBSG007", "Conflicting graph key mapping",
        "{0}", "GraphBridge.Generation", DiagnosticSeverity.Error, isEnabledByDefault: true);
    internal static readonly DiagnosticDescriptor SchemaPolicy = new("GBSG008", "Explicit graph schema policy required",
        "{0}", "GraphBridge.Generation", DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static GenerationResult Read(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        var owner = (INamedTypeSymbol)context.TargetSymbol;
        var issues = ImmutableArray.CreateBuilder<Diagnostic>();
        void Error(DiagnosticDescriptor rule, ISymbol symbol, string message, AttributeData? attribute = null)
            => issues.Add(Diagnostic.Create(rule, attribute?.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation()
                ?? symbol.Locations.FirstOrDefault(location => location.IsInSource), message));
        var hint = ExpectedWitness.Hash(owner.ToDisplayString()) + ".GraphBridge.g.cs";
        var declaration = (ClassDeclarationSyntax)context.TargetNode;
        if (!owner.IsStatic || owner.ContainingType is not null || owner.Arity != 0
            || !declaration.Modifiers.Any(SyntaxKind.PartialKeyword)
            || owner.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
            Error(Invalid, owner, "GraphModel requires a top-level public/internal static partial non-generic class.");
        foreach (var name in new[] { "Apply", "Verify", "Fingerprint", "ExpectedCanonicalJson", "Identifiers" })
            if (owner.Name == name || owner.GetMembers(name).Length != 0) Error(Invalid, owner, "Generated registration member name conflicts: " + name + ".");
        if (context.Attributes.Length != 1) Error(Invalid, owner, "Exactly one GraphModel declaration is required.");
        var model = context.Attributes[0];
        if (NamedInt(model, "SchemaPolicy") != 1)
            Error(SchemaPolicy, owner, "Set GraphModel.SchemaPolicy = GraphSchemaPolicy.ExistingConstraints. Provision reviewed key constraints separately; generation never deploys or verifies database schema.", model);
        var typeValues = model.ConstructorArguments.Length == 1 && !model.ConstructorArguments[0].IsNull
            ? model.ConstructorArguments[0].Values : ImmutableArray<TypedConstant>.Empty;
        if (typeValues.Length is 0 or > 256)
        {
            Error(Invalid, owner, "GraphModel requires between 1 and 256 explicit entity types.", model);
            return new(hint, null, issues.ToImmutable());
        }
        var entities = new List<EntityContract>();
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var value in typeValues)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (value.Value is not INamedTypeSymbol type || type.TypeKind != TypeKind.Class || !seen.Add(type))
            { Error(Invalid, owner, "GraphModel entity types must be distinct classes.", model); continue; }
            if (type.ContainingType is not null || type.Arity != 0 || type.IsAbstract || type.IsStatic || type.IsRecord
                || type.BaseType?.SpecialType != SpecialType.System_Object
                || type.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
                Error(Invalid, type, "Generated entities must be top-level accessible non-generic concrete classes without inheritance or record semantics.");
            CheckMetadata(type);
            var node = Attribute(type, "GraphNode");
            var edge = Attribute(type, "GraphIdentifiedEdge");
            if ((node is null) == (edge is null))
            { Error(Invalid, type, "Declare exactly one GraphNode or GraphIdentifiedEdge mapping."); continue; }
            var annotation = node ?? edge!;
            var token = String(annotation, 0);
            if (!IsToken(token)) Error(Token, type, "Storage identifiers must match [A-Za-z_][A-Za-z0-9_]* and contain at most 128 characters.", annotation);
            var keys = node is not null ? Strings(annotation, 1) : new[] { String(annotation, 1) };
            var entity = new EntityContract(type, token, NamedString(annotation, "Iri"), edge is not null, keys);
            CheckIri(entity.Iri, type, annotation);
            foreach (var field in type.GetMembers().OfType<IFieldSymbol>().Where(field => !field.IsImplicitlyDeclared && !field.IsStatic))
                Error(Invalid, field, "Explicit instance fields are outside the generated contract; use mapped or ignored properties.");
            var declaredProperties = type.GetMembers().OfType<IPropertySymbol>().ToArray();
            if (declaredProperties.Length > 256) Error(Invalid, type, "At most 256 properties are supported per entity.");
            foreach (var property in declaredProperties)
            {
                CheckMetadata(property);
                var propertyAttribute = Attribute(property, "GraphProperty");
                var source = Attribute(property, "GraphSource");
                var target = Attribute(property, "GraphTarget");
                var ignored = Attribute(property, "GraphIgnore");
                var count = new[] { propertyAttribute, source, target, ignored }.Count(a => a is not null);
                if (count != 1)
                { Error(Invalid, property, "Every property must declare exactly one GraphProperty, GraphSource, GraphTarget or GraphIgnore."); continue; }
                if (property.IsStatic || property.IsIndexer || property.ExplicitInterfaceImplementations.Length != 0)
                { Error(Invalid, property, "Static, indexed and explicit-interface properties are unsupported."); continue; }
                if (ignored is not null) { entity.Ignored.Add(property.Name); continue; }
                if (property.GetMethod?.DeclaredAccessibility != Accessibility.Public || property.SetMethod?.DeclaredAccessibility != Accessibility.Public
                    || property.SetMethod?.IsInitOnly == true || property.ReturnsByRef || property.ReturnsByRefReadonly)
                    Error(Property, property, "Mapped property '" + property.Name + "' requires public get/set accessors. Use a mutable mapped property or GraphIgnore; init-only and ref properties are unsupported.");
                if (propertyAttribute is not null)
                {
                    var name = String(propertyAttribute, 0);
                    if (!IsToken(name)) Error(Token, property, "Invalid property storage identifier.", propertyAttribute);
                    var required = propertyAttribute.ConstructorArguments.Length > 1 && propertyAttribute.ConstructorArguments[1].Value is true;
                    var nullableValue = property.Type is INamedTypeSymbol named && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
                    var scalarType = nullableValue ? ((INamedTypeSymbol)property.Type).TypeArguments[0] : property.Type;
                    var scalar = Scalar(scalarType);
                    var conversion = NamedInt(propertyAttribute, "Conversion");
                    var concurrency = NamedInt(propertyAttribute, "Concurrency");
                    if (scalar is null || conversion != (scalar == "guid" ? 1 : scalar == "date" ? 2 : 0))
                        Error(Property, property, "Property '" + property.Name + "' of type '" + property.Type.ToDisplayString() + "' needs a supported scalar/conversion pair: native string/bool/int/long/double, GuidString, or DateOnlyIsoString. Use GraphIgnore for nonpersisted members.", propertyAttribute);
                    if (nullableValue == required && property.Type.IsValueType || property.Type.IsReferenceType
                        && property.NullableAnnotation != (required ? NullableAnnotation.NotAnnotated : NullableAnnotation.Annotated))
                        Error(Property, property, "Property '" + property.Name + "' requiredness must match explicit CLR nullability; align the GraphProperty required flag and CLR declaration and enable nullable annotations.", propertyAttribute);
                    if (concurrency < 0 || concurrency > 2 || concurrency == 2 && (scalar != "long" || !required))
                        Error(Property, property, "Property '" + property.Name + "' has an unsupported concurrency mode. IncrementingInt64 requires a required native long; otherwise choose None or OriginalValue.", propertyAttribute);
                    var iri = NamedString(propertyAttribute, "Iri");
                    CheckIri(iri, property, propertyAttribute);
                    entity.Properties.Add(new(property, name, required, scalar ?? "invalid", conversion, concurrency, iri));
                }
                else
                {
                    var endpointAttribute = source ?? target!;
                    if (!entity.Edge || property.Type is not INamedTypeSymbol endpointType || endpointType.TypeKind != TypeKind.Class)
                    { Error(Endpoint, property, "Only identified edges declare source/target node reference properties.", endpointAttribute); continue; }
                    if (property.NullableAnnotation != NullableAnnotation.NotAnnotated)
                        Error(Endpoint, property, "Endpoint navigation must be non-nullable.", endpointAttribute);
                    var endpoint = new EndpointContract(property, endpointType, Strings(endpointAttribute, 0));
                    if (source is not null)
                    { if (entity.Source is not null) Error(Endpoint, property, "Declare exactly one source endpoint.", source); entity.Source = endpoint; }
                    else
                    { if (entity.Target is not null) Error(Endpoint, property, "Declare exactly one target endpoint.", target); entity.Target = endpoint; }
                }
            }
            if (keys.Length == 0 || keys.Length > 16 || keys.Any(string.IsNullOrWhiteSpace) || keys.Distinct(StringComparer.Ordinal).Count() != keys.Length)
                Error(Key, type, "Declare 1 to 16 distinct ordered application key properties.", annotation);
            foreach (var key in keys)
            {
                var property = entity.Properties.SingleOrDefault(p => p.Symbol.Name == key);
                if (property is not null && property.Concurrency != 0)
                    Error(KeyConflict, property.Symbol, "Key '" + key + "' cannot also be a concurrency token or generated version. Keep the stable key and version in separate properties.", Attribute(property.Symbol, "GraphProperty"));
                else if (property is null || !property.Required || property.Scalar is not ("guid" or "string" or "int" or "long"))
                    Error(Key, type, "Key '" + key + "' must name a required stable Guid/string/int/long GraphProperty without concurrency generation.", annotation);
            }
            foreach (var group in entity.Properties.GroupBy(p => p.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
                foreach (var property in group) Error(Token, property.Symbol, "Property storage identifiers must be unique within an entity.");
            entities.Add(entity);
        }
        foreach (var duplicate in entities.GroupBy(e => (e.Edge ? "edge:" : "node:") + e.Token, StringComparer.Ordinal).Where(g => g.Count() > 1))
            foreach (var entity in duplicate) Error(Token, entity.Symbol, "Graph tokens must identify exactly one entity of each storage kind.");
        foreach (var edge in entities.Where(e => e.Edge))
        {
            CheckEndpoint(edge, edge.Source, "source"); CheckEndpoint(edge, edge.Target, "target");
            var slots = edge.Keys.Concat(edge.Source?.Keys ?? Array.Empty<string>()).Concat(edge.Target?.Keys ?? Array.Empty<string>()).ToArray();
            if (slots.Distinct(StringComparer.Ordinal).Count() != slots.Length)
                Error(Endpoint, edge.Symbol, "Edge identity and source/target foreign-key groups must be disjoint.");
        }
        if (!entities.Any(e => !e.Edge)) Error(Invalid, owner, "A generated graph model requires at least one explicitly keyed node.", model);
        if (issues.Count != 0) return new(hint, null, issues.ToImmutable());
        var contract = new MappingContract(owner, entities.OrderBy(e => e.Symbol.ToDisplayString(), StringComparer.Ordinal).ToImmutableArray());
        return new(hint, MappingEmitter.Emit(contract), ImmutableArray<Diagnostic>.Empty);

        void CheckIri(string? iri, ISymbol symbol, AttributeData attribute)
        {
            if (iri is not null && (iri.Length > 2048 || iri.Any(char.IsControl) || !Uri.TryCreate(iri, UriKind.Absolute, out _)))
                Error(Invalid, symbol, "An optional IRI must be absolute, contain no controls and fit within 2048 characters.", attribute);
        }
        void CheckMetadata(ISymbol symbol)
        {
            foreach (var attribute in symbol.GetAttributes())
            {
                var ns = attribute.AttributeClass?.ContainingNamespace.ToDisplayString() ?? "";
                if (ns.StartsWith("System.ComponentModel.DataAnnotations", StringComparison.Ordinal)
                    || ns.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal))
                {
                    var keyAttribute = attribute.AttributeClass?.ToDisplayString() is "System.ComponentModel.DataAnnotations.KeyAttribute"
                        or "Microsoft.EntityFrameworkCore.PrimaryKeyAttribute" or "Microsoft.EntityFrameworkCore.KeylessAttribute"
                        or "System.ComponentModel.DataAnnotations.Schema.DatabaseGeneratedAttribute";
                    Error(keyAttribute ? KeyConflict : Invalid, symbol, keyAttribute
                        ? "Remove the EF/data-annotation key or generation attribute. Declare stable keys only with GraphNode/GraphIdentifiedEdge and GraphProperty; put generated versions in a separate non-key property."
                        : "EF/data-annotation mapping attributes are outside this closed contract; express mapping with GraphBridge.Generated attributes.", attribute);
                }
            }
        }
        void CheckEndpoint(EntityContract edge, EndpointContract? endpoint, string role)
        {
            if (endpoint is null) { Error(Endpoint, edge.Symbol, "An identified edge needs exactly one " + role + " endpoint."); return; }
            var node = entities.SingleOrDefault(e => SymbolEqualityComparer.Default.Equals(e.Symbol, endpoint.Node) && !e.Edge);
            if (node is null) { Error(Endpoint, endpoint.Symbol, "Endpoint type must resolve to a node explicitly included in this GraphModel."); return; }
            if (endpoint.Keys.Length != node.Keys.Length || endpoint.Keys.Length == 0 || endpoint.Keys.Distinct(StringComparer.Ordinal).Count() != endpoint.Keys.Length)
            { Error(Endpoint, endpoint.Symbol, "Endpoint slots must match the complete ordered principal key."); return; }
            for (var index = 0; index < endpoint.Keys.Length; index++)
            {
                var dependent = edge.Properties.SingleOrDefault(p => p.Symbol.Name == endpoint.Keys[index]);
                var principal = node.Properties.SingleOrDefault(p => p.Symbol.Name == node.Keys[index]);
                if (dependent is null || principal is null || !dependent.Required || dependent.Concurrency != 0
                    || dependent.Scalar != principal.Scalar || dependent.Conversion != principal.Conversion)
                    Error(Endpoint, endpoint.Symbol, "Each ordered endpoint slot must be a required GraphProperty matching its principal key type/conversion.");
            }
        }
    }

    internal static AttributeData? Attribute(ISymbol symbol, string name)
        => symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == Prefix + name + "Attribute");
    private static string String(AttributeData attribute, int index)
        => attribute.ConstructorArguments.Length > index ? attribute.ConstructorArguments[index].Value as string ?? "" : "";
    private static string[] Strings(AttributeData attribute, int index)
        => attribute.ConstructorArguments.Length > index && !attribute.ConstructorArguments[index].IsNull
            && attribute.ConstructorArguments[index].Kind == TypedConstantKind.Array
            ? attribute.ConstructorArguments[index].Values.Select(v => v.Value as string ?? "").ToArray() : Array.Empty<string>();
    private static string? NamedString(AttributeData attribute, string name)
        => attribute.NamedArguments.FirstOrDefault(p => p.Key == name).Value.Value as string;
    private static int NamedInt(AttributeData attribute, string name)
        => attribute.NamedArguments.FirstOrDefault(p => p.Key == name).Value.Value is int value ? value : 0;
    private static bool IsToken(string value) => value.Length is > 0 and <= 128
        && (value[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_')
        && value.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
    private static string? Scalar(ITypeSymbol type) => type.SpecialType switch
    {
        SpecialType.System_String => "string", SpecialType.System_Boolean => "bool", SpecialType.System_Int32 => "int",
        SpecialType.System_Int64 => "long", SpecialType.System_Double => "double",
        _ => type.ToDisplayString() switch { "System.Guid" => "guid", "System.DateOnly" => "date", _ => null }
    };
}
