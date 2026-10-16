using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GraphBridge.EntityFrameworkCore.Schema;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.UnitTests;

public sealed class NodeReferenceManifestTests : IDisposable
{
    private readonly IDriver _driver = ProviderTestDriver.Create();
    public void Dispose() => Assert.Empty(((ProviderTestDriver)(object)_driver).Calls);

    [Theory]
    [InlineData(DeleteBehavior.Restrict)]
    [InlineData(DeleteBehavior.NoAction)]
    public void Manifest_describes_principal_to_dependent_ownership_without_phantom_storage(DeleteBehavior deletion)
    {
        using var context = Context(deletion: deletion);
        var manifest = context.Database.GetGraphSchemaManifest();
        var dependent = Assert.Single(manifest.Nodes, node => node.Label == "PostStore");
        Assert.Equal(new[] { "post_id", "title" }, dependent.Properties.Select(property => property.Name));
        Assert.Equal(new[] { "post_id" }, dependent.KeyProperties);
        var relationship = Assert.Single(manifest.Relationships);
        Assert.Equal(GraphSchemaRelationshipIdentity.DependentNode, relationship.Identity);
        Assert.Equal("HAS_POST", relationship.RelationshipType);
        Assert.Equal("source-to-target", relationship.Direction);
        Assert.Equal("BlogStore", relationship.Source.NodeLabel);
        Assert.Equal(new[] { "blog_id" }, relationship.Source.KeyProperties);
        Assert.Equal("PostStore", relationship.Target.NodeLabel);
        Assert.Equal(new[] { "post_id" }, relationship.Target.KeyProperties);
        Assert.Equal(deletion.ToString(), relationship.Source.EfDeleteBehavior);
        Assert.Equal(deletion.ToString(), relationship.Target.EfDeleteBehavior);
        Assert.Equal("remove-with-dependent-restrict-principal", relationship.DeleteBehavior);
        Assert.Equal(1, relationship.RequiredIncomingLinks);
        Assert.Null(relationship.KeyProperty);
        Assert.Empty(relationship.Properties);
        Assert.Empty(relationship.Indexes);
        var slot = Assert.Single(relationship.DerivedForeignKeys);
        Assert.Equal("BlogId", slot.DependentSlot);
        Assert.Equal("blog_id", slot.PrincipalKeyProperty);
        using var json = JsonDocument.Parse(manifest.CanonicalJson);
        var witness = Assert.Single(json.RootElement.GetProperty("relationships").EnumerateArray());
        Assert.Equal(1, witness.GetProperty("requiredIncomingLinks").GetInt32());
        Assert.False(witness.TryGetProperty("derivedForeignKeys", out _));
        Assert.DoesNotContain("BlogId", manifest.CanonicalJson);
        Assert.Contains(manifest.Caveats, caveat => caveat.Contains("endpoint key uniqueness does not enforce", StringComparison.Ordinal));
    }

    [Fact]
    public void Renamed_CLR_entities_slots_and_declaration_order_preserve_storage_witness()
    {
        using var first = Context();
        using var renamed = Context(renamed: true, reverse: true);
        Assert.Equal(GraphSchemaManifest.Create(first.Model).CanonicalJson, GraphSchemaManifest.Create(renamed.Model).CanonicalJson);
        Assert.Equal(GraphSchemaManifest.Create(first.Model).Fingerprint, GraphSchemaManifest.Create(renamed.Model).Fingerprint);
        var text = GraphSchemaManifest.Create(renamed.Model).CanonicalJson;
        Assert.DoesNotContain(nameof(RenamedPost), text);
        Assert.DoesNotContain(nameof(RenamedPost.OwnerIdentifier), text);
    }

