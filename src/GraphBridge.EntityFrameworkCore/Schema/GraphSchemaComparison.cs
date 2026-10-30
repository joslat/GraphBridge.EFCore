using System.Security.Cryptography;
using System.Text;
using GraphBridge.EntityFrameworkCore.Metadata;

namespace GraphBridge.EntityFrameworkCore.Schema;

public enum GraphSchemaStatus { Pass, Warn, Fail }
public enum GraphSchemaIssueCategory { Identity, Existence, PropertyType, Index, Conflict, Capability, UnmanagedSchema, Manifest }
public enum GraphSchemaProposalKind { UniquenessConstraint, ExistenceConstraint, PropertyTypeConstraint, RangeIndex }

public sealed record GraphSchemaIssue(GraphSchemaStatus Status, GraphSchemaIssueCategory Category,
    string Code, string Subject, string Message);

/// <summary>Review-only CREATE text. This is neither an executable migration nor a guarantee that existing data passes DDL validation.</summary>
public sealed class GraphSchemaProposal
{
    internal GraphSchemaProposal(string name, GraphSchemaProposalKind kind, string entityType,
        string labelOrType, IEnumerable<string> properties, bool isRequired, string cypher)
    {
        Name = name;
        Kind = kind;
        EntityType = entityType;
        LabelOrType = labelOrType;
        Properties = Array.AsReadOnly(properties.ToArray());
        IsRequired = isRequired;
        Cypher = cypher;
    }
    public string Name { get; }
    public GraphSchemaProposalKind Kind { get; }
    public string EntityType { get; }
    public string LabelOrType { get; }
    public IReadOnlyList<string> Properties { get; }
    public bool IsRequired { get; }
    public string Cypher { get; }
}

public sealed class GraphSchemaComparison
{
    internal GraphSchemaComparison(IEnumerable<GraphSchemaIssue> issues, IEnumerable<GraphSchemaProposal> proposals)
    {
        Issues = Array.AsReadOnly(issues.Distinct()
            .OrderBy(issue => issue.Subject, StringComparer.Ordinal).ThenBy(issue => issue.Category)
            .ThenBy(issue => issue.Code, StringComparer.Ordinal).ThenBy(issue => issue.Message, StringComparer.Ordinal).ToArray());
        Proposals = Array.AsReadOnly(proposals.OrderBy(proposal => proposal.Name, StringComparer.Ordinal).ToArray());
        Status = Issues.Count == 0 ? GraphSchemaStatus.Pass : Issues.Max(issue => issue.Status);
    }
    public GraphSchemaStatus Status { get; }
    public IReadOnlyList<GraphSchemaIssue> Issues { get; }
    public IReadOnlyList<GraphSchemaProposal> Proposals { get; }
}

