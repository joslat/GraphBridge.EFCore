namespace GraphBridge.GraphRag.Ontologies;

// ADR 0052, section 7: the classes of the standard ontologies (design sections 5.3 to 5.5). Things are nodes keyed by a
// computed ID; facts are association edges with the ontology's type names; mentions and quotes are identified
// relationships to the chunk they come from, with the words' exact UTF-8 bytes.

/// <summary>
/// An extracted thing. <see cref="Id"/> hashes the kind and the resolved name, so the same name of the same kind is one
/// thing across chunks and documents; a placeholder's ID hashes its chunk and span instead, so it never merges.
/// </summary>
public abstract class GraphRagEntity
{
    public string Id { get; set; } = "";
    /// <summary>The name as the text first gives it.</summary>
    public string Name { get; set; } = "";
    /// <summary>Other names the text uses, one per line, sorted ordinally; null when there are none or the class has no aliases.</summary>
    public string? Aliases { get; set; }
    public string? Description { get; set; }
    /// <summary>A name such as "the man" or "someone": kept apart, never merged with another thing.</summary>
    public bool IsPlaceholder { get; set; }
    /// <summary>The run that first wrote the thing.</summary>
    public string RunId { get; set; } = "";
    public string ExtractorVersion { get; set; } = "";
}

/// <summary>A named individual (generic document, article).</summary>
public sealed class Person : GraphRagEntity { }

/// <summary>A company, institution, government body, team or other organised group (generic document, article).</summary>
public sealed class Organization : GraphRagEntity { }

/// <summary>A named location (all three standard ontologies).</summary>
public sealed class Place : GraphRagEntity { }

/// <summary>A happening. The generic document records <see cref="Date"/>, the novel <see cref="Chapter"/>, both in the text's own words.</summary>
public sealed class Event : GraphRagEntity
{
    public string? Date { get; set; }
    public string? Chapter { get; set; }
}

/// <summary>A topic, product, technology or idea (generic document, article).</summary>
public sealed class Concept : GraphRagEntity { }

/// <summary>A person, animal or creature that acts or speaks in a story (novel).</summary>
public sealed class Character : GraphRagEntity { }

/// <summary>A family, household, crew, company or other group of characters (novel).</summary>
public sealed class Group : GraphRagEntity { }

/// <summary>What an article is about (article).</summary>
public sealed class Topic : GraphRagEntity { }

/// <summary>
/// A fact the text states: an association edge from <see cref="FromId"/> to <see cref="ToId"/>, one per pair. A fact is kept
/// only when both ends are grounded in the same chunk, so its evidence in each listed document is that document's chunks
/// that mention both ends.
/// </summary>
public abstract class GraphRagFact
{
    public string FromId { get; set; } = "";
    public string ToId { get; set; } = "";
    /// <summary>The documents the fact was extracted from, one ID per line, sorted ordinally.</summary>
    public string DocumentIds { get; set; } = "";
    /// <summary>The run that first wrote the fact.</summary>
    public string RunId { get; set; } = "";
    public string ExtractorVersion { get; set; } = "";
}

/// <summary><c>WORKS_FOR</c>: a person to an organisation.</summary>
public sealed class WorksFor : GraphRagFact
{
    public string? Role { get; set; }
}

/// <summary><c>MEMBER_OF</c>: a person or organisation to an organisation, or a character to a group.</summary>
public sealed class MemberOf<TFrom, TTo> : GraphRagFact where TFrom : GraphRagEntity where TTo : GraphRagEntity { }

/// <summary><c>LOCATED_IN</c>: a thing to a place.</summary>
public sealed class LocatedIn<TFrom> : GraphRagFact where TFrom : GraphRagEntity { }

/// <summary><c>PARTICIPATES_IN</c>: a person, organisation or character to an event.</summary>
public sealed class ParticipatesIn<TFrom> : GraphRagFact where TFrom : GraphRagEntity
{
    public string? Role { get; set; }
}

/// <summary><c>PART_OF</c>: an organisation, place or concept to another of the same kind.</summary>
public sealed class PartOf<T> : GraphRagFact where T : GraphRagEntity { }

/// <summary><c>RELATED_TO</c>: the catch-all. The novel records a <see cref="Kind"/> (friend, employer, employee, rival, other).</summary>
public sealed class RelatedTo<TFrom, TTo> : GraphRagFact where TFrom : GraphRagEntity where TTo : GraphRagEntity
{
    public string? Kind { get; set; }
    public string? Description { get; set; }
}

/// <summary><c>KIN_OF</c>: a character to a relative; <see cref="Kind"/> is parent, child, sibling, spouse or other.</summary>
public sealed class KinOf : GraphRagFact
{
    public string? Kind { get; set; }
}

/// <summary><c>TAKES_PLACE_IN</c>: an event to a place (novel).</summary>
public sealed class TakesPlaceIn : GraphRagFact { }

/// <summary><c>AUTHORED</c>: a person to the article's document.</summary>
public sealed class Authored : GraphRagFact { }

/// <summary><c>ABOUT</c>: the article's document to a topic.</summary>
public sealed class About : GraphRagFact { }

/// <summary>
/// A mention: the identified relationship <c>MENTIONED_IN_{LABEL}</c> from the thing to the chunk. <see cref="Quote"/> is exactly
/// the document's bytes [<see cref="StartByte"/>, <see cref="EndByte"/>). <see cref="Id"/> hashes the chunk, the start byte and the thing.
/// </summary>
public sealed class GraphRagMention<TEntity> : GraphRagMention where TEntity : GraphRagEntity { }

/// <summary>What every mention type carries; the mapped types are <see cref="GraphRagMention{TEntity}"/>, one per class.</summary>
public abstract class GraphRagMention
{
    public string Id { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string ChunkId { get; set; } = "";
    public int StartByte { get; set; }
    public int EndByte { get; set; }
    public string Quote { get; set; } = "";
    public string RunId { get; set; } = "";
    public string ExtractorVersion { get; set; } = "";
}

/// <summary>
/// Who said it: an identified relationship from the speaker to the chunk. <see cref="Words"/> is exactly the document's bytes
/// [<see cref="StartByte"/>, <see cref="EndByte"/>). <see cref="Id"/> hashes the chunk, the start byte and the speaker.
/// </summary>
public abstract class GraphRagQuote
{
    public string Id { get; set; } = "";
    public string SpeakerId { get; set; } = "";
    public string ChunkId { get; set; } = "";
    public int StartByte { get; set; }
    public int EndByte { get; set; }
    public string Words { get; set; } = "";
    public string RunId { get; set; } = "";
    public string ExtractorVersion { get; set; } = "";
}

/// <summary><c>SAYS</c>: a character's spoken words (novel), with the addressee's name when the text gives one.</summary>
public sealed class Says : GraphRagQuote
{
    public string? Addressee { get; set; }
}

/// <summary><c>QUOTED</c>: a person's quoted words (article).</summary>
public sealed class Quoted : GraphRagQuote { }
