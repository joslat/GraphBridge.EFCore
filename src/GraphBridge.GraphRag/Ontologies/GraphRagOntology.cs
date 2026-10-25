using System.Globalization;
using System.Reflection;
using System.Text;
using GraphBridge.GraphRag.Ontologies;

namespace GraphBridge.GraphRag;

/// <summary>
/// What extraction looks for and how it is stored (ADR 0052, section 7): the classes of things, the facts allowed between
/// them, who-said-it quotes, the names that never merge and the extraction instructions. Use one of
/// <see cref="GraphRagOntologies"/>, or <see cref="Custom"/> with your own classes. Pass it to <c>AddGraphRag</c>.
/// </summary>
public sealed class GraphRagOntology
{
    private GraphRagOntologyRuntime? _runtime;

    internal GraphRagOntology(string name, IReadOnlyList<GraphRagOntologyClass> classes, IReadOnlyList<GraphRagOntologyRelationship> relationships,
        IReadOnlyList<GraphRagOntologyQuote> quotes, IReadOnlyList<string> placeholders, string instructions)
    {
        (Name, Classes, Relationships, Quotes, Placeholders, Instructions) = (name, classes, relationships, quotes, placeholders, instructions);
        Fingerprint = GraphRagEmbeddingSet.Hash(Canonical());
    }

    public string Name { get; }
    public IReadOnlyList<GraphRagOntologyClass> Classes { get; }
    public IReadOnlyList<GraphRagOntologyRelationship> Relationships { get; }
    public IReadOnlyList<GraphRagOntologyQuote> Quotes { get; }
    /// <summary>Normalised names that never merge, such as "the man" or "someone".</summary>
    public IReadOnlyList<string> Placeholders { get; }
    public string Instructions { get; }
    /// <summary>Hashes everything above; it joins the ingestion settings, so changing the ontology re-ingests.</summary>
    public string Fingerprint { get; }

    /// <summary>An ontology of your own classes, which derive from <see cref="GraphRagEntity"/>, <see cref="GraphRagFact"/> and <see cref="GraphRagQuote"/>.</summary>
    public static GraphRagOntology Custom(string name, Action<GraphRagOntologyBuilder> build)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(build);
        var builder = new GraphRagOntologyBuilder(name);
        build(builder);
        return builder.Build();
    }

    /// <summary>
    /// The same ontology with every class label prefixed, for an application whose own model already uses a label such as
    /// <c>Person</c>: <c>WithLabelPrefix("Rag")</c> stores <c>RagPerson</c> with <c>MENTIONED_IN_RAG_PERSON</c>. The kinds
    /// shown to the model and the fact and quote types stay the same.
    /// </summary>
    public GraphRagOntology WithLabelPrefix(string prefix)
    {
        GraphRagOntologyBuilder.Validate(prefix, "label prefix");
        return new(Name, Classes.Select(c => c with { Label = prefix + c.Label }).ToArray(), Relationships, Quotes, Placeholders, Instructions);
    }

    internal GraphRagOntologyRuntime Runtime => _runtime ??= new(this);

    internal GraphRagOntologyClass? FindClass(string kind) => Classes.FirstOrDefault(c => c.Kind == kind);

    /// <summary>Unicode NFC, whitespace runs collapsed, trimmed and case-folded: the exact-match key of resolution.</summary>
    internal static string Normalize(string name)
    {
        var text = new StringBuilder(name.Length);
        foreach (var c in name.Normalize(NormalizationForm.FormC))
        {
            if (char.IsWhiteSpace(c))
            {
                if (text.Length > 0 && text[^1] != ' ') text.Append(' ');
                continue;
            }
            text.Append(char.ToLowerInvariant(c));
        }
        return text.ToString().TrimEnd(' ');
    }

    private string Canonical()
    {
        var text = new StringBuilder("graphrag-ontology-1\n").Append(Name).Append('\n');
        foreach (var c in Classes)
            text.Append("class ").Append(c.Kind).Append(' ').Append(c.Label).Append(' ').Append(c.Aliases ? "aliases" : "no-aliases").Append(' ')
                .AppendJoin(',', c.Properties).Append(' ').Append(c.Description).Append('\n');
        foreach (var r in Relationships)
            text.Append("relationship ").Append(r.FromKind).Append(' ').Append(r.Type).Append(' ').Append(r.ToKind).Append(' ')
                .AppendJoin(',', r.Properties).Append(' ').Append(r.Description).Append('\n');
        foreach (var q in Quotes)
            text.Append("quote ").Append(q.Type).Append(' ').Append(q.SpeakerKind).Append(' ').Append(q.Addressee).Append(' ').Append(q.Description).Append('\n');
        text.Append("placeholders ").AppendJoin('|', Placeholders).Append('\n').Append(Instructions);
        return text.ToString();
    }
}

