using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.EntityFrameworkCore.Schema;

namespace GraphBridge.UnitTests;

/// <summary>Vector and full-text index steps (ADR 0053): rendering, checksums, validation, matching and capabilities.</summary>
public sealed class GraphMigrationSearchIndexTests
{
    [Fact]
    public void Plans_of_the_earlier_kinds_keep_their_canonical_checksum()
    {
        // Computed with the provider before vector and full-text kinds existed (commit 1a29dcf).
        // A change here would make every applied migration's journal checksum mismatch its plan.
        var plan = Plan(
            GraphMigrationStep.CreateConstraint("unique", Definition("reviewed_schema", properties: ["tenant", "id"])),
            GraphMigrationStep.CreateConstraint("rel-unique", Definition("rel_schema", entityType: "RELATIONSHIP")),
            GraphMigrationStep.CreateConstraint("exists", Definition("exists_schema", GraphMigrationSchemaKind.ExistenceConstraint)),
            GraphMigrationStep.CreateConstraint("type", Definition("type_schema", GraphMigrationSchemaKind.PropertyTypeConstraint, propertyType: "STRING")),
            GraphMigrationStep.CreateIndex("range", Definition("range_schema", GraphMigrationSchemaKind.RangeIndex)),
            GraphMigrationStep.DropIndex("drop-range", Definition("old_range", GraphMigrationSchemaKind.RangeIndex), "Obsolete."),
            GraphMigrationStep.DropConstraint("drop-unique", Definition("old_unique"), "Replaced."),
            GraphMigrationStep.RenameProperty("rename", Scope(new() { ["tenant"] = 7 }), "old", "new", 2, "Reviewed rename."),
            GraphMigrationStep.Backfill("fill", Scope(), "active", true, 2),
            GraphMigrationStep.AssertPrecondition("check", Scope(), 2));
        Assert.Equal("ece2157a719e9deffa466a555d1b059924a6490dcacb269bdefc88a9e98b8876", plan.Checksum);
        Assert.DoesNotContain("labelsOrTypes", plan.CanonicalJson);
        Assert.DoesNotContain("vectorDimensions", plan.CanonicalJson);
    }

    [Theory]
    [InlineData("NODE", "CREATE VECTOR INDEX `chunk_embedding` FOR (n:`Chunk`) ON (n.`embedding`)\nOPTIONS {indexConfig: {`vector.dimensions`: 1536, `vector.similarity_function`: 'cosine'}}")]
    [InlineData("RELATIONSHIP", "CREATE VECTOR INDEX `chunk_embedding` FOR ()-[r:`Chunk`]-() ON (r.`embedding`)\nOPTIONS {indexConfig: {`vector.dimensions`: 1536, `vector.similarity_function`: 'cosine'}}")]
    public void Vector_index_steps_render_exact_parameter_free_DDL(string entityType, string expected)
    {
        var step = GraphMigrationStep.CreateIndex("vector", Vector(entityType: entityType));
        var command = GraphMigrationStepRenderer.Render(step);
        Assert.Equal(expected, command.Cypher);
        Assert.Empty(command.Parameters);
        Assert.Equal(GraphMigrationCapability.VectorIndexes, Assert.Single(step.RequiredCapabilities));
        Assert.Equal("The exact named VECTOR index exists and is ONLINE; readiness is checked separately with a finite timeout.", step.Postcondition);
        Assert.Contains("'euclidean'", GraphMigrationStepRenderer.Render(
            GraphMigrationStep.CreateIndex("vector", Vector(similarity: GraphVectorSimilarity.Euclidean))).Cypher);
        Assert.Equal("DROP INDEX `chunk_embedding`", GraphMigrationStepRenderer.Render(
            GraphMigrationStep.DropIndex("drop", Vector(entityType: entityType), "Replaced by a new embedding set.")).Cypher);
    }

