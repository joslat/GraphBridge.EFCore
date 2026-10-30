using GraphBridge.GraphRag;
using static GraphBridge.GraphRag.GraphRagResolution;

namespace GraphBridge.GraphRag.Tests;

/// <summary>GR-55 (ADR 0052, section 16) offline: which things are suggested as one, and which is kept.</summary>
public sealed class GraphRagResolutionTests
{
    [Theory]
    [InlineData("the Caterpillar", "caterpillar")]
    [InlineData("  The   Cheshire   Cat ", "cheshire cat")]
    [InlineData("a Hatter", "hatter")]
    [InlineData("An Owl!", "owl")]
    [InlineData("The", "the")]
    [InlineData("Theodore", "theodore")]
    public void A_name_is_read_without_case_extra_spaces_a_leading_article_or_trailing_punctuation(string name, string key)
        => Assert.Equal(key, Key(name));

    [Fact]
    public void Names_equal_but_for_an_article_or_case_are_suggested_and_the_most_mentioned_is_kept()
    {
        var suggestions = Suggest(
        [
            new("1", "Character", "Caterpillar", null, false, 9),
            new("2", "Character", "the Caterpillar", null, false, 3),
            new("3", "Character", "Cheshire cat", null, false, 1),
            new("4", "Character", "the Cheshire Cat", null, false, 6),
            new("5", "Character", "Hatter", null, false, 4),
            new("6", "Character", "the Hatter", null, false, 4),
            new("7", "Character", "a Hatter", null, false, 1)
        ]);
        Assert.Equal(
        [
            new GraphRagMergeSuggestion("Character", "1", "Caterpillar", "2", "the Caterpillar", "same-name"),
            new GraphRagMergeSuggestion("Character", "5", "Hatter", "7", "a Hatter", "same-name"),
            new GraphRagMergeSuggestion("Character", "5", "Hatter", "6", "the Hatter", "same-name"),
            new GraphRagMergeSuggestion("Character", "4", "the Cheshire Cat", "3", "Cheshire cat", "same-name")
        ], suggestions.OrderBy(s => s.KeepName, StringComparer.Ordinal).ThenBy(s => s.MergeName, StringComparer.Ordinal));
    }

    [Fact]
    public void An_alias_joins_two_things_and_containment_placeholders_and_other_kinds_never_do()
    {
        var suggestions = Suggest(
        [
            new("1", "Character", "White Rabbit", "the Rabbit\nW. Rabbit", false, 12),
            new("2", "Character", "W. Rabbit", null, false, 1),
            new("3", "Character", "Dormouse", null, false, 5),
            new("4", "Character", "Mouse", null, false, 4),
            new("5", "Character", "Alice", null, false, 40),
            new("6", "Character", "Alice's sister", null, false, 2),
            new("7", "Character", "the man", null, true, 1),
            new("8", "Character", "The Man", null, true, 1),
            new("9", "Place", "Caterpillar", null, false, 1),
            new("10", "Character", "Caterpillar", null, false, 1)
        ]);
        Assert.Equal([new GraphRagMergeSuggestion("Character", "1", "White Rabbit", "2", "W. Rabbit", "alias")], suggestions);
    }

    [Fact]
    public void A_plan_blocks_a_thing_merged_twice_or_kept_and_merged()
    {
        var plan = Plan(
        [
            new("Character", "1", "A", "2", "B", "same-name"),
            new("Character", "3", "C", "2", "B", "same-name"),
            new("Character", "2", "B", "4", "D", "alias")
        ]);
        Assert.Equal(3, plan.Merges.Count);
    }
}