/// <summary>
/// A class of things: its CLR type, the kind name the model sees, the stored label, what it means, whether it has aliases,
/// and its other extracted properties.
/// </summary>
public sealed record GraphRagOntologyClass(Type ClrType, string Kind, string Label, string Description, bool Aliases, IReadOnlyList<string> Properties)
{
    /// <summary>The mention relationship type, <c>MENTIONED_IN_</c> and the label in upper snake case.</summary>
    public string MentionType => "MENTIONED_IN_" + GraphRagOntologyBuilder.UpperSnake(Label);
    internal Type MentionClrType => typeof(GraphRagMention<>).MakeGenericType(ClrType);
}

/// <summary>A fact type between two kinds, or between a kind and the document (<see cref="GraphRagDocument"/>, kind <c>Document</c>).</summary>
public sealed record GraphRagOntologyRelationship(Type ClrType, string Type, Type From, string FromKind, Type To, string ToKind,
    string Description, IReadOnlyList<string> Properties);

/// <summary>A who-said-it quote type and its speaker class. <see cref="Addressee"/>: the quote records who was addressed.</summary>
public sealed record GraphRagOntologyQuote(Type ClrType, string Type, Type Speaker, string SpeakerKind, string Description, bool Addressee);

/// <summary>Builds a custom ontology. Labels default to the class names; extra properties are string properties of the class.</summary>
public sealed class GraphRagOntologyBuilder
{
    internal const string DocumentKind = "Document";
    private readonly string _name;
    private readonly List<GraphRagOntologyClass> _classes = [];
    private readonly List<GraphRagOntologyRelationship> _relationships = [];
    private readonly List<GraphRagOntologyQuote> _quotes = [];
    private readonly List<string> _placeholders = [];
    private string? _instructions;

    internal GraphRagOntologyBuilder(string name) => _name = name;