    [Theory]
    [InlineData("NODE", "english", true, "CREATE FULLTEXT INDEX `chunk_text` FOR (n:`Chunk`|`Section`) ON EACH [n.`text`, n.`title`]\nOPTIONS {indexConfig: {`fulltext.analyzer`: 'english', `fulltext.eventually_consistent`: true}}")]
    [InlineData("RELATIONSHIP", "standard-no-stop-words", false, "CREATE FULLTEXT INDEX `chunk_text` FOR ()-[r:`Chunk`|`Section`]-() ON EACH [r.`text`, r.`title`]\nOPTIONS {indexConfig: {`fulltext.analyzer`: 'standard-no-stop-words', `fulltext.eventually_consistent`: false}}")]
    public void Full_text_index_steps_render_every_label_and_their_options(string entityType, string analyzer, bool eventual, string expected)
    {
        var step = GraphMigrationStep.CreateIndex("fulltext", FullText(entityType: entityType, analyzer: analyzer, eventual: eventual));
        var command = GraphMigrationStepRenderer.Render(step);
        Assert.Equal(expected, command.Cypher);
        Assert.Empty(command.Parameters);
        Assert.Equal(GraphMigrationCapability.FullTextIndexes, Assert.Single(step.RequiredCapabilities));
        Assert.Equal(["Chunk", "Section"], step.Impact.LabelsOrTypes);
        Assert.Equal("Chunk", step.Impact.LabelOrType);
        Assert.Equal(["text", "title"], step.Impact.Properties);
        Assert.Equal("standard-no-stop-words", GraphMigrationSchemaDefinition.FullTextIndex("chunk_text", "NODE", ["Chunk"], ["text"]).FullTextAnalyzer);
    }

    [Fact]
    public void Definitions_expose_their_kind_and_options()
    {
        var vector = Vector();
        Assert.True(vector.IsIndex);
        Assert.Equal("VECTOR", vector.DefinitionType);
        Assert.Equal(1536, vector.VectorDimensions);
        Assert.Equal(GraphVectorSimilarity.Cosine, vector.VectorSimilarity);
        Assert.Null(vector.FullTextAnalyzer);
        Assert.Null(vector.FullTextEventuallyConsistent);
        Assert.Equal(["Chunk"], vector.LabelsOrTypes);
        var fullText = FullText();
        Assert.True(fullText.IsIndex);
        Assert.Equal("FULLTEXT", fullText.DefinitionType);
        Assert.Equal(["Chunk", "Section"], fullText.LabelsOrTypes);
        Assert.Equal("Chunk", fullText.LabelOrType);
        Assert.Equal(["text", "title"], fullText.Properties);
        Assert.Equal("english", fullText.FullTextAnalyzer);
        Assert.False(fullText.FullTextEventuallyConsistent);
        Assert.Null(fullText.VectorDimensions);
        Assert.Equal(["Records"], Definition("plain").LabelsOrTypes);
        Assert.Throws<ArgumentException>(() => GraphMigrationStep.CreateConstraint("constraint", vector));
        Assert.Throws<ArgumentException>(() => GraphMigrationStep.DropConstraint("constraint", fullText, "Reviewed."));
    }

    [Fact]
    public void Checksums_cover_every_search_index_option()
    {
        var baseline = Plan(GraphMigrationStep.CreateIndex("index", Vector())).Checksum;
        GraphMigrationSchemaDefinition[] vectors =
        [
            Vector(dimensions: 768), Vector(similarity: GraphVectorSimilarity.Euclidean), Vector(property: "vector"),
            Vector(label: "Section"), Vector(entityType: "RELATIONSHIP"), Vector(name: "other_embedding")
        ];
        Assert.All(vectors, changed => Assert.NotEqual(baseline, Plan(GraphMigrationStep.CreateIndex("index", changed)).Checksum));
        var text = Plan(GraphMigrationStep.CreateIndex("index", FullText())).Checksum;
        GraphMigrationSchemaDefinition[] fullTexts =
        [
            FullText(labels: ["Chunk"]), FullText(labels: ["Section", "Chunk"]), FullText(properties: ["title", "text"]),
            FullText(analyzer: "standard-no-stop-words"), FullText(analyzer: "standard"), FullText(eventual: true)
        ];
        Assert.All(fullTexts, changed => Assert.NotEqual(text, Plan(GraphMigrationStep.CreateIndex("index", changed)).Checksum));
        Assert.Contains("\"vectorDimensions\":1536", Plan(GraphMigrationStep.CreateIndex("index", Vector())).CanonicalJson);
    }

