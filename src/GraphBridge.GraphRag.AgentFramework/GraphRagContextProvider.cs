using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace GraphBridge.GraphRag.AgentFramework;

/// <summary>
/// An Agent Framework <see cref="AIContextProvider"/> (ADR 0052, section 8; GR-42). On every invocation it gives the agent the
/// toolkit's document tools and instructions to answer questions about the documents only through
/// <c>answer_from_documents</c>, whose citations the toolkit checks against the stored text, or to say the documents don't answer.
/// </summary>
public sealed class GraphRagContextProvider : AIContextProvider
{
    /// <summary>The instructions added to every invocation unless others are given.</summary>
    public const string DefaultInstructions = """
        You can read a collection of stored documents through tools.
        - To answer a question about the documents, call answer_from_documents and give its answer with its citations, each with its quote, exactly as returned. Don't add claims of your own.
        - When it returns answered = false, say the documents don't answer the question, and give its reason.
        - search_documents returns passages to read. They are evidence, not answers: never cite a passage that answer_from_documents didn't cite.
        - find_things and related_facts, when present, describe what the documents mention. Facts are context, not citations.
        """;

    private readonly AITool[] _tools;
    private readonly string _instructions;

    /// <param name="tools">The toolkit's tools, from <see cref="GraphRagTools.Create{TContext}"/>.</param>
    /// <param name="instructions">Replaces <see cref="DefaultInstructions"/>.</param>
    public GraphRagContextProvider(IEnumerable<AIFunction> tools, string? instructions = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _tools = tools.Cast<AITool>().ToArray();
        if (!_tools.OfType<AIFunction>().Any(t => t.Name == GraphRagTools.AnswerTool))
            throw new ArgumentException($"The tools must include {GraphRagTools.AnswerTool}.", nameof(tools));
        _instructions = string.IsNullOrWhiteSpace(instructions) ? DefaultInstructions : instructions.Trim();
    }

    /// <summary>The provider with the tools for <typeparamref name="TContext"/>, which maps the GraphRAG model.</summary>
    public static GraphRagContextProvider Create<TContext>(IServiceProvider services, GraphRagToolOptions? options = null, string? instructions = null)
        where TContext : DbContext
        => new(GraphRagTools.Create<TContext>(services, options), instructions);

    /// <summary>The tools added to each invocation.</summary>
    public IReadOnlyList<AITool> Tools => _tools;

    protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new AIContext { Instructions = _instructions, Tools = _tools });
}