    /// <param name="description">What the class means, shown to the model.</param>
    /// <param name="aliases">Whether the class keeps the other names the text uses.</param>
    /// <param name="properties">Further string properties of the class to extract, such as <c>Date</c>.</param>
    public GraphRagOntologyBuilder Class<TEntity>(string description, bool aliases = true, params string[] properties) where TEntity : GraphRagEntity, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(properties);
        var label = typeof(TEntity).Name;
        if (_classes.Any(c => c.ClrType == typeof(TEntity) || c.Label == label))
            throw new ArgumentException($"Class '{label}' is registered twice.");
        Validate(label, "label");
        RequireStrings(typeof(TEntity), properties, typeof(GraphRagEntity));
        _classes.Add(new(typeof(TEntity), label, label, description.Trim(), aliases, Array.AsReadOnly(properties.ToArray())));
        return this;
    }

    /// <summary>A fact type. <typeparamref name="TFrom"/> and <typeparamref name="TTo"/> are registered classes, or <see cref="GraphRagDocument"/> for the document itself.</summary>
    public GraphRagOntologyBuilder Relationship<TFact, TFrom, TTo>(string type, string description, params string[] properties)
        where TFact : GraphRagFact, new() where TFrom : class where TTo : class
        => Relationship(typeof(TFact), typeof(TFrom), typeof(TTo), type, description, properties);

    /// <summary>A who-said-it quote type spoken by <typeparamref name="TSpeaker"/>; <paramref name="addressee"/> needs a <c>string? Addressee</c> property.</summary>
    public GraphRagOntologyBuilder Quote<TQuote, TSpeaker>(string type, string description, bool addressee = false)
        where TQuote : GraphRagQuote, new() where TSpeaker : GraphRagEntity
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        Validate(type, "quote type");
        var speaker = Registered(typeof(TSpeaker));
        if (_quotes.Any(q => q.ClrType == typeof(TQuote) || q.Type == type)) throw new ArgumentException($"Quote type '{type}' is registered twice.");
        if (addressee) RequireStrings(typeof(TQuote), ["Addressee"], typeof(GraphRagQuote));
        _quotes.Add(new(typeof(TQuote), type, typeof(TSpeaker), speaker, description.Trim(), addressee));
        return this;
    }

    /// <summary>Names that never merge, compared after normalisation (case, spacing).</summary>
    public GraphRagOntologyBuilder Placeholders(params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        foreach (var name in names)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            var normalized = GraphRagOntology.Normalize(name);
            if (!_placeholders.Contains(normalized)) _placeholders.Add(normalized);
        }
        return this;
    }

    /// <summary>The extraction instructions; the schema's description of the classes and facts is appended to them.</summary>
    public GraphRagOntologyBuilder Instructions(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        _instructions = text.Trim();
        return this;
    }

    internal GraphRagOntologyBuilder Relationship(Type fact, Type from, Type to, string type, string description, string[] properties)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(properties);
        Validate(type, "relationship type");
        var fromLabel = Endpoint(from);
        var toLabel = Endpoint(to);
        if (from == typeof(GraphRagDocument) && to == typeof(GraphRagDocument))
            throw new ArgumentException($"Relationship '{type}' can't link the document to itself.");
        if (_relationships.Any(r => r.ClrType == fact)) throw new ArgumentException($"Fact class '{fact.Name}' is registered twice.");
        if (_relationships.Any(r => r.Type == type && r.FromKind == fromLabel && r.ToKind == toLabel))
            throw new ArgumentException($"Relationship ({fromLabel})-[:{type}]->({toLabel}) is registered twice.");
        RequireStrings(fact, properties, typeof(GraphRagFact));
        _relationships.Add(new(fact, type, from, fromLabel, to, toLabel, description.Trim(), Array.AsReadOnly(properties.ToArray())));
        return this;
    }

    internal GraphRagOntology Build()
    {
        if (_classes.Count == 0) throw new ArgumentException($"Ontology '{_name}' needs at least one class.");
        var types = _relationships.Select(r => r.Type).Concat(_quotes.Select(q => q.Type)).ToHashSet(StringComparer.Ordinal);
        if (_classes.FirstOrDefault(c => types.Contains(c.MentionType)) is { } clash)
            throw new ArgumentException($"Relationship type '{clash.MentionType}' is the mention type of class '{clash.Label}'.");
        if (_quotes.FirstOrDefault(q => _relationships.Any(r => r.Type == q.Type)) is { } quote)
            throw new ArgumentException($"Quote type '{quote.Type}' is also a relationship type.");
        return new(_name.Trim(), _classes.AsReadOnly(), _relationships.AsReadOnly(), _quotes.AsReadOnly(), _placeholders.AsReadOnly(),
            _instructions ?? GraphRagOntologies.GenericInstructions);
    }

    internal static string UpperSnake(string label)
    {
        var text = new StringBuilder(label.Length + 8);
        for (var i = 0; i < label.Length; i++)
        {
            var c = label[i];
            if (char.IsUpper(c) && i > 0 && (char.IsLower(label[i - 1]) || i + 1 < label.Length && char.IsLower(label[i + 1])) && label[i - 1] != '_')
                text.Append('_');
            text.Append(char.ToUpperInvariant(c));
        }
        return text.ToString();
    }

    private string Endpoint(Type type) => type == typeof(GraphRagDocument) ? DocumentKind : Registered(type);

    private string Registered(Type type)
        => _classes.FirstOrDefault(c => c.ClrType == type)?.Kind
            ?? throw new ArgumentException($"'{type.Name}' isn't a registered class; register it with Class<{type.Name}>(...) first.");

    internal static void Validate(string name, string what)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        static bool Initial(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';
        if (!Initial(name[0]) || name.Any(c => !Initial(c) && c is not (>= '0' and <= '9')))
            throw new ArgumentException($"The {what} '{name}' must match [A-Za-z_][A-Za-z0-9_]*.");
        if (name == DocumentKind) throw new ArgumentException($"The {what} '{DocumentKind}' is reserved for the document itself.");
    }

    // Extra properties are public read-write string properties declared by the class itself, never the base's.
    private static void RequireStrings(Type type, IEnumerable<string> properties, Type root)
    {
        foreach (var name in properties)
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property is null || property.PropertyType != typeof(string) || !property.CanRead || !property.CanWrite || property.DeclaringType == root)
                throw new ArgumentException($"'{type.Name}.{name}' must be a public read-write string property declared by {type.Name}.");
        }
    }
}