/// <summary>Compares schema metadata only. It does not scan data, execute DDL, or establish default Cypher dialect.</summary>
public static class GraphSchemaComparer
{
    public static GraphSchemaComparison Compare(GraphSchemaManifest manifest,
        GraphSchemaCapabilities capabilities, GraphSchemaObservation observation)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(observation);
        return new Comparison(manifest, capabilities, observation).Run();
    }

    private sealed class Comparison(GraphSchemaManifest manifest, GraphSchemaCapabilities capabilities,
        GraphSchemaObservation observation)
    {
        private readonly List<GraphSchemaIssue> _issues = [];
        private readonly Dictionary<string, GraphSchemaProposal> _proposals = new(StringComparer.Ordinal);
        private readonly HashSet<GraphObservedConstraint> _usedConstraints = [];
        private readonly HashSet<GraphObservedIndex> _usedIndexes = [];

        public GraphSchemaComparison Run()
        {
            foreach (var node in manifest.Nodes)
                Owner("NODE", node.Label, node.KeyProperties, node.Properties, node.Indexes, allowProposals: true, node.AlternateKeys);
            foreach (var group in manifest.Relationships.GroupBy(edge => edge.RelationshipType, StringComparer.Ordinal))
            {
                var shared = group.Count() > 1;
                if (shared)
                    Add(GraphSchemaStatus.Warn, GraphSchemaIssueCategory.Capability, "SharedRelationshipType",
                        "RELATIONSHIP:" + group.Key,
                        "Multiple endpoint mappings share this relationship type. Type-wide property/index proposals are suppressed; review their combined schema manually. An identified key's constraint is still checked and proposed, once.");
                // Owners of a shared identified type share one key property (ADR 0056), so its constraint is checked once.
                var keyed = new HashSet<string>(StringComparer.Ordinal);
                foreach (var edge in group)
                {
                    var identity = edge.Identity switch
                    {
                        GraphSchemaRelationshipIdentity.ApplicationId => keyed.Add(edge.KeyProperty!) ? new[] { edge.KeyProperty! } : [],
                        GraphSchemaRelationshipIdentity.EndpointPair or GraphSchemaRelationshipIdentity.DependentNode => [],
                        _ => throw new NotSupportedException("Unknown GraphBridge relationship identity cannot be compared as an association.")
                    };
                    Owner("RELATIONSHIP", edge.RelationshipType,
                        identity,
                        edge.Properties, edge.Indexes, allowProposals: !shared, identityProposals: true);
                    if (edge.Identity == GraphSchemaRelationshipIdentity.EndpointPair)
                        Add(GraphSchemaStatus.Warn, GraphSchemaIssueCategory.Identity, "EndpointPairApplicationEnforced",
                            "RELATIONSHIP:" + edge.RelationshipType,
                            "Native uniqueness constraints do not enforce one relationship per endpoint pair. Provider locks/cardinality checks remain required; schema inspection does not validate relationship data.");
                    if (edge.Identity == GraphSchemaRelationshipIdentity.DependentNode)
                    {
                        Add(GraphSchemaStatus.Warn, GraphSchemaIssueCategory.Identity, "DependentNodeCardinalityApplicationEnforced",
                            "RELATIONSHIP:" + edge.RelationshipType,
                            $"Each dependent node requires {(edge.RequiredIncomingLinks == 0 ? "zero or one" : "exactly one")} incoming mapped physical relationship. Principal/dependent node key uniqueness does not enforce this cardinality; provider locking and read/write validation remain required. No relationship uniqueness constraint can be inferred from the derived FK slots.");
                        Add(GraphSchemaStatus.Warn, GraphSchemaIssueCategory.Manifest, "DependentNodeShapeNotValidated",
                            "RELATIONSHIP:" + edge.RelationshipType,
                            "Schema metadata does not prove stored direction, principal/dependent labels, required link existence or complete canonical principal keys. Relationship data was not scanned; malformed, duplicate and missing incoming links remain runtime validation concerns.");
                    }
                }
            }
            foreach (var constraint in observation.Constraints.Where(value => !_usedConstraints.Contains(value)))
                Add(GraphSchemaStatus.Warn, GraphSchemaIssueCategory.UnmanagedSchema, "AdditionalConstraint", constraint.Name,
                    "Observed constraint is outside the compared manifest requirements. Review its effect; no removal is proposed.");
            foreach (var index in observation.Indexes.Where(value => !_usedIndexes.Contains(value)))
            {
                if (index.OwningConstraint is not null
                    && _usedConstraints.Any(value => value.Name == index.OwningConstraint))
                    continue;
                Add(GraphSchemaStatus.Warn, GraphSchemaIssueCategory.UnmanagedSchema, "AdditionalIndex", index.Name,
                    "Observed index is outside the compared manifest requirements. No removal is proposed.");
            }
            foreach (var caveat in manifest.Caveats)
                Add(GraphSchemaStatus.Warn, GraphSchemaIssueCategory.Manifest, "ManifestCaveat", "manifest", caveat);
            if (observation.GraphTypeSpecification is not null)
                Add(GraphSchemaStatus.Warn, GraphSchemaIssueCategory.Capability, "GraphTypeObservedNotCompared", "graph-type",
                    "Graph type specification was observed but is not semantically parsed; constraints and indexes are compared separately.");
            return new GraphSchemaComparison(_issues, _proposals.Values);
        }

        private void Owner(string entityType, string label, IReadOnlyList<string> identity,
            IReadOnlyList<GraphSchemaProperty> properties, IReadOnlyList<GraphSchemaIndex> indexes, bool allowProposals,
            IReadOnlyList<GraphSchemaIndex>? alternateKeys = null, bool? identityProposals = null)
        {
            GraphIdentifier.Validate(label, nameof(label));
            foreach (var property in properties) GraphIdentifier.Validate(property.Name, nameof(properties));
            if (identity.Count > 0)
                Unique(entityType, label, identity, required: true, identityProposals ?? allowProposals);
            foreach (var alternateKey in alternateKeys ?? [])
                Unique(entityType, label, alternateKey.Properties, required: true, allowProposals, alternate: true);
            foreach (var property in properties)
                Property(entityType, label, property, allowProposals);
            foreach (var index in indexes.OrderByDescending(index => index.IsUnique))
            {
                if (index.IsUnique)
                {
                    if (allowProposals)
                        Unique(entityType, label, index.Properties, required: false, allowProposals);
                }
                else
                    Index(entityType, label, index.Properties, allowProposals);
            }
            // Required database properties absent from the mapped storage shape reject provider-created data.
            foreach (var constraint in Constraints(entityType, label).Where(value => IsExistence(value) || IsKey(value)))
            {
                if (constraint.Properties?.Any(name => properties.All(property => property.Name != name)) == true)
                    Add(GraphSchemaStatus.Fail, GraphSchemaIssueCategory.Conflict, "UnmappedRequiredProperty", constraint.Name,
                        "Observed existence/key constraint requires an unmapped property. Provider creates cannot satisfy this schema; no destructive fix is proposed.");
            }
        }

        private void Unique(string entityType, string label, IReadOnlyList<string> properties, bool required, bool allowProposals,
            bool alternate = false)
        {
            var subject = Subject(entityType, label, properties);
            var matches = Constraints(entityType, label).Where(value => (IsUnique(value) || IsKey(value))
                && SameSet(value.Properties, properties)).ToArray();
            if (matches.Length > 0)
            {
                foreach (var match in matches) _usedConstraints.Add(match);
                Add(GraphSchemaStatus.Pass, required ? GraphSchemaIssueCategory.Identity : GraphSchemaIssueCategory.Index,
                    alternate ? "AlternateKeyConstraintPresent" : required ? "IdentityConstraintPresent" : "UniqueIndexIntentPresent", subject,
                    "Observed exact property-set uniqueness. This does not imply property existence unless the constraint is a key.");
                // Existence of a constraint proves enforcement; a non-online backing index is a separate operational failure.
                foreach (var index in observation.Indexes.Where(index => matches.Any(value => value.Name == index.OwningConstraint)))
                {
                    _usedIndexes.Add(index);
                    if (index.State != "ONLINE")
                        Add(required ? GraphSchemaStatus.Fail : GraphSchemaStatus.Warn, GraphSchemaIssueCategory.Index,
                            "ConstraintIndexNotOnline", index.Name, $"Constraint backing index state is '{index.State}'.");
                }
                return;
            }
            Add(required ? GraphSchemaStatus.Fail : GraphSchemaStatus.Warn,
                required ? GraphSchemaIssueCategory.Identity : GraphSchemaIssueCategory.Index,
                alternate ? "MissingAlternateKeyConstraint" : required ? "MissingIdentityConstraint" : "MissingUniqueIndexIntent", subject,
                "No exact node/relationship property uniqueness or key constraint covers these store properties.");
            var support = entityType == "NODE" ? capabilities.NodeUniqueness : capabilities.RelationshipUniqueness;
            Propose(GraphSchemaProposalKind.UniquenessConstraint, entityType, label, properties,
                required, support, allowProposals);
        }

        private void Property(string entityType, string label, GraphSchemaProperty property, bool allowProposals)
        {
            var subject = Subject(entityType, label, [property.Name]);
            var existence = Constraints(entityType, label).Where(value => (IsExistence(value) || IsKey(value))
                && value.Properties?.Contains(property.Name, StringComparer.Ordinal) == true).ToArray();
            if (existence.Length > 0)
            {
                foreach (var match in existence.Where(IsExistence)) _usedConstraints.Add(match);
                Add(property.IsRequired ? GraphSchemaStatus.Pass : GraphSchemaStatus.Fail,
                    property.IsRequired ? GraphSchemaIssueCategory.Existence : GraphSchemaIssueCategory.Conflict,
                    property.IsRequired ? "PropertyExistencePresent" : "NullablePropertyConstrained", subject,
                    property.IsRequired ? "Observed existence enforcement, evaluated separately from uniqueness."
                        : "Database existence enforcement rejects null values allowed by the EF property.");
            }
            else if (property.IsRequired)
            {
                Add(GraphSchemaStatus.Warn, GraphSchemaIssueCategory.Existence, "PropertyExistenceApplicationEnforced", subject,
                    "Required property existence is checked by the provider but is not enforced by the observed database schema.");
                Propose(GraphSchemaProposalKind.ExistenceConstraint, entityType, label, [property.Name],
                    required: false, capabilities.PropertyExistence, allowProposals);
            }
            var types = Constraints(entityType, label).Where(value => IsType(value)
                && SameSet(value.Properties, [property.Name])).ToArray();
            if (types.Length == 0 && property.VectorDimensions is not null)
                // A migration step can't create a LIST<FLOAT> type constraint, so none is proposed; writes check vectors.
                Add(GraphSchemaStatus.Warn, GraphSchemaIssueCategory.PropertyType, "PropertyTypeApplicationEnforced", subject,
                    $"Provider checks {property.ProviderType} values of {property.VectorDimensions} dimensions on write; no property type constraint was observed or is proposed.");
            else if (types.Length == 0)
            {
                Add(GraphSchemaStatus.Warn, GraphSchemaIssueCategory.PropertyType, "PropertyTypeApplicationEnforced", subject,
                    $"Provider expects {property.ProviderType}; no property type constraint was observed.");
                Propose(GraphSchemaProposalKind.PropertyTypeConstraint, entityType, label, [property.Name],
                    required: false, capabilities.PropertyType, allowProposals, property.ProviderType);
            }
            foreach (var type in types)
            {
                _usedConstraints.Add(type);
                var exact = type.PropertyType == property.ProviderType;
                var knownScalar = type.PropertyType is "STRING" or "INTEGER" or "FLOAT" or "BOOLEAN"
                    or "DATE" or "LOCAL TIME" or "ZONED TIME" or "LOCAL DATETIME" or "ZONED DATETIME"
                    or "DURATION" or "POINT" or "UUID";
                Add(exact ? GraphSchemaStatus.Pass : knownScalar ? GraphSchemaStatus.Fail : GraphSchemaStatus.Warn,
                    GraphSchemaIssueCategory.PropertyType,
                    exact ? "PropertyTypePresent" : knownScalar ? "PropertyTypeConflict" : "PropertyTypeNotCompared", subject,
                    exact ? "Observed exact wire property type."
                        : $"Observed type '{type.PropertyType ?? "unknown"}' differs from expected {property.ProviderType}; no replacement is proposed.");
            }
        }

        private void Index(string entityType, string label, IReadOnlyList<string> properties, bool allowProposals)
        {
            var subject = Subject(entityType, label, properties);
            var matches = observation.Indexes.Where(value => value.Type == "RANGE"
                && OwnerMatches(value.EntityType, value.LabelsOrTypes, entityType, label)
                && value.Properties?.SequenceEqual(properties, StringComparer.Ordinal) == true).ToArray();
            if (matches.Length > 0)
            {
                foreach (var match in matches) _usedIndexes.Add(match);
                var online = matches.Any(value => value.State == "ONLINE");
                Add(online ? GraphSchemaStatus.Pass : GraphSchemaStatus.Warn, GraphSchemaIssueCategory.Index,
                    online ? "RangeIndexPresent" : "RangeIndexNotOnline", subject,
                    online ? "Observed online range index with the exact ordered properties."
                        : "Matching range index exists but is not ONLINE; no duplicate creation is proposed.");
                return;
            }
            var backingConstraint = Constraints(entityType, label)
                .Any(value => (IsUnique(value) || IsKey(value)) && SameSet(value.Properties, properties));
            var plannedConstraint = _proposals.Values.Any(value => value.Kind == GraphSchemaProposalKind.UniquenessConstraint
                && value.EntityType == entityType && value.LabelOrType == label && SameSet(value.Properties, properties));
            if (backingConstraint || plannedConstraint)
            {
                Add(GraphSchemaStatus.Warn, GraphSchemaIssueCategory.Index, "ConstraintBackingIndexRequiresReview", subject,
                    "An observed or proposed uniqueness/key constraint owns the same property-set range index. Verify its order/state; no conflicting standalone index is proposed.");
                return;
            }
            Add(GraphSchemaStatus.Warn, GraphSchemaIssueCategory.Index, "MissingRangeIndex", subject,
                "No online range index covers this optional ordered EF index intent.");
            Propose(GraphSchemaProposalKind.RangeIndex, entityType, label, properties,
                required: false, capabilities.RangeIndexes, allowProposals);
        }

        private void Propose(GraphSchemaProposalKind kind, string entityType, string label,
            IReadOnlyList<string> properties, bool required, GraphSchemaCapabilitySupport support,
            bool allowed, string? propertyType = null)
        {
            if (!allowed) return;
            var subject = Subject(entityType, label, properties);
            if (support != GraphSchemaCapabilitySupport.Supported)
            {
                Add(GraphSchemaStatus.Warn, GraphSchemaIssueCategory.Capability, "ProposalCapabilityUnavailable", subject,
                    $"{kind} support is {support}; no DDL is proposed.");
                return;
            }
            // Uniqueness is a set; RANGE index key order remains significant.
            var names = kind == GraphSchemaProposalKind.UniquenessConstraint
                ? properties.Order(StringComparer.Ordinal).ToArray() : properties.ToArray();
            if (names.Length == 0 || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
                throw new InvalidOperationException("Schema proposals require nonempty, distinct properties.");
            foreach (var name in names) GraphIdentifier.Validate(name, nameof(properties));
            GraphIdentifier.Validate(label, nameof(label));
            var signature = string.Join("|", kind, entityType, label, string.Join(",", names), propertyType ?? "");
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature))).ToLowerInvariant();
            var proposedName = "gb_" + (kind == GraphSchemaProposalKind.RangeIndex ? "ix_" : "ct_") + hash[..24];
            if (_proposals.ContainsKey(proposedName)) return;
            if (observation.Constraints.Any(value => value.Name == proposedName)
                || observation.Indexes.Any(value => value.Name == proposedName))
            {
                Add(GraphSchemaStatus.Fail, GraphSchemaIssueCategory.Conflict, "SchemaNameConflict", proposedName,
                    "The deterministic proposal name is already occupied by a different schema object. Review explicitly; no replacement is proposed.");
                return;
            }
            if (kind == GraphSchemaProposalKind.UniquenessConstraint
                && observation.Indexes.Any(value => value.Type == "RANGE"
                    && OwnerMatches(value.EntityType, value.LabelsOrTypes, entityType, label)
                    && SameSet(value.Properties, names)))
            {
                Add(GraphSchemaStatus.Fail, GraphSchemaIssueCategory.Conflict, "ExistingIndexBlocksConstraint", subject,
                    "An existing range index occupies the required uniqueness schema. Review its ownership before deployment; no drop or replacement is proposed.");
                return;
            }
            var variable = entityType == "NODE" ? "n" : "r";
            var pattern = entityType == "NODE" ? $"(n:`{label}`)" : $"()-[r:`{label}`]-()";
            var slots = string.Join(", ", names.Select(name => $"{variable}.`{name}`"));
            string cypher;
            if (kind == GraphSchemaProposalKind.RangeIndex)
                cypher = $"CREATE RANGE INDEX `{proposedName}` FOR {pattern} ON ({slots})";
            else
            {
                var predicate = kind switch
                {
                    GraphSchemaProposalKind.UniquenessConstraint => $"({slots}) IS UNIQUE",
                    GraphSchemaProposalKind.ExistenceConstraint => $"{slots} IS NOT NULL",
                    GraphSchemaProposalKind.PropertyTypeConstraint when propertyType is "STRING" or "INTEGER" or "FLOAT" or "BOOLEAN"
                        => $"{slots} IS :: {propertyType}",
                    _ => throw new InvalidOperationException("Unsupported schema proposal type.")
                };
                cypher = $"CREATE CONSTRAINT `{proposedName}` FOR {pattern} REQUIRE {predicate}";
            }
            _proposals.Add(proposedName, new GraphSchemaProposal(proposedName, kind, entityType, label, names, required, cypher));
        }

        private IEnumerable<GraphObservedConstraint> Constraints(string entityType, string label)
            => observation.Constraints.Where(value => OwnerMatches(value.EntityType, value.LabelsOrTypes, entityType, label));

        private void Add(GraphSchemaStatus status, GraphSchemaIssueCategory category, string code, string subject, string message)
            => _issues.Add(new GraphSchemaIssue(status, category, code, subject, message));
    }

    private static bool OwnerMatches(string observedEntity, IReadOnlyList<string>? labels, string entityType, string label)
        => observedEntity == entityType && labels is { Count: 1 } && labels[0] == label;
    private static bool SameSet(IReadOnlyList<string>? observed, IReadOnlyList<string> expected)
        => observed is not null && observed.Count == expected.Count
            && new HashSet<string>(observed, StringComparer.Ordinal).SetEquals(expected);
    private static bool IsUnique(GraphObservedConstraint value)
        => value.EntityType == "NODE" && value.Type is "NODE_PROPERTY_UNIQUENESS" or "UNIQUENESS"
            || value.EntityType == "RELATIONSHIP" && value.Type is "RELATIONSHIP_PROPERTY_UNIQUENESS" or "RELATIONSHIP_UNIQUENESS";
    private static bool IsKey(GraphObservedConstraint value)
        => value.EntityType == "NODE" && value.Type == "NODE_KEY"
            || value.EntityType == "RELATIONSHIP" && value.Type == "RELATIONSHIP_KEY";
    private static bool IsExistence(GraphObservedConstraint value)
        => value.Type == value.EntityType + "_PROPERTY_EXISTENCE";
    private static bool IsType(GraphObservedConstraint value)
        => value.Type == value.EntityType + "_PROPERTY_TYPE";
    private static string Subject(string entityType, string label, IEnumerable<string> properties)
        => entityType + ":" + label + "(" + string.Join(",", properties) + ")";
}
