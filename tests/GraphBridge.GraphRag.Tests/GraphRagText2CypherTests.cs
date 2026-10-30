using GraphBridge.GraphRag;
using GraphBridge.GraphRag.Ontologies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

namespace GraphBridge.GraphRag.Tests;

/// <summary>GR-15 (ADR 0052, section 15) offline: the model's reply, the text gate, the schema and the row values.</summary>
public sealed class GraphRagText2CypherTests
{
    [Fact]
    public void A_reply_is_a_query_a_refusal_or_unreadable()
    {
        Assert.Equal(("MATCH (n) RETURN count(n) AS n", (string?)null),
            GraphRagText2Cypher<DbContext>.Parse("```json\n{\"cypher\": \"MATCH (n) RETURN count(n) AS n;\"}\n```"));
        Assert.Equal(((string?)null, "model-declined"), GraphRagText2Cypher<DbContext>.Parse("""{"insufficient": "no capitals in this graph"}"""));
        Assert.Equal(((string?)null, "unreadable-response"), GraphRagText2Cypher<DbContext>.Parse("MATCH (n) RETURN n"));
        Assert.Equal(((string?)null, "unreadable-response"), GraphRagText2Cypher<DbContext>.Parse("""{"cypher": ""}"""));
        Assert.Equal(((string?)null, "unreadable-response"), GraphRagText2Cypher<DbContext>.Parse("{\"cypher\": "));
    }

    [Theory]
    [InlineData("MATCH (p:Person) RETURN p.Name AS name", true)]
    [InlineData("MATCH (p:Person) CALL { WITH p MATCH (p)--(o) RETURN count(o) AS n } RETURN p.Name, n", true)]
    [InlineData("MATCH (p:Person) CALL (p) { MATCH (p)--(o) RETURN count(o) AS n } RETURN p.Name, n", true)]
    [InlineData("CALL db.labels() YIELD label RETURN label", false)]
    [InlineData("LOAD CSV FROM 'https://example.org/x.csv' AS row RETURN row", false)]
    [InlineData("USE other MATCH (n) RETURN n", false)]
    [InlineData("MATCH (n) RETURN n; MATCH (m) DETACH DELETE m", false)]
    [InlineData("MATCH (n) CALL { WITH n SET n.x = 1 } IN TRANSACTIONS RETURN n", false)]
    // A comment is a space to Cypher, so it hides no keyword (GR-69).
    [InlineData("CALL/**/db.labels() YIELD label RETURN label", false)]
    [InlineData("LOAD/**/CSV FROM 'https://example.org/x.csv' AS row RETURN row", false)]
    [InlineData("MATCH (n) WITH n CALL//\ndb.labels() YIELD label RETURN label", false)]
    [InlineData("MATCH (n) RETURN n /* never closed", false)]
    // Only Neo4j's own function namespaces: a plugin's could run text as Cypher.
    [InlineData("RETURN apoc.cypher.runFirstColumnSingle('CA' + 'LL db.labels()', {}) AS r", false)]
    [InlineData("RETURN `apoc`.`cypher`.runFirstColumnSingle('RETURN 1', {}) AS r", false)]
    [InlineData("RETURN `apoc.cypher.runFirstColumnSingle`('RETURN 1', {}) AS r", false)]
    [InlineData("MATCH (a:Place), (b:Place) RETURN point.distance(a.Location, b.Location) AS d, date.truncate('month', date()) AS m", true)]
    [InlineData("MATCH (c:Chunk) RETURN vector.similarity.cosine(c.Embedding, c.Embedding) AS s, coll.sort([2, 1]) AS l", true)]
    // A reading clause first: no administration, no plan options of the model's own.
    [InlineData("SHOW TRANSACTIONS", false)]
    [InlineData("TERMINATE TRANSACTIONS 'neo4j-transaction-1'", false)]
    [InlineData("EXPLAIN MATCH (n) RETURN n", false)]
    [InlineData("CYPHER 25 MATCH (n) RETURN count(n) AS n", true)]
    // String literals and quoted names are not code: these words in them refuse nothing.
    [InlineData("MATCH (c:Chunk) WHERE c.Text CONTAINS 'the use of solar sails; call sign' RETURN c.Id", true)]
    [InlineData("MATCH (n:`Use Case`) RETURN n.`call` AS c", true)]
    [InlineData("MATCH (n) WHERE n.Name = 'never closed RETURN n", false)]
    public void The_text_gate_refuses_procedures_files_other_databases_and_several_statements(string cypher, bool allowed)
        => Assert.Equal(allowed, GraphRagText2Cypher<DbContext>.Allowed(cypher, 4000));