/// <summary>The three standard ontologies of the design (sections 5.3 to 5.5).</summary>
public static class GraphRagOntologies
{
    internal const string GenericInstructions = """
        You read one passage of a document and list what it says, as a small knowledge graph.
        - List the things the passage names, using only the kinds below. Give each thing's name as the passage writes it, and copy every phrase in the passage that refers to it into "mentions", exactly as written.
        - List a relationship only when the passage itself states it, using only the relationship types and kinds below. Never add what you know from elsewhere.
        - Use "Document" as the kind for the document this passage comes from, where a relationship type allows it.
        - Leave out anything you're unsure of. An empty list is a good answer for a passage that names nothing.
        Reply with one JSON object that matches the schema, and nothing else.
        """;

    private static readonly string[] CommonPlaceholders =
    [
        "someone", "somebody", "anyone", "anybody", "everyone", "everybody", "no one", "nobody", "he", "she", "it", "they",
        "him", "her", "them", "i", "me", "we", "us", "you", "the man", "the woman", "the boy", "the girl", "the child",
        "the person", "the people", "a man", "a woman", "a person", "the stranger", "the speaker", "the author"
    ];

    private static GraphRagOntology? _generic, _novel, _article;

    /// <summary>For any text: <c>Person</c>, <c>Organization</c>, <c>Place</c>, <c>Event</c>, <c>Concept</c>, and the facts between them.</summary>
    public static GraphRagOntology Generic => _generic ??= GraphRagOntology.Custom("generic-document", builder =>
    {
        AddGeneric(builder);
        builder.Placeholders(CommonPlaceholders).Placeholders("the company", "the organization", "the place", "the event", "the city", "the country")
            .Instructions(GenericInstructions);
    });

    /// <summary>For fiction: <c>Character</c>, <c>Place</c>, <c>Group</c>, <c>Event</c>, the facts between them, and <c>SAYS</c>.</summary>
    public static GraphRagOntology Novel => _novel ??= GraphRagOntology.Custom("novel", builder =>
    {
        builder.Class<Character>("A person, animal or creature that acts or speaks in the story.")
            .Class<Place>("A named place in the story: a country, town, house, room, garden or landscape.")
            .Class<Group>("A family, household, crew, company or other group of characters.", aliases: false)
            .Class<Event>("A scene or happening in the story.", aliases: false, nameof(Event.Chapter))
            .Relationship<KinOf, Character, Character>("KIN_OF", "The first character is a relative of the second; kind is parent, child, sibling, spouse or other.", nameof(KinOf.Kind))
            .Relationship<RelatedTo<Character, Character>, Character, Character>("RELATED_TO",
                "Another relation between two characters; kind is friend, employer, employee, rival or other.",
                nameof(RelatedTo<Character, Character>.Kind), nameof(RelatedTo<Character, Character>.Description))
            .Relationship<MemberOf<Character, Group>, Character, Group>("MEMBER_OF", "The character belongs to the group.")
            .Relationship<ParticipatesIn<Character>, Character, Event>("PARTICIPATES_IN", "The character takes part in the event.")
            .Relationship<TakesPlaceIn, Event, Place>("TAKES_PLACE_IN", "The event happens at the place.")
            .Quote<Says, Character>("SAYS", "The character speaks these words in the passage; copy only the spoken words, without the quotation marks, and name who is addressed when the passage says.", addressee: true)
            .Placeholders(CommonPlaceholders).Placeholders("a voice", "the voice", "the creature", "the animal", "the other", "the others")
            .Instructions(GenericInstructions + """

                Characters include animals and creatures that act or speak. Record speech with SAYS: the speaker and the spoken words, copied exactly.
                """);
    });

