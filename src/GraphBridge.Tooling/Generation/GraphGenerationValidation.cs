namespace GraphBridge.Tooling.Generation;

internal static class GraphGenerationValidation
{
    internal static IReadOnlyList<GraphGenerationDiagnostic> Validate(GraphGenerationModel model)
    {
        var issues = new List<GraphGenerationDiagnostic>();
        var objectMembers = typeof(object).GetMembers().Select(member => member.Name).Distinct(StringComparer.Ordinal).ToArray();
        void Error(string code, string subject, string message) => issues.Add(new(code, GraphGenerationSeverity.Error, subject, message));
        void Name(string value, string subject)
        {
            if (string.IsNullOrWhiteSpace(value)) Error("GBGEN001", subject, "An explicit nonempty CLR name is required.");
            else if (GraphCSharpNames.Clr(value) != value)
                issues.Add(new("GBGEN002", GraphGenerationSeverity.Warning, subject,
                    $"CLR name {GraphCSharpNames.Literal(value)} is emitted as {GraphCSharpNames.Literal(GraphCSharpNames.Clr(value))}."));
        }
        void Unique(IEnumerable<string> names, string subject, bool normalize)
        {
            foreach (var group in names.GroupBy(value => normalize ? GraphCSharpNames.Clr(value) : value,
                         normalize ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Where(group => group.Count() > 1))
                Error("GBGEN003", subject, $"Names collide after deterministic normalization: {string.Join(", ", group.Order(StringComparer.Ordinal).Select(GraphCSharpNames.Literal))}.");
        }
        void StoreName(string value, string subject)
        {
            if (!GraphCSharpNames.IsStoreIdentifier(value))
                Error("GBGEN004", subject, "Graph identifiers must match [A-Za-z_][A-Za-z0-9_]*; they are never inferred or escaped from arbitrary text.");
        }
        void Iri(string? value, string subject)
        {
            if (value is not null && (!Uri.TryCreate(value, UriKind.Absolute, out _) || value.Any(char.IsControl)))
                Error("GBGEN005", subject, "Provenance IRI must be an absolute IRI without control characters.");
        }
        void Properties(string owner, IReadOnlyList<GraphPropertyDefinition> properties, IEnumerable<string> navigationNames)
        {
            var members = properties.Select(property => property.ClrName).Concat(navigationNames).ToArray();
            Unique(members.Concat(members.Select(name => "get_" + GraphCSharpNames.Clr(name)))
                .Concat(members.Select(name => "set_" + GraphCSharpNames.Clr(name)))
                .Concat(objectMembers).Append(owner), owner, normalize: true);
            Unique(properties.Select(property => property.StoreName), owner + ".store", normalize: false);
            foreach (var property in properties)
            {
                var subject = owner + "." + property.ClrName;
                Name(property.ClrName, subject);
                StoreName(property.StoreName, subject);
                Iri(property.Iri, subject);
                var validConversion = property.ScalarType switch
                {
                    GraphScalarType.String or GraphScalarType.Boolean or GraphScalarType.Int32 or GraphScalarType.Int64 or GraphScalarType.Double
                        => property.Conversion == GraphScalarConversion.Native,
                    GraphScalarType.Guid => property.Conversion == GraphScalarConversion.GuidString,
                    GraphScalarType.DateOnly => property.Conversion == GraphScalarConversion.DateOnlyIsoString,
                    _ => false
                };
                if (!validConversion) Error("GBGEN006", subject, "An explicit supported scalar type/conversion pair is required.");
                if (!Enum.IsDefined(property.Concurrency)
                    || property.Concurrency == GraphConcurrencyMode.IncrementingInt64
                    && (property.ScalarType != GraphScalarType.Int64 || !property.IsRequired || property.Conversion != GraphScalarConversion.Native))
                    Error("GBGEN007", subject, "Generated versions require a required native Int64; other concurrency modes must be recognized.");
            }
        }
        void Identity(string owner, IReadOnlyList<GraphPropertyDefinition> properties, IReadOnlyList<string> names)
        {
            if (names.Count == 0) Error("GBGEN008", owner, "Explicit application key properties are required.");
            Unique(names, owner + ".key", normalize: false);
            foreach (var name in names)
            {
                var matches = properties.Where(property => property.ClrName == name).ToArray();
                if (matches.Length != 1)
                    Error("GBGEN008", owner + "." + name, "Key slot must refer to exactly one declared property by its authored CLR name.");
                else if (!matches[0].IsRequired || matches[0].Concurrency != GraphConcurrencyMode.None
                    || matches[0].ScalarType is not (GraphScalarType.Guid or GraphScalarType.String or GraphScalarType.Int32 or GraphScalarType.Int64))
                    Error("GBGEN008", owner + "." + name, "Keys require a required stable Guid, string, Int32 or Int64 property without concurrency generation.");
            }
        }
        void Endpoint(GraphIdentifiedEdgeDefinition edge, GraphEndpointDefinition endpoint, string role)
        {
            var subject = edge.ClrName + "." + role;
            Name(endpoint.NavigationName, subject);
            if (endpoint.Deletion != GraphDeletionPolicy.Restrict)
                Error("GBGEN009", subject, "Only explicit Restrict endpoint deletion is supported by this emitter.");
            var nodes = model.Nodes.Where(node => node.ClrName == endpoint.NodeClrName).ToArray();
            if (nodes.Length != 1)
            {
                Error("GBGEN010", subject, "Endpoint must resolve to exactly one declared node by its authored CLR name; edges cannot be endpoints.");
                return;
            }
            var node = nodes[0];
            if (endpoint.ForeignKeyProperties.Count != node.KeyProperties.Count || endpoint.ForeignKeyProperties.Count == 0)
                Error("GBGEN011", subject, "Endpoint FK slots must match the complete ordered node primary key.");
            Unique(endpoint.ForeignKeyProperties, subject, normalize: false);
            for (var index = 0; index < Math.Min(endpoint.ForeignKeyProperties.Count, node.KeyProperties.Count); index++)
            {
                var dependent = edge.Properties.Where(property => property.ClrName == endpoint.ForeignKeyProperties[index]).ToArray();
                var principal = node.Properties.Where(property => property.ClrName == node.KeyProperties[index]).ToArray();
                if (dependent.Length != 1 || principal.Length != 1 || !dependent[0].IsRequired
                    || dependent[0].Concurrency != GraphConcurrencyMode.None
                    || dependent[0].ScalarType != principal[0].ScalarType || dependent[0].Conversion != principal[0].Conversion)
                    Error("GBGEN011", subject, "Each endpoint FK must be a declared required non-generated property matching the corresponding node key type and conversion.");
            }
        }

        foreach (var segment in model.Namespace.Split('.')) Name(segment, "namespace");
        Name(model.ContextName, "context");
        var types = model.Nodes.Select(node => node.ClrName).Concat(model.IdentifiedEdges.Select(edge => edge.ClrName)).ToArray();
        Unique(types.Concat([model.ContextName, "GraphModelConfiguration", "GraphIris"]).Concat(objectMembers), "types", normalize: true);
        // DbSet accessors are deliberately not pluralized. Reserve inherited/public context names as well.
        var contextMembers = typeof(Microsoft.EntityFrameworkCore.DbContext).GetMembers()
            .Select(member => member.Name).Distinct(StringComparer.Ordinal);
        var sets = types.Select(type => GraphCSharpNames.Clr(type) + "Set").ToArray();
        Unique(sets.Concat(sets.Select(name => "get_" + name))
            .Concat(contextMembers).Append(GraphCSharpNames.Clr(model.ContextName)).Append("OnModelCreatingPartial"), "context members", normalize: true);
        Unique(model.Nodes.Select(node => node.Label), "node labels", normalize: false);
        Unique(model.IdentifiedEdges.Select(edge => edge.RelationshipType), "relationship types", normalize: false);
        if (model.Nodes.Count == 0) Error("GBGEN008", "model", "At least one explicitly keyed node is required for tracked model generation.");
        foreach (var node in model.Nodes)
        {
            Name(node.ClrName, node.ClrName);
            StoreName(node.Label, node.ClrName);
            Iri(node.Iri, node.ClrName);
            Properties(node.ClrName, node.Properties, []);
            Identity(node.ClrName, node.Properties, node.KeyProperties);
            if (node.Deletion != GraphDeletionPolicy.Restrict)
                Error("GBGEN009", node.ClrName, "Only explicit Restrict node deletion is supported by this emitter.");
        }
        foreach (var edge in model.IdentifiedEdges)
        {
            Name(edge.ClrName, edge.ClrName);
            StoreName(edge.RelationshipType, edge.ClrName);
            Iri(edge.Iri, edge.ClrName);
            Properties(edge.ClrName, edge.Properties, [edge.Source.NavigationName, edge.Target.NavigationName]);
            Identity(edge.ClrName, edge.Properties, [edge.KeyProperty]);
            Endpoint(edge, edge.Source, "source");
            Endpoint(edge, edge.Target, "target");
            if (edge.Direction != GraphEdgeDirection.SourceToTarget)
                Error("GBGEN012", edge.ClrName, "Only explicit SourceToTarget direction is supported; reverse the authored endpoint roles deliberately if needed.");
            var endpoints = edge.Source.ForeignKeyProperties.Concat(edge.Target.ForeignKeyProperties).ToArray();
            if (endpoints.Distinct(StringComparer.Ordinal).Count() != endpoints.Length || endpoints.Contains(edge.KeyProperty, StringComparer.Ordinal))
                Error("GBGEN013", edge.ClrName, "Identified-edge key and the two ordered endpoint FK groups must be disjoint.");
        }
        // Hierarchical IRI constants use one nested class per entity and one constant per property.
        foreach (var node in model.Nodes)
            Unique(node.Properties.Where(property => property.Iri is not null).Select(property => property.ClrName)
                .Concat(["Type", node.ClrName]), node.ClrName + ".IRIs", normalize: true);
        foreach (var edge in model.IdentifiedEdges)
            Unique(edge.Properties.Where(property => property.Iri is not null).Select(property => property.ClrName)
                .Concat(["Type", edge.ClrName]), edge.ClrName + ".IRIs", normalize: true);
        return issues;
    }
}