    [Fact]
    public void A_plan_that_reaches_a_procedure_a_file_or_a_batched_transaction_is_refused()
    {
        static IPlan Plan(string operatorType, params IPlan[] children) => new FakePlan(operatorType, children);
        Assert.True(GraphRagText2Cypher<DbContext>.PlanAllowed(Plan("ProduceResults@neo4j", Plan("Projection@neo4j", Plan("NodeByLabelScan@neo4j")))));
        Assert.False(GraphRagText2Cypher<DbContext>.PlanAllowed(Plan("ProduceResults@neo4j", Plan("ProcedureCall@neo4j"))));
        Assert.False(GraphRagText2Cypher<DbContext>.PlanAllowed(Plan("ProduceResults@neo4j", Plan("Projection@neo4j", Plan("LoadCSV@neo4j")))));
        Assert.False(GraphRagText2Cypher<DbContext>.PlanAllowed(Plan("ProduceResults@neo4j", Plan("TransactionApply@neo4j"))));
    }

    private sealed record FakePlan(string OperatorType, IList<IPlan> Children) : IPlan
    {
        public IDictionary<string, object> Arguments { get; } = new Dictionary<string, object>();
        public IList<string> Identifiers { get; } = [];
    }

    [Fact]
    public void A_query_over_the_length_bound_is_refused_and_the_options_are_validated()
    {
        Assert.False(GraphRagText2Cypher<DbContext>.Allowed("MATCH (n) RETURN n" + new string(' ', 200), 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddGraphRagText2Cypher<DbContext>(o => o.MaxRows = 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddGraphRagText2Cypher<DbContext>(o => o.Timeout = TimeSpan.FromHours(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddGraphRagText2Cypher<DbContext>(o => o.MaxQueryLength = 10));
    }

    [Fact]
    public void The_schema_lists_the_layout_and_the_ontology_the_same_way_every_time()
    {
        var layout = GraphRagLayout.Default with { DocumentLabel = "Article", ChunkLabel = "ArticlePassage" };
        var schema = GraphRagText2Cypher<DbContext>.Schema(layout, GraphRagOntologies.Article);
        Assert.Equal(schema, GraphRagText2Cypher<DbContext>.Schema(layout, GraphRagOntologies.Article));
        Assert.Contains("- Article: a source document. Id, Title, Published, Publisher, Url, SourceUri", schema, StringComparison.Ordinal);
        Assert.Contains("- (ArticlePassage)-[:IN_DOCUMENT]->(Article)", schema, StringComparison.Ordinal);
        Assert.Contains("- (Person)-[:WORKS_FOR {DocumentIds, Role}]->(Organization)", schema, StringComparison.Ordinal);
        Assert.Contains("- (Person)-[:AUTHORED {DocumentIds}]->(Article)", schema, StringComparison.Ordinal);
        Assert.Contains("- (Person)-[:QUOTED {Words}]->(ArticlePassage)", schema, StringComparison.Ordinal);
        Assert.Contains("- (Place)-[:MENTIONED_IN_PLACE {Quote}]->(ArticlePassage)", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("Embedding", schema, StringComparison.Ordinal);
        var lexical = GraphRagText2Cypher<DbContext>.Schema(GraphRagLayout.Default, null);
        Assert.Contains("- (Chunk)-[:IN_DOCUMENT]->(Document)", lexical, StringComparison.Ordinal);
    }

    [Fact]
    public void Row_values_are_plain_and_never_carry_an_embedding()
    {
        var properties = new Dictionary<string, object>
        {
            ["Name"] = "Reid Wiseman", ["Embedding"] = new List<object> { 0.5, 0.5 },
            ["Staged"] = Enumerable.Range(0, 64).Select(i => (object)(double)i).ToList(), ["Tags"] = new List<object> { "crew", "NASA" }
        };
        var value = Assert.IsType<Dictionary<string, object?>>(GraphRagText2Cypher<DbContext>.Value((IReadOnlyDictionary<string, object>)properties, "Embedding"));
        Assert.Equal(["Name", "Tags"], value.Keys);
        Assert.Equal(new object?[] { "crew", "NASA" }, value["Tags"]);
        Assert.Equal(4L, GraphRagText2Cypher<DbContext>.Value(4L, "Embedding"));
    }

    [Fact]
    public void A_returned_embedding_is_left_out_and_long_lists_and_strings_are_cut_and_reported()
    {
        // RETURN c.Embedding: a list of numbers with fractions, left out anywhere; whole numbers, such as years, stay.
        Assert.Null(GraphRagText2Cypher<DbContext>.Value(Enumerable.Range(0, 64).Select(i => (object)(i / 10.0)).ToList(), "Embedding"));
        Assert.Equal(100, Assert.IsType<object?[]>(GraphRagText2Cypher<DbContext>.Value(Enumerable.Range(0, 100).Select(i => (object)(long)i).ToList(), "Embedding")).Length);

        var values = new GraphRagText2Cypher<DbContext>.RowValues("Embedding");
        var list = Assert.IsType<object?[]>(values.Value(Enumerable.Range(0, 5000).Select(i => (object)(long)i).ToList()));
        Assert.Equal(1000, list.Length);
        Assert.Equal(new string('x', 10_000), values.Value(new string('x', 20_000)));
        Assert.Equal([new GraphRagLimit("GraphQueryListItems", 1000), new GraphRagLimit("GraphQueryText", 10_000)], values.Limits);
        Assert.Empty(new GraphRagText2Cypher<DbContext>.RowValues("Embedding").Limits);
    }
}
