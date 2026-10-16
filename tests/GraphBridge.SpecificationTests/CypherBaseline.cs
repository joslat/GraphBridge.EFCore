using Microsoft.EntityFrameworkCore.TestUtilities;

namespace GraphBridge.SpecificationTests;

internal static class CypherBaseline
{
    // Fixed expected projection of the unmodified upstream BasicTypesEntity at its EF scalar indices.
    private const string Projection = "n.`Id` AS `c0`, n.`Bool` AS `c1`, n.`Byte` AS `c2`, n.`ByteArray` AS `c3`, "
        + "n.`DateOnly` AS `c4`, n.`DateTime` AS `c5`, n.`DateTimeOffset` AS `c6`, n.`Decimal` AS `c7`, "
        + "n.`Double` AS `c8`, n.`Enum` AS `c9`, n.`FlagsEnum` AS `c10`, n.`Float` AS `c11`, "
        + "n.`Guid` AS `c12`, n.`Int` AS `c13`, n.`Long` AS `c14`, n.`Short` AS `c15`, "
        + "n.`String` AS `c16`, n.`TimeOnly` AS `c17`, n.`TimeSpan` AS `c18`";

    public static void AssertCommand(ListLoggerFactory logger, string predicate, bool nullable = false)
    {
        var commands = logger.Log.SelectMany(entry =>
                entry.State as IEnumerable<KeyValuePair<string, object?>> ?? [])
            .Where(pair => pair.Key == "Cypher")
            .Select(pair => Assert.IsType<string>(pair.Value))
            .ToArray();
        var actual = Assert.Single(commands);
        Assert.Equal("MATCH (n:`" + (nullable ? "GraphBridgeSpecNullableBasicTypes" : "GraphBridgeSpecBasicTypes")
            + "`)\nWHERE " + predicate + "\nRETURN " + Projection, actual);
        Assert.DoesNotContain("Seattle", actual, StringComparison.Ordinal);
        Assert.DoesNotContain("999", actual, StringComparison.Ordinal);
    }
}
