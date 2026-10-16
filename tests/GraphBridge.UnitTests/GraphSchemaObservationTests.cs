using GraphBridge.EntityFrameworkCore.Schema;

namespace GraphBridge.UnitTests;

public sealed class GraphSchemaObservationTests
{
    [Fact]
    public void Driver_shaped_lists_are_strictly_decoded_and_copied_without_sanitizing_observed_names()
    {
        var labels = new List<object> { "External label" };
        var properties = new List<object> { "some-property" };
        var row = ConstraintRow();
        row["labelsOrTypes"] = labels;
        row["properties"] = properties;
        row["propertyType"] = "STRING";
        var result = GraphSchemaObservationParser.ParseConstraint(row);
        labels.Clear();
        properties.Clear();
        Assert.Equal("external-constraint", result.Name);
        Assert.Equal(new[] { "External label" }, result.LabelsOrTypes);
        Assert.Equal(new[] { "some-property" }, result.Properties);
        Assert.Equal("STRING", result.PropertyType);
    }

    [Fact]
    public void Lookup_indexes_and_nonproperty_constraints_preserve_null_schema_columns()
    {
        var result = GraphSchemaObservationParser.ParseIndex(new Dictionary<string, object?>
        {
            ["name"] = "lookup", ["state"] = "ONLINE", ["type"] = "LOOKUP", ["entityType"] = "NODE",
            ["labelsOrTypes"] = null, ["properties"] = null, ["owningConstraint"] = null
        });
        Assert.Null(result.LabelsOrTypes);
        Assert.Null(result.Properties);
        var row = ConstraintRow();
        row["type"] = "NODE_LABEL_EXISTENCE";
        row["properties"] = null;
        Assert.Null(GraphSchemaObservationParser.ParseConstraint(row).Properties);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("type")]
    [InlineData("entityType")]
    [InlineData("labelsOrTypes")]
    [InlineData("properties")]
    public void Missing_required_observation_columns_do_not_become_partial_success(string column)
    {
        var row = ConstraintRow();
        row.Remove(column);
        var error = Assert.Throws<InvalidOperationException>(() => GraphSchemaObservationParser.ParseConstraint(row));
        Assert.Contains(column, error.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData("scalar-is-not-a-list")]
    public void Wrong_list_shape_is_rejected(object value)
    {
        var row = ConstraintRow();
        row["properties"] = value;
        Assert.Throws<InvalidOperationException>(() => GraphSchemaObservationParser.ParseConstraint(row));
        row["properties"] = new object?[] { "id", null };
        Assert.Throws<InvalidOperationException>(() => GraphSchemaObservationParser.ParseConstraint(row));
    }

    [Fact]
    public void Optional_type_column_may_be_absent_on_older_servers_but_wrong_type_is_rejected()
    {
        var row = ConstraintRow();
        Assert.Null(GraphSchemaObservationParser.ParseConstraint(row).PropertyType);
        row["propertyType"] = 12L;
        Assert.Throws<InvalidOperationException>(() => GraphSchemaObservationParser.ParseConstraint(row));
    }

    [Fact]
    public void Snapshot_copies_and_sorts_rows_but_rejects_duplicate_names()
    {
        var rows = new List<GraphObservedConstraint>
        {
            new("z", "UNIQUENESS", "NODE", ["A"], ["id"]),
            new("a", "NODE_KEY", "NODE", ["B"], ["id"])
        };
        var observation = new GraphSchemaObservation(rows, [], "GRAPH TYPE specification");
        rows.Clear();
        Assert.Equal(new[] { "a", "z" }, observation.Constraints.Select(value => value.Name));
        Assert.Equal("GRAPH TYPE specification", observation.GraphTypeSpecification);
        Assert.Throws<ArgumentException>(() => new GraphSchemaObservation(
            [observation.Constraints[0], observation.Constraints[0]], []));
    }

    private static Dictionary<string, object?> ConstraintRow() => new()
    {
        ["name"] = "external-constraint", ["type"] = "NODE_PROPERTY_UNIQUENESS", ["entityType"] = "NODE",
        ["labelsOrTypes"] = new List<object> { "Thing" }, ["properties"] = new List<object> { "id" }
    };
}