    /// <summary>For news, posts, papers and press releases: the generic classes, <c>Topic</c>, <c>AUTHORED</c>, <c>ABOUT</c> and <c>QUOTED</c>.</summary>
    public static GraphRagOntology Article => _article ??= GraphRagOntology.Custom("article", builder =>
    {
        AddGeneric(builder);
        builder.Class<Topic>("A subject the article is about.", aliases: false)
            .Relationship<Authored, Person, GraphRagDocument>("AUTHORED", "The person wrote the article (kind Document).")
            .Relationship<About, GraphRagDocument, Topic>("ABOUT", "The article (kind Document) is about the topic.")
            .Quote<Quoted, Person>("QUOTED", "The person is quoted saying these words in the passage; copy only the quoted words, without the quotation marks.")
            .Placeholders(CommonPlaceholders).Placeholders("a spokesperson", "the spokesperson", "officials", "the company", "the organization")
            .Instructions(GenericInstructions + """

                Record direct quotations with QUOTED: the person quoted and the quoted words, copied exactly.
                """);
    });

    private static void AddGeneric(GraphRagOntologyBuilder builder)
    {
        builder.Class<Person>("A named individual.")
            .Class<Organization>("A company, institution, government body, team or other organised group.")
            .Class<Place>("A named location: a country, city, region, building or natural feature.")
            .Class<Event>("A named happening, such as a meeting, launch, election or release.", aliases: false, nameof(Event.Date))
            .Class<Concept>("A topic, product, technology or idea the text discusses.")
            .Relationship<WorksFor, Person, Organization>("WORKS_FOR", "The person works for the organisation.", nameof(WorksFor.Role))
            .Relationship<MemberOf<Person, Organization>, Person, Organization>("MEMBER_OF", "The person is a member of the organisation.")
            .Relationship<MemberOf<Organization, Organization>, Organization, Organization>("MEMBER_OF", "The organisation is a member of the other.")
            .Relationship<LocatedIn<Person>, Person, Place>("LOCATED_IN", "The person is located in the place.")
            .Relationship<LocatedIn<Organization>, Organization, Place>("LOCATED_IN", "The organisation is located in the place.")
            .Relationship<LocatedIn<Event>, Event, Place>("LOCATED_IN", "The event happens in the place.")
            .Relationship<LocatedIn<Place>, Place, Place>("LOCATED_IN", "The place lies within the other.")
            .Relationship<ParticipatesIn<Person>, Person, Event>("PARTICIPATES_IN", "The person takes part in the event.", nameof(ParticipatesIn<Person>.Role))
            .Relationship<ParticipatesIn<Organization>, Organization, Event>("PARTICIPATES_IN", "The organisation takes part in the event.", nameof(ParticipatesIn<Organization>.Role))
            .Relationship<PartOf<Organization>, Organization, Organization>("PART_OF", "The organisation is part of the other.")
            .Relationship<PartOf<Place>, Place, Place>("PART_OF", "The place is part of the other.")
            .Relationship<PartOf<Concept>, Concept, Concept>("PART_OF", "The concept is part of the other.");
        // RELATED_TO is the catch-all between any two of the five classes: one association class per pair.
        Type[] kinds = [typeof(Person), typeof(Organization), typeof(Place), typeof(Event), typeof(Concept)];
        foreach (var from in kinds)
            foreach (var to in kinds)
                builder.Relationship(typeof(RelatedTo<,>).MakeGenericType(from, to), from, to, "RELATED_TO",
                    string.Create(CultureInfo.InvariantCulture, $"Another relation the passage states between a {from.Name} and a {to.Name}; describe it."),
                    [nameof(RelatedTo<Person, Person>.Description)]);
    }
}