    [Fact]
    public void Invalid_search_index_definitions_fail_during_construction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Vector(dimensions: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Vector(dimensions: 4097));
        Assert.Throws<ArgumentOutOfRangeException>(() => Vector(similarity: (GraphVectorSimilarity)9));
        Assert.ThrowsAny<ArgumentException>(() => Vector(property: "embedding` MATCH (n)"));
        Assert.ThrowsAny<ArgumentException>(() => Vector(label: "__GraphBridgeMigration"));
        Assert.ThrowsAny<ArgumentException>(() => Vector(name: "gb_migration_vector"));
        Assert.Throws<ArgumentException>(() => Vector(entityType: "node"));
        Assert.Throws<ArgumentException>(() => FullText(labels: []));
        Assert.Throws<ArgumentException>(() => FullText(labels: ["Chunk", "Chunk"]));
        Assert.Throws<ArgumentException>(() => FullText(labels: Enumerable.Range(0, 17).Select(i => "L" + i).ToArray()));
        Assert.Throws<ArgumentException>(() => FullText(properties: []));
        Assert.Throws<ArgumentException>(() => FullText(properties: ["text", "text"]));
        Assert.Throws<ArgumentException>(() => FullText(properties: Enumerable.Range(0, 17).Select(i => "p" + i).ToArray()));
        Assert.Throws<ArgumentException>(() => FullText(analyzer: ""));
        Assert.Throws<ArgumentException>(() => FullText(analyzer: null!));
        Assert.Throws<ArgumentException>(() => FullText(analyzer: "english', `fulltext.eventually_consistent`: true"));
        Assert.Throws<ArgumentException>(() => FullText(analyzer: new string('a', 65)));
        Assert.Equal("standard-no-stop-words", FullText(analyzer: "standard-no-stop-words").FullTextAnalyzer);
        Assert.Equal(16, FullText(labels: Enumerable.Range(0, 16).Select(i => "L" + i).ToArray()).LabelsOrTypes.Count);
        Assert.Throws<ArgumentException>(() => Definition("vector_index", GraphMigrationSchemaKind.VectorIndex));
        Assert.Throws<ArgumentException>(() => Definition("text_index", GraphMigrationSchemaKind.FullTextIndex));
    }

    [Theory]
    [InlineData("ONLINE", false, false)]
    [InlineData("POPULATING", true, false)]
    [InlineData("FAILED", false, true)]
    public void A_matching_named_index_follows_the_readiness_states(string state, bool waiting, bool failed)
    {
        var step = GraphMigrationStep.CreateIndex("vector", Vector());
        var problem = GraphMigrationExecutor.SchemaPostcondition(step, Observe(ObservedVector(state: state)), out var pending);
        Assert.Equal(waiting, pending);
        Assert.Equal(failed, problem is not null);
        var text = GraphMigrationStep.CreateIndex("fulltext", FullText());
        Assert.Equal(failed, GraphMigrationExecutor.SchemaPostcondition(text, Observe(ObservedFullText(state: state)), out _) is not null);
    }

    [Fact]
    public void A_vector_index_matches_only_its_type_schema_dimensions_and_similarity()
    {
        var create = GraphMigrationStep.CreateIndex("vector", Vector());
        Assert.Null(GraphMigrationExecutor.SchemaPostcondition(create, Observe(ObservedVector()), out _));
        Assert.Null(GraphMigrationExecutor.SchemaPostcondition(create, Observe(ObservedVector(similarity: "cosine")), out _));
        GraphObservedIndex[] conflicts =
        [
            ObservedVector(dimensions: 768), ObservedVector(similarity: "EUCLIDEAN"), ObservedVector(type: "RANGE"),
            ObservedVector(entityType: "RELATIONSHIP"), ObservedVector(labels: ["Section"]), ObservedVector(properties: ["vector"]),
            ObservedVector(config: false), ObservedVector(owner: "some_constraint")
        ];
        foreach (var conflict in conflicts)
        {
            Assert.Contains("differs", GraphMigrationExecutor.SchemaPostcondition(create, Observe(conflict), out _));
            Assert.NotNull(GraphMigrationExecutor.SchemaPrecondition(create, Observe(conflict), recovery: true, out var adopted));
            Assert.False(adopted);
        }
        var drop = GraphMigrationStep.DropIndex("drop", Vector(), "Replaced by a new embedding set.");
        Assert.Null(GraphMigrationExecutor.SchemaPrecondition(drop, Observe(ObservedVector()), recovery: false, out _));
        Assert.NotNull(GraphMigrationExecutor.SchemaPrecondition(drop, Observe(ObservedVector(dimensions: 768)), recovery: false, out _));
    }

    [Fact]
    public void A_full_text_index_matches_label_and_property_sets_its_analyzer_and_consistency()
    {
        var create = GraphMigrationStep.CreateIndex("fulltext", FullText());
        Assert.Null(GraphMigrationExecutor.SchemaPostcondition(create, Observe(ObservedFullText()), out _));
        Assert.Null(GraphMigrationExecutor.SchemaPostcondition(create, Observe(ObservedFullText(labels: ["Section", "Chunk"])), out _));
        Assert.Null(GraphMigrationExecutor.SchemaPostcondition(create, Observe(ObservedFullText(properties: ["title", "text"])), out _));
        Assert.Null(GraphMigrationExecutor.SchemaPostcondition(create, Observe(ObservedFullText(analyzer: "English")), out _));
        GraphObservedIndex[] conflicts =
        [
            ObservedFullText(properties: ["text"]), ObservedFullText(labels: ["Chunk"]), ObservedFullText(analyzer: "standard"),
            ObservedFullText(eventual: true), ObservedFullText(entityType: "RELATIONSHIP"), ObservedFullText(type: "VECTOR"),
            ObservedFullText(config: false)
        ];
        foreach (var conflict in conflicts)
            Assert.NotNull(GraphMigrationExecutor.SchemaPostcondition(create, Observe(conflict), out _));
        // The default analyzer is declared like any other, so a server with another default can't pass for it.
        var standard = GraphMigrationStep.CreateIndex("fulltext", FullText(analyzer: "standard-no-stop-words"));
        Assert.Null(GraphMigrationExecutor.SchemaPostcondition(standard, Observe(ObservedFullText(analyzer: "standard-no-stop-words")), out _));
        Assert.NotNull(GraphMigrationExecutor.SchemaPostcondition(standard, Observe(ObservedFullText()), out _));
    }

    [Fact]
    public void An_equivalent_index_under_another_name_refuses_creation_whatever_its_options()
    {
        var vector = GraphMigrationStep.CreateIndex("vector", Vector());
        var problem = GraphMigrationExecutor.SchemaPrecondition(vector,
            Observe(ObservedVector(name: "legacy_embedding", dimensions: 768, similarity: "EUCLIDEAN")), recovery: false, out _);
        Assert.Equal("An equivalent VECTOR index already exists under another name: 'legacy_embedding'.", problem);
        var text = GraphMigrationStep.CreateIndex("fulltext", FullText());
        Assert.Contains("'legacy_text'", GraphMigrationExecutor.SchemaPrecondition(text,
            Observe(ObservedFullText(name: "legacy_text", labels: ["Section", "Chunk"], properties: ["title", "text"], analyzer: "standard")),
            recovery: false, out _));
        // Other schemas, other types and a RANGE index on the same property don't block creation.
        Assert.Null(GraphMigrationExecutor.SchemaPrecondition(vector, Observe(ObservedVector(name: "section_embedding", labels: ["Section"])), recovery: false, out _));
        Assert.Null(GraphMigrationExecutor.SchemaPrecondition(vector,
            Observe(new GraphObservedIndex("chunk_range", "ONLINE", "RANGE", "NODE", ["Chunk"], ["embedding"])), recovery: false, out _));
        Assert.Null(GraphMigrationExecutor.SchemaPrecondition(text, Observe(ObservedFullText(name: "chunk_only", labels: ["Chunk"])), recovery: false, out _));
        Assert.Null(GraphMigrationExecutor.SchemaPrecondition(vector, Observe(), recovery: false, out _));
    }

    [Theory]
    [InlineData("2026.09.0", "community", GraphSchemaCapabilitySupport.Supported)]
    [InlineData("2025.01.0", "enterprise", GraphSchemaCapabilitySupport.Supported)]
    [InlineData("5.26.0", "community", GraphSchemaCapabilitySupport.Unknown)]
    [InlineData("2027.01.0", "community", GraphSchemaCapabilitySupport.Unknown)]
    [InlineData("2026.09.0", "aura", GraphSchemaCapabilitySupport.Unknown)]
    public void Search_index_steps_run_only_on_verified_releases(string version, string edition, GraphSchemaCapabilitySupport expected)
    {
        var capabilities = GraphSchemaCapabilities.Detect(version, edition, ["5", "25"]);
        Assert.Equal(expected, capabilities.VectorIndexes);
        Assert.Equal(expected, capabilities.FullTextIndexes);
        var plan = Plan(GraphMigrationStep.CreateIndex("vector", Vector()), GraphMigrationStep.CreateIndex("fulltext", FullText()));
        var problems = GraphMigrationExecutor.CapabilityProblems(plan, capabilities).ToArray();
        if (expected == GraphSchemaCapabilitySupport.Supported) Assert.Empty(problems);
        else
        {
            Assert.Equal(2, problems.Length);
            Assert.Contains(problems, p => p.Contains("'VectorIndexes' is Unknown", StringComparison.Ordinal));
            Assert.Contains(problems, p => p.Contains("'FullTextIndexes' is Unknown", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_index_observation_copies_options_and_tolerates_their_absence()
    {
        var config = new Dictionary<string, object> { ["vector.dimensions"] = 1536L, ["vector.similarity_function"] = "COSINE", ["vector.hnsw.m"] = 16L };
        var row = Row(new Dictionary<string, object> { ["indexProvider"] = "vector-3.0", ["indexConfig"] = config });
        var observed = GraphSchemaObservationParser.ParseIndex(row);
        Assert.Equal(1536L, observed.IndexConfig!["vector.dimensions"]);
        Assert.Equal("COSINE", observed.IndexConfig["vector.similarity_function"]);
        config["vector.dimensions"] = 3L;
        Assert.Equal(1536L, observed.IndexConfig["vector.dimensions"]);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, object?>)observed.IndexConfig).Clear());
        Assert.Null(GraphSchemaObservationParser.ParseIndex(Row(null)).IndexConfig);
        var withoutOptions = Row(null);
        withoutOptions.Remove("options");
        Assert.Null(GraphSchemaObservationParser.ParseIndex(withoutOptions).IndexConfig);
        Assert.Null(GraphSchemaObservationParser.ParseIndex(Row(new Dictionary<string, object> { ["indexProvider"] = "range-1.0", ["indexConfig"] = null! })).IndexConfig);
        Assert.Throws<InvalidOperationException>(() => GraphSchemaObservationParser.ParseIndex(Row("not a map")));
        Assert.Throws<InvalidOperationException>(() => GraphSchemaObservationParser.ParseIndex(Row(new Dictionary<string, object> { ["indexConfig"] = "not a map" })));
        Assert.Null(new GraphObservedIndex("plain", "ONLINE", "RANGE", "NODE", ["Chunk"], ["id"]).IndexConfig);
        Assert.Contains("options", GraphSchemaInspector.IndexesQuery);
    }

    private static Dictionary<string, object?> Row(object? options) => new()
    {
        ["name"] = "chunk_embedding", ["state"] = "ONLINE", ["type"] = "VECTOR", ["entityType"] = "NODE",
        ["labelsOrTypes"] = new List<object> { "Chunk" }, ["properties"] = new List<object> { "embedding" },
        ["owningConstraint"] = null, ["options"] = options
    };

    private static GraphObservedIndex ObservedVector(string name = "chunk_embedding", string state = "ONLINE", string type = "VECTOR",
        string entityType = "NODE", string[]? labels = null, string[]? properties = null, long dimensions = 1536,
        string similarity = "COSINE", bool config = true, string? owner = null)
        => new(name, state, type, entityType, labels ?? ["Chunk"], properties ?? ["embedding"], owner,
            config ? new Dictionary<string, object?>
            {
                ["vector.dimensions"] = dimensions, ["vector.similarity_function"] = similarity,
                ["vector.quantization.enabled"] = true, ["vector.hnsw.m"] = 16L
            } : null);

    private static GraphObservedIndex ObservedFullText(string name = "chunk_text", string state = "ONLINE", string type = "FULLTEXT",
        string entityType = "NODE", string[]? labels = null, string[]? properties = null, string analyzer = "english",
        bool eventual = false, bool config = true)
        => new(name, state, type, entityType, labels ?? ["Chunk", "Section"], properties ?? ["text", "title"], null,
            config ? new Dictionary<string, object?> { ["fulltext.analyzer"] = analyzer, ["fulltext.eventually_consistent"] = eventual } : null);

    private static GraphSchemaObservation Observe(params GraphObservedIndex[] indexes) => new([], indexes);

    private static GraphMigrationSchemaDefinition Vector(string name = "chunk_embedding", string entityType = "NODE", string label = "Chunk",
        string property = "embedding", int dimensions = 1536, GraphVectorSimilarity similarity = GraphVectorSimilarity.Cosine)
        => GraphMigrationSchemaDefinition.VectorIndex(name, entityType, label, property, dimensions, similarity);

    private static GraphMigrationSchemaDefinition FullText(string name = "chunk_text", string entityType = "NODE", string[]? labels = null,
        string[]? properties = null, string analyzer = "english", bool eventual = false)
        => GraphMigrationSchemaDefinition.FullTextIndex(name, entityType, labels ?? ["Chunk", "Section"], properties ?? ["text", "title"], analyzer, eventual);

    private static GraphMigrationPlan Plan(params GraphMigrationStep[] steps)
        => new("migrate-2", "migrate-1", steps, GraphMigrationRollbackPolicy.NoRollback("Restore the reviewed backup if compensation is required."));

    private static GraphMigrationDataScope Scope(Dictionary<string, object?>? filters = null) => new("NODE", "Records", filters ?? [], 10);

    private static GraphMigrationSchemaDefinition Definition(string name, GraphMigrationSchemaKind kind = GraphMigrationSchemaKind.UniquenessConstraint,
        string entityType = "NODE", string[]? properties = null, string? propertyType = null)
        => new(name, kind, entityType, "Records", properties ?? ["id"], propertyType);
}
