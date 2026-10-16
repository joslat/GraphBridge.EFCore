using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GraphBridge.EntityFrameworkCore.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Neo4j.Driver;

namespace GraphBridge.UnitTests;

public sealed class GraphSchemaManifestTests : IDisposable
{
    private readonly IDriver _driver = ProviderTestDriver.Create();
    public void Dispose() => Assert.Empty(((ProviderTestDriver)(object)_driver).Calls);

    [Fact]
    public void Canonical_versioned_JSON_and_SHA256_have_an_explicit_stable_shape()
    {
        using var context = new ProviderModelContext(_driver, model =>
            model.Entity<SimpleNode>().ToGraphNode("Simple").Property(e => e.Id).HasGraphPropertyName("id").ValueGeneratedNever());
        var manifest = GraphSchemaManifest.Create(context.Model);
        const string expected = "{\"schemaVersion\":1,\"nodes\":[{\"label\":\"Simple\",\"keyProperties\":[\"id\"],\"alternateKeys\":[],\"properties\":[{\"name\":\"id\",\"providerType\":\"INTEGER\",\"isRequired\":true,\"isKey\":true,\"concurrency\":\"None\",\"valueGeneration\":\"Never\",\"beforeSaveBehavior\":\"Save\",\"afterSaveBehavior\":\"Throw\",\"conversion\":\"native\"}],\"indexes\":[],\"deleteBehavior\":\"restrict-existing-relationships\"}],\"relationships\":[],\"caveats\":[]}";
        Assert.Equal(expected, manifest.CanonicalJson);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(expected))).ToLowerInvariant(), manifest.Fingerprint);
        Assert.Matches("^[0-9a-f]{64}$", manifest.Fingerprint);
        using var document = JsonDocument.Parse(manifest.CanonicalJson);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void Renamed_CLR_entities_properties_and_index_names_with_reordered_configuration_have_equal_storage_manifest()
    {
        using var original = Context();
        using var renamed = Context(renamed: true, reverseDeclaration: true);
        var first = GraphSchemaManifest.Create(original.Model);
        var second = GraphSchemaManifest.Create(renamed.Model);
        Assert.Equal(first.CanonicalJson, second.CanonicalJson);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.DoesNotContain(nameof(OriginalSource), first.CanonicalJson);
        Assert.DoesNotContain(nameof(OriginalEdge.SourceTenant), first.CanonicalJson);
        Assert.DoesNotContain(nameof(RenamedEdge.Description), second.CanonicalJson);
        Assert.DoesNotContain("ClrIndex", first.CanonicalJson);
        Assert.DoesNotContain("System.", first.CanonicalJson);
    }

    [Fact]
    public void Repeated_creation_is_culture_independent_and_does_not_touch_driver()
    {
        using var context = Context();
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var first = GraphSchemaManifest.Create(context.Model);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var second = GraphSchemaManifest.Create(context.Model);
            Assert.Equal(first.Fingerprint, second.Fingerprint);
            Assert.Equal(first.CanonicalJson, second.CanonicalJson);
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
    }

    [Theory]
    [InlineData("label")]
    [InlineData("relationship-type")]
    [InlineData("key-store-name")]
    [InlineData("payload-store-name")]
    [InlineData("provider-type")]
    [InlineData("nullable")]
    [InlineData("concurrency")]
    [InlineData("version")]
    [InlineData("direction")]
    [InlineData("delete")]
    [InlineData("key-order")]
    [InlineData("index-order")]
    [InlineData("index-unique")]
    public void Changed_storage_semantics_change_fingerprint(string variant)
    {
        using var baseline = Context();
        using var changed = Context(variant: variant);
        Assert.NotEqual(GraphSchemaManifest.Create(baseline.Model).Fingerprint, GraphSchemaManifest.Create(changed.Model).Fingerprint);
    }

    [Fact]
    public void Pair_and_identified_relationships_have_distinct_identity_and_no_phantom_FK_properties()
    {
        using var identified = Context();
        using var pair = Context(identified: false);
        var identifiedManifest = GraphSchemaManifest.Create(identified.Model);
        var pairManifest = GraphSchemaManifest.Create(pair.Model);
        var edge = Assert.Single(identifiedManifest.Relationships);
        var association = Assert.Single(pairManifest.Relationships);
        Assert.Equal(GraphSchemaRelationshipIdentity.ApplicationId, edge.Identity);
        Assert.Equal("edge_id", edge.KeyProperty);
        Assert.Equal(GraphSchemaRelationshipIdentity.EndpointPair, association.Identity);
        Assert.Null(association.KeyProperty);
        Assert.NotEqual(identifiedManifest.Fingerprint, pairManifest.Fingerprint);
        Assert.Equal("source-to-target", edge.Direction);
        Assert.Equal(new[] { "tenant", "source_key" }, edge.Source.KeyProperties);
        Assert.Equal(new[] { "tenant", "target_key" }, edge.Target.KeyProperties);
        Assert.DoesNotContain(edge.Properties, p => p.Name.StartsWith("fk_", StringComparison.Ordinal));
        Assert.DoesNotContain(association.Properties, p => p.IsKey);
        Assert.True(Assert.Single(edge.Properties, p => p.Name == "edge_id").IsKey);
        Assert.Equal("guid-d-string", Assert.Single(edge.Properties, p => p.Name == "edge_id").Conversion);
        Assert.Equal(GraphSchemaConcurrency.IncrementingInt64, Assert.Single(edge.Properties, p => p.Name == "version").Concurrency);
        Assert.Equal(GraphSchemaConcurrency.OriginalValue, Assert.Single(edge.Properties, p => p.Name == "token").Concurrency);
        Assert.Equal("relationship-only", edge.DeleteBehavior);
        Assert.All(identifiedManifest.Nodes, n => Assert.Equal("restrict-existing-relationships", n.DeleteBehavior));
    }

    [Fact]
    public void Opaque_converters_expose_semantic_parity_limit_instead_of_hashing_lambda_or_CLR_names()
    {
        using var first = Context(variant: "converter-a");
        using var second = Context(variant: "converter-b");
        using var native = Context();
        var a = GraphSchemaManifest.Create(first.Model);
        var b = GraphSchemaManifest.Create(second.Model);
        Assert.Equal(a.Fingerprint, b.Fingerprint);
        Assert.NotEqual(a.Fingerprint, GraphSchemaManifest.Create(native.Model).Fingerprint);
        Assert.Equal("opaque", Assert.Single(Assert.Single(a.Relationships).Properties, p => p.Name == "role").Conversion);
        Assert.Contains(a.Caveats, c => c.Contains("relationship:EVENT.role: opaque conversion", StringComparison.Ordinal)
            && c.Contains("not converter semantic equivalence", StringComparison.Ordinal));
    }

    [Fact]
    public void Indexes_use_ordered_stored_names_deduplicate_equivalent_intent_and_report_endpoint_slot_exclusion()
    {
        using var normal = Context();
        using var duplicate = Context(variant: "duplicate-index");
        using var mixed = Context(variant: "mixed-index");
        var manifest = GraphSchemaManifest.Create(normal.Model);
        Assert.Equal(manifest.Fingerprint, GraphSchemaManifest.Create(duplicate.Model).Fingerprint);
        var edge = Assert.Single(manifest.Relationships);
        var index = Assert.Single(edge.Indexes);
        Assert.Equal(new[] { "role", "measurement" }, index.Properties);
        Assert.False(index.IsUnique);
        var mixedManifest = GraphSchemaManifest.Create(mixed.Model);
        Assert.Single(Assert.Single(mixedManifest.Relationships).Indexes);
        Assert.Contains(mixedManifest.Caveats, c => c.Contains("indexes containing endpoint slots are excluded", StringComparison.Ordinal));
        Assert.DoesNotContain(Assert.Single(mixedManifest.Relationships).Indexes.SelectMany(i => i.Properties), p => p.StartsWith("fk_", StringComparison.Ordinal));
        Assert.Contains(manifest.Caveats, c => c.Contains("sort direction", StringComparison.Ordinal));
    }

    [Fact]
    public void Manifest_and_nested_collections_are_read_only_copies()
    {
        using var context = Context();
        var manifest = GraphSchemaManifest.Create(context.Model);
        Assert.Throws<NotSupportedException>(() => ((IList<GraphSchemaNode>)manifest.Nodes).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<GraphSchemaRelationship>)manifest.Relationships).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)manifest.Caveats).Clear());
        var edge = Assert.Single(manifest.Relationships);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)edge.Source.KeyProperties).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<GraphSchemaProperty>)edge.Properties).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<GraphSchemaIndex>)edge.Indexes).Clear());
        var names = new[] { "a", "b" };
        var index = new GraphSchemaIndex(names, false);
        names[0] = "changed";
        Assert.Equal(new[] { "a", "b" }, index.Properties);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)index.Properties).Clear());
    }

    [Fact]
    public void Uninitialized_model_is_rejected_before_compiling_a_manifest()
    {
        Assert.Throws<ArgumentNullException>(() => GraphSchemaManifest.Create(null!));
        var builder = new ModelBuilder();
        builder.Entity<SimpleNode>().ToGraphNode("Simple");
        Assert.Throws<InvalidOperationException>(() => GraphSchemaManifest.Create((IModel)builder.Model));
    }

    [Fact]
    public void Composite_alternate_key_separate_alternate_keys_and_no_alternate_key_are_distinct_contracts()
    {
        using var composite = AlternateContext("composite");
        using var separate = AlternateContext("separate");
        using var none = AlternateContext("none");
        var compositeManifest = GraphSchemaManifest.Create(composite.Model);
        var separateManifest = GraphSchemaManifest.Create(separate.Model);
        var noKeysManifest = GraphSchemaManifest.Create(none.Model);
        Assert.Equal(3, new[] { compositeManifest.Fingerprint, separateManifest.Fingerprint, noKeysManifest.Fingerprint }.Distinct().Count());
        var compositeNode = Assert.Single(compositeManifest.Nodes);
        var separateNode = Assert.Single(separateManifest.Nodes);
        Assert.Equal(new[] { "a", "b" }, Assert.Single(compositeNode.AlternateKeys).Properties);
        Assert.Equal(2, separateNode.AlternateKeys.Count);
        Assert.Equal(new[] { "a" }, separateNode.AlternateKeys[0].Properties);
        Assert.Equal(new[] { "b" }, separateNode.AlternateKeys[1].Properties);
        Assert.All(compositeNode.AlternateKeys.Concat(separateNode.AlternateKeys), key => Assert.True(key.IsUnique));
        Assert.Empty(Assert.Single(noKeysManifest.Nodes).AlternateKeys);
        Assert.Equal(new[] { "id" }, compositeNode.KeyProperties);
        Assert.Empty(compositeNode.Indexes);
        Assert.All(compositeNode.Properties, property => Assert.True(property.IsKey));
        using var document = JsonDocument.Parse(compositeManifest.CanonicalJson);
        Assert.Equal(1, document.RootElement.GetProperty("nodes")[0].GetProperty("alternateKeys").GetArrayLength());
    }

    [Theory]
    [InlineData("composite")]
    [InlineData("separate")]
    public void Alternate_keys_are_immutable_and_ignore_CLR_names_and_declaration_order(string mode)
    {
        using var original = AlternateContext(mode);
        using var renamed = AlternateContext(mode, renamed: true);
        var originalManifest = GraphSchemaManifest.Create(original.Model);
        var renamedManifest = GraphSchemaManifest.Create(renamed.Model);
        Assert.Equal(originalManifest.CanonicalJson, renamedManifest.CanonicalJson);
        var keys = Assert.Single(originalManifest.Nodes).AlternateKeys;
        Assert.Throws<NotSupportedException>(() => ((IList<GraphSchemaIndex>)keys).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)keys[0].Properties).Clear());
        var input = new List<GraphSchemaIndex> { new(["a", "b"], true) };
        var copy = new GraphSchemaNode("Copy", ["id"], [], [], input);
        input.Clear();
        Assert.Single(copy.AlternateKeys);
    }

    private ProviderModelContext AlternateContext(string mode, bool renamed = false) => new(_driver, model =>
    {
        var entity = model.Entity(renamed ? typeof(RenamedAlternateNode) : typeof(AlternateNode));
        var id = renamed ? "Identifier" : "Id";
        var a = renamed ? "Left" : "A";
        var b = renamed ? "Right" : "B";
        entity.ToGraphNode("AlternateStore");
        entity.Property<long>(id).HasGraphPropertyName("id").ValueGeneratedNever();
        entity.Property<string>(a).HasGraphPropertyName("a").IsRequired();
        entity.Property<string>(b).HasGraphPropertyName("b").IsRequired();
        entity.HasKey(id);
        if (mode == "composite") entity.HasAlternateKey(a, b);
        if (mode == "separate")
        {
            if (renamed) { entity.HasAlternateKey(b); entity.HasAlternateKey(a); }
            else { entity.HasAlternateKey(a); entity.HasAlternateKey(b); }
        }
    });

    private ProviderModelContext Context(bool renamed = false, bool reverseDeclaration = false,
        bool identified = true, string? variant = null)
        => new(_driver, model => Configure(model, renamed, reverseDeclaration, identified, variant));

    private static void Configure(ModelBuilder model, bool renamed, bool reverse, bool identified, string? variant)
    {
        var sourceType = renamed ? typeof(RenamedSource) : typeof(OriginalSource);
        var targetType = renamed ? typeof(RenamedTarget) : typeof(OriginalTarget);
        var edgeType = renamed ? typeof(RenamedEdge) : typeof(OriginalEdge);
        var tenant = renamed ? "Partition" : "Tenant";
        var id = renamed ? "BusinessId" : "Id";
        var caption = renamed ? "Caption" : "Name";
        var edgeId = renamed ? "EventKey" : "Id";
        var sourceTenant = renamed ? "FromTenant" : "SourceTenant";
        var sourceId = renamed ? "FromId" : "SourceId";
        var targetTenant = renamed ? "ToTenant" : "TargetTenant";
        var targetId = renamed ? "ToId" : "TargetId";
        var role = renamed ? "Description" : "Role";
        var version = renamed ? "Revision" : "Version";
        var token = renamed ? "Stamp" : "Token";
        var measure = renamed ? "Reading" : "Measure";
        if (reverse) { model.Entity(edgeType); model.Entity(targetType); model.Entity(sourceType); }
        var source = model.Entity(sourceType);
        source.ToGraphNode(variant == "label" ? "OtherSource" : "SourceStore");
        source.Property<long>(tenant).HasGraphPropertyName("tenant");
        source.Property<Guid>(id).HasGraphPropertyName(variant == "key-store-name" ? "other_key" : "source_key").ValueGeneratedNever();
        source.Property<string?>(caption).HasGraphPropertyName("name");
        source.HasKey(variant == "key-order" ? [id, tenant] : [tenant, id]);
        source.HasIndex([caption], renamed ? "RenamedClrIndex" : "OriginalClrIndex");
        var target = model.Entity(targetType);
        target.ToGraphNode("TargetStore");
        target.Property<long>(tenant).HasGraphPropertyName("tenant");
        target.Property<Guid>(id).HasGraphPropertyName("target_key").ValueGeneratedNever();
        target.HasKey(tenant, id);
        var edge = model.Entity(edgeType);
        edge.Property<long>(sourceTenant).HasGraphPropertyName("fk_source_tenant");
        edge.Property<Guid>(sourceId).HasGraphPropertyName("fk_source_id");
        edge.Property<long>(targetTenant).HasGraphPropertyName("fk_target_tenant");
        edge.Property<Guid>(targetId).HasGraphPropertyName("fk_target_id");
        edge.Property<string?>(role).HasGraphPropertyName(variant == "payload-store-name" ? "other_role" : "role").IsRequired(variant == "nullable");
        if (variant == "provider-type") edge.Property<double>(measure).HasGraphPropertyName("measurement");
        else edge.Property<long>(measure).HasGraphPropertyName("measurement");
        if (variant == "version") edge.Property<long>(version).HasGraphPropertyName("version").IsConcurrencyToken();
        else edge.Property<long>(version).HasGraphPropertyName("version").IsRowVersion();
        edge.Property<long>(token).HasGraphPropertyName("token").IsConcurrencyToken(variant != "concurrency");
        if (variant == "converter-a") edge.Property<string?>(role).HasConversion(v => v == null ? null : "a" + v, v => v == null ? null : v.Substring(1));
        if (variant == "converter-b") edge.Property<string?>(role).HasConversion(v => v == null ? null : "b" + v, v => v == null ? null : v.Substring(1));
        string[] sourceSlots = variant == "key-order" ? [sourceId, sourceTenant] : [sourceTenant, sourceId];
        string[] targetSlots = [targetTenant, targetId];
        edge.HasOne(sourceType).WithMany().HasForeignKey(sourceSlots).OnDelete(variant == "delete" ? DeleteBehavior.Restrict : DeleteBehavior.Cascade);
        edge.HasOne(targetType).WithMany().HasForeignKey(targetSlots).OnDelete(DeleteBehavior.Cascade);
        if (variant == "direction") (sourceSlots, targetSlots) = (targetSlots, sourceSlots);
        if (identified)
        {
            edge.Property<Guid>(edgeId).HasGraphPropertyName("edge_id").ValueGeneratedNever();
            edge.HasKey(edgeId);
            edge.ToGraphIdentifiedRelationship(variant == "relationship-type" ? "OTHER_EVENT" : "EVENT", edgeId, sourceSlots, targetSlots);
        }
        else
        {
            edge.Ignore(edgeId);
            edge.HasKey(sourceSlots.Concat(targetSlots).ToArray());
            edge.ToGraphRelationship("EVENT", sourceSlots, targetSlots);
        }
        string[] indexSlots = variant == "index-order" ? [measure, role] : [role, measure];
        edge.HasIndex(indexSlots, renamed ? "RenamedEdgeClrIndex" : "OriginalEdgeClrIndex").IsUnique(variant == "index-unique");
        if (variant == "duplicate-index") edge.HasIndex(indexSlots, "SecondNameForSameStorageIndex");
        // Explicitly model a conventional-shaped endpoint index to verify it never becomes an edge property index.
        edge.HasIndex(sourceSlots);
        if (variant == "mixed-index") edge.HasIndex([sourceSlots[0], role], "UnsupportedMixedIndex");
        if (reverse)
        {
            edge.HasAnnotation("Ignored:Authoring", "different-order");
            source.Property<Guid>(id).HasGraphPropertyName("source_key");
            edge.Property<string?>(role).HasGraphPropertyName("role");
        }
    }

    public sealed class SimpleNode { public long Id { get; set; } }
    public sealed class AlternateNode { public long Id { get; set; } public string A { get; set; } = ""; public string B { get; set; } = ""; }
    public sealed class RenamedAlternateNode { public long Identifier { get; set; } public string Left { get; set; } = ""; public string Right { get; set; } = ""; }
    public sealed class OriginalSource { public long Tenant { get; set; } public Guid Id { get; set; } public string? Name { get; set; } }
    public sealed class RenamedSource { public long Partition { get; set; } public Guid BusinessId { get; set; } public string? Caption { get; set; } }
    public sealed class OriginalTarget { public long Tenant { get; set; } public Guid Id { get; set; } }
    public sealed class RenamedTarget { public long Partition { get; set; } public Guid BusinessId { get; set; } }
    public sealed class OriginalEdge
    {
        public Guid Id { get; set; }
        public long SourceTenant { get; set; }
        public Guid SourceId { get; set; }
        public long TargetTenant { get; set; }
        public Guid TargetId { get; set; }
        public string? Role { get; set; }
        public long Version { get; set; }
        public long Token { get; set; }
    }
    public sealed class RenamedEdge
    {
        public Guid EventKey { get; set; }
        public long FromTenant { get; set; }
        public Guid FromId { get; set; }
        public long ToTenant { get; set; }
        public Guid ToId { get; set; }
        public string? Description { get; set; }
        public long Revision { get; set; }
        public long Stamp { get; set; }
    }
}