    [Fact]
    public void Repeated_generation_is_culture_independent_and_derived_collections_are_immutable()
    {
        using var context = Context();
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var first = GraphSchemaManifest.Create(context.Model);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var second = GraphSchemaManifest.Create(context.Model);
            Assert.Equal(first.CanonicalJson, second.CanonicalJson);
            Assert.Equal(first.Fingerprint, second.Fingerprint);
            var relationship = Assert.Single(first.Relationships);
            Assert.Throws<NotSupportedException>(() => ((IList<GraphSchemaDerivedForeignKey>)relationship.DerivedForeignKeys).Clear());
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    [Theory]
    [InlineData("type")]
    [InlineData("source-label")]
    [InlineData("target-label")]
    [InlineData("principal-key")]
    [InlineData("dependent-key")]
    [InlineData("delete")]
    public void Changed_reference_contract_changes_fingerprint(string variant)
    {
        using var first = Context();
        using var second = Context(variant: variant);
        Assert.NotEqual(GraphSchemaManifest.Create(first.Model).Fingerprint, GraphSchemaManifest.Create(second.Model).Fingerprint);
    }

    [Fact]
    public void Logical_derived_slot_name_is_review_metadata_outside_the_storage_fingerprint()
    {
        using var first = Context();
        using var renamedSlot = Context(renamed: true);
        var baseline = GraphSchemaManifest.Create(first.Model);
        var changed = GraphSchemaManifest.Create(renamedSlot.Model);
        Assert.NotEqual(baseline.Relationships[0].DerivedForeignKeys[0].DependentSlot, changed.Relationships[0].DerivedForeignKeys[0].DependentSlot);
        Assert.Equal(baseline.CanonicalJson, changed.CanonicalJson);
        Assert.Equal(baseline.Fingerprint, changed.Fingerprint);
        Assert.Contains(baseline.Caveats, caveat => caveat.Contains("not logical/CLR FK slot bindings", StringComparison.Ordinal));
    }

    [Fact]
    public void Composite_reference_preserves_ordered_key_and_slot_derivations()
    {
        using var context = CompositeContext();
        using var reordered = CompositeContext(reverseKeys: true);
        using var remapped = CompositeContext(reverseForeignKeys: true);
        var manifest = GraphSchemaManifest.Create(context.Model);
        var relationship = Assert.Single(manifest.Relationships);
        Assert.Equal(new[] { "tenant", "code" }, relationship.Source.KeyProperties);
        Assert.Equal(new[] { "BlogTenant", "BlogCode" }, relationship.DerivedForeignKeys.Select(slot => slot.DependentSlot));
        Assert.Equal(new[] { "tenant", "code" }, relationship.DerivedForeignKeys.Select(slot => slot.PrincipalKeyProperty));
        Assert.Equal(new[] { "post_id" }, relationship.Target.KeyProperties);
        Assert.DoesNotContain(manifest.Nodes.Single(n => n.Label == "PostStore").Properties, p => p.Name is "BlogTenant" or "BlogCode");
        Assert.NotEqual(manifest.Fingerprint, GraphSchemaManifest.Create(reordered.Model).Fingerprint);
        var remappedManifest = GraphSchemaManifest.Create(remapped.Model);
        Assert.NotEqual(relationship.DerivedForeignKeys[0].DependentSlot, remappedManifest.Relationships[0].DerivedForeignKeys[0].DependentSlot);
        Assert.Equal(manifest.Fingerprint, remappedManifest.Fingerprint);
    }

    [Fact]
    public void Stored_indexes_remain_while_derived_and_mixed_indexes_are_excluded_with_accurate_caveat()
    {
        using var context = Context(variant: "indexes");
        var manifest = GraphSchemaManifest.Create(context.Model);
        var post = manifest.Nodes.Single(n => n.Label == "PostStore");
        Assert.Equal(new[] { "title" }, Assert.Single(post.Indexes).Properties);
        Assert.DoesNotContain(post.Indexes.SelectMany(i => i.Properties), p => p == "BlogId");
        Assert.Contains(manifest.Caveats, caveat => caveat.Contains("indexes containing derived FK slots are excluded", StringComparison.Ordinal)
            && caveat.Contains("Mixed stored/derived indexes", StringComparison.Ordinal));
        Assert.DoesNotContain(manifest.Caveats, caveat => caveat.Contains("not stored relationship properties", StringComparison.Ordinal));
    }

    [Fact]
    public void Reversing_principal_and_dependent_roles_changes_the_storage_fingerprint()
    {
        var forward = new GraphSchemaRelationship("HAS_POST", GraphSchemaRelationshipIdentity.DependentNode,
            null, new("BlogStore", ["blog_id"], "Restrict"), new("PostStore", ["post_id"], "Restrict"), [], [], [new("BlogId", "blog_id")]);
        var reverse = new GraphSchemaRelationship("HAS_POST", GraphSchemaRelationshipIdentity.DependentNode,
            null, new("PostStore", ["post_id"], "Restrict"), new("BlogStore", ["blog_id"], "Restrict"), [], [], [new("PostId", "post_id")]);
        Assert.NotEqual(new GraphSchemaManifest([], [forward], []).Fingerprint, new GraphSchemaManifest([], [reverse], []).Fingerprint);
    }

    [Theory]
    [InlineData("community")]
    [InlineData("enterprise")]
    public void Schema_comparison_proposes_endpoint_constraints_but_no_fictional_cardinality_DDL(string edition)
    {
        using var context = Context(variant: "indexes");
        var manifest = GraphSchemaManifest.Create(context.Model);
        var capabilities = GraphSchemaCapabilities.Detect("2026.09.0", edition, ["5", "25"]);
        var result = GraphSchemaComparer.Compare(manifest, capabilities, new([], []));
        Assert.Equal(2, result.Proposals.Count(p => p.Kind == GraphSchemaProposalKind.UniquenessConstraint));
        Assert.All(result.Proposals, proposal =>
        {
            Assert.Equal("NODE", proposal.EntityType);
            Assert.DoesNotContain("HAS_POST", proposal.Cypher);
            Assert.DoesNotContain("BlogId", proposal.Cypher);
        });
        Assert.Contains(result.Issues, issue => issue.Code == "DependentNodeCardinalityApplicationEnforced" && issue.Status == GraphSchemaStatus.Warn);
        Assert.Contains(result.Issues, issue => issue.Code == "DependentNodeShapeNotValidated" && issue.Status == GraphSchemaStatus.Warn);
        Assert.DoesNotContain(result.Issues, issue => issue.Code == "EndpointPairApplicationEnforced");
        var observed = new GraphSchemaObservation([
            new("blog", "UNIQUENESS", "NODE", ["BlogStore"], ["blog_id"]),
            new("post", "UNIQUENESS", "NODE", ["PostStore"], ["post_id"])], []);
        var covered = GraphSchemaComparer.Compare(manifest, capabilities, observed);
        Assert.Equal(GraphSchemaStatus.Warn, covered.Status);
        Assert.DoesNotContain(covered.Issues, issue => issue.Code == "MissingIdentityConstraint");
        Assert.Contains(covered.Issues, issue => issue.Code == "DependentNodeCardinalityApplicationEnforced");
        Assert.Contains(covered.Issues, issue => issue.Code == "DependentNodeShapeNotValidated");
    }

    [Fact]
    public void Relationship_required_property_and_phantom_dependent_FK_constraints_report_conflicts()
    {
        using var context = Context();
        var result = GraphSchemaComparer.Compare(GraphSchemaManifest.Create(context.Model),
            GraphSchemaCapabilities.Detect("2026.09.0", "enterprise", ["25"]), new([
                new("edge_payload", "RELATIONSHIP_PROPERTY_EXISTENCE", "RELATIONSHIP", ["HAS_POST"], ["weight"]),
                new("phantom_fk", "NODE_PROPERTY_EXISTENCE", "NODE", ["PostStore"], ["BlogId"])], []));
        Assert.Contains(result.Issues, issue => issue.Code == "UnmappedRequiredProperty" && issue.Subject == "edge_payload" && issue.Status == GraphSchemaStatus.Fail);
        Assert.Contains(result.Issues, issue => issue.Code == "UnmappedRequiredProperty" && issue.Subject == "phantom_fk" && issue.Status == GraphSchemaStatus.Fail);
        Assert.DoesNotContain(result.Proposals, proposal => proposal.EntityType == "RELATIONSHIP");
    }

    [Fact]
    public void Composite_endpoint_uniqueness_does_not_become_a_link_constraint()
    {
        using var context = CompositeContext();
        var result = GraphSchemaComparer.Compare(GraphSchemaManifest.Create(context.Model),
            GraphSchemaCapabilities.Detect("2026.09.0", "community", ["5", "25"]), new([], []));
        var principal = Assert.Single(result.Proposals, p => p.LabelOrType == "BlogStore" && p.Kind == GraphSchemaProposalKind.UniquenessConstraint);
        Assert.Equal(new[] { "code", "tenant" }, principal.Properties);
        Assert.All(result.Proposals, p => Assert.Equal("NODE", p.EntityType));
    }

    [Fact]
    public void Legacy_relationship_canonical_bytes_are_exactly_preserved_for_both_prior_identities()
    {
        foreach (var identity in new[] { GraphSchemaRelationshipIdentity.EndpointPair, GraphSchemaRelationshipIdentity.ApplicationId })
        {
            var edge = new GraphSchemaRelationship("LINK", identity, identity == GraphSchemaRelationshipIdentity.ApplicationId ? "edge_id" : null,
                new("Source", ["id"], "Restrict"), new("Target", ["id"], "NoAction"), [], []);
            var manifest = new GraphSchemaManifest([], [edge], []);
            var key = identity == GraphSchemaRelationshipIdentity.ApplicationId ? "\"edge_id\"" : "null";
            var expected = "{\"schemaVersion\":1,\"nodes\":[],\"relationships\":[{\"relationshipType\":\"LINK\",\"identity\":\"" + identity
                + "\",\"keyProperty\":" + key + ",\"direction\":\"source-to-target\",\"source\":{\"nodeLabel\":\"Source\",\"keyProperties\":[\"id\"],\"efDeleteBehavior\":\"Restrict\"},"
                + "\"target\":{\"nodeLabel\":\"Target\",\"keyProperties\":[\"id\"],\"efDeleteBehavior\":\"NoAction\"},\"properties\":[],\"indexes\":[],\"deleteBehavior\":\"relationship-only\"}],\"caveats\":[]}";
            Assert.Equal(expected, manifest.CanonicalJson);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(expected))).ToLowerInvariant(), manifest.Fingerprint);
            Assert.Empty(edge.DerivedForeignKeys);
            Assert.Null(edge.RequiredIncomingLinks);
        }
    }

    [Fact]
    public void Unknown_identity_and_invalid_dependent_witness_cannot_silently_become_an_association()
    {
        Assert.Throws<NotSupportedException>(() => new GraphSchemaRelationship("LINK", (GraphSchemaRelationshipIdentity)99,
            null, new("Source", ["id"], "Restrict"), new("Target", ["id"], "Restrict"), [], []));
        Assert.Throws<ArgumentException>(() => new GraphSchemaRelationship("LINK", GraphSchemaRelationshipIdentity.DependentNode,
            null, new("Source", ["id"], "Restrict"), new("Target", ["id"], "Restrict"), [], []));
        var slots = new List<GraphSchemaDerivedForeignKey> { new("parent_slot", "id") };
        var relationship = new GraphSchemaRelationship("LINK", GraphSchemaRelationshipIdentity.DependentNode,
            null, new("Source", ["id"], "Restrict"), new("Target", ["id"], "Restrict"), [], [], slots);
        slots.Clear();
        Assert.Single(relationship.DerivedForeignKeys);
    }

    private ProviderModelContext Context(bool renamed = false, bool reverse = false, DeleteBehavior deletion = DeleteBehavior.Restrict, string? variant = null)
        => new(_driver, model =>
        {
            var blogType = renamed ? typeof(RenamedBlog) : typeof(Blog);
            var postType = renamed ? typeof(RenamedPost) : typeof(Post);
            var id = renamed ? "Identifier" : "Id";
            var fk = renamed ? "OwnerIdentifier" : "BlogId";
            var title = renamed ? "Caption" : "Title";
            if (reverse) { model.Entity(postType); model.Entity(blogType); }
            var blog = model.Entity(blogType).ToGraphNode(variant == "source-label" ? "OtherBlog" : "BlogStore");
            blog.Property<long>(id).HasGraphPropertyName(variant == "principal-key" ? "other_blog_id" : "blog_id").ValueGeneratedNever();
            blog.HasKey(id);
            var post = model.Entity(postType).ToGraphNode(variant == "target-label" ? "OtherPost" : "PostStore");
            post.Property<long>(id).HasGraphPropertyName(variant == "dependent-key" ? "other_post_id" : "post_id").ValueGeneratedNever();
            post.HasKey(id);
            post.Property<long>(fk);
            post.Property<string>(title).HasGraphPropertyName("title").IsRequired();
            post.HasOne(blogType).WithMany().HasForeignKey(fk).IsRequired()
                .OnDelete(variant == "delete" ? DeleteBehavior.NoAction : deletion).ToGraphReference(variant == "type" ? "OTHER_POST" : "HAS_POST");
            post.HasIndex(fk); // Explicitly verify exclusion, irrespective of EF index conventions.
            if (variant == "indexes")
            {
                post.HasIndex(title);
                post.HasIndex([fk, title], "MixedStoredAndDerived").IsUnique();
            }
        });

    private ProviderModelContext CompositeContext(bool reverseKeys = false, bool reverseForeignKeys = false)
        => new(_driver, model =>
        {
            var blog = model.Entity<CompositeBlog>().ToGraphNode("BlogStore");
            blog.Property(b => b.Tenant).HasGraphPropertyName("tenant");
            blog.Property(b => b.Code).HasGraphPropertyName("code");
            blog.HasKey(reverseKeys ? [nameof(CompositeBlog.Code), nameof(CompositeBlog.Tenant)] : [nameof(CompositeBlog.Tenant), nameof(CompositeBlog.Code)]);
            var post = model.Entity<CompositePost>().ToGraphNode("PostStore");
            post.Property(p => p.Id).HasGraphPropertyName("post_id").ValueGeneratedNever();
            var reverse = reverseKeys || reverseForeignKeys;
            post.HasOne<CompositeBlog>().WithMany().HasForeignKey(reverse
                ? [nameof(CompositePost.BlogCode), nameof(CompositePost.BlogTenant)]
                : [nameof(CompositePost.BlogTenant), nameof(CompositePost.BlogCode)])
                .IsRequired().OnDelete(DeleteBehavior.Restrict).ToGraphReference("HAS_POST");
        });

    public sealed class Blog { public long Id { get; set; } }
    public sealed class Post { public long Id { get; set; } public long BlogId { get; set; } public string Title { get; set; } = ""; }
    public sealed class RenamedBlog { public long Identifier { get; set; } }
    public sealed class RenamedPost { public long Identifier { get; set; } public long OwnerIdentifier { get; set; } public string Caption { get; set; } = ""; }
    public sealed class CompositeBlog { public long Tenant { get; set; } public long Code { get; set; } }
    public sealed class CompositePost { public long Id { get; set; } public long BlogTenant { get; set; } public long BlogCode { get; set; } }
}
