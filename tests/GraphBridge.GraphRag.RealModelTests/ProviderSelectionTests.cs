using GraphBridge.Samples.GraphRagShared;

namespace GraphBridge.GraphRag.RealModelTests;

/// <summary>
/// GraphRAG v2, wave 7, offline: the samples choose a model with the environment contract AgentEval and AgentMemory share
/// (<c>AI_INFERENCE_PROVIDER</c>), read here from a dictionary rather than the process environment.
/// </summary>
public sealed class ProviderSelectionTests
{
    private const string Recordings = "no-recordings.json";

    [Fact]
    public void Bitdeer_needs_only_its_key_and_defaults_to_GLM_and_bge_m3()
    {
        var settings = Resolve(("AI_INFERENCE_PROVIDER", "bitdeer"), ("BITDEER_API_KEY", "secret-value-123"))!;
        Assert.Equal(("bitdeer", "https://api-inference.bitdeer.ai/v1/", "zai-org/GLM-5.3-Flash", "BAAI/bge-m3", 1024),
            (settings.Provider, settings.Endpoint!.ToString(), settings.ChatModel, settings.EmbeddingModel, settings.EmbeddingDimensions));
        Assert.Equal("zai-org/GLM-5.3-Flash@bitdeer", settings.ModelIdentity);

        var other = Resolve(("AI_INFERENCE_PROVIDER", "Bitdeer"), ("BITDEER_API_KEY", "k"), ("BITDEER_MODEL", "deepseek-ai/DeepSeek-V4.1-Flash"))!;
        Assert.Equal("deepseek-ai/DeepSeek-V4.1-Flash@bitdeer", other.ModelIdentity);
    }

    [Fact]
    public void Without_a_named_provider_the_samples_stay_offline_even_when_keys_are_set()
    {
        Assert.Null(Resolve(("BITDEER_API_KEY", "k"), ("OPENAI_API_KEY", "k"), ("AZURE_OPENAI_ENDPOINT", "https://example.invalid/")));
    }

    [Fact]
    public void A_named_provider_with_a_variable_missing_fails_naming_the_variable_and_never_a_value()
    {
        var missing = Assert.Throws<InvalidOperationException>(() => Resolve(("AI_INFERENCE_PROVIDER", "bitdeer")));
        Assert.Contains("BITDEER_API_KEY", missing.Message, StringComparison.Ordinal);
        var compatible = Assert.Throws<InvalidOperationException>(() => Resolve(("AI_INFERENCE_PROVIDER", "openai-compatible"),
            ("OPENAI_COMPATIBLE_ENDPOINT", "http://127.0.0.1:8000/v1"), ("OPENAI_COMPATIBLE_MODEL", "local")));
        Assert.Contains("OPENAI_COMPATIBLE_EMBEDDING_MODEL", compatible.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => Resolve(("AI_INFERENCE_PROVIDER", "azure")));
        var width = Assert.Throws<InvalidOperationException>(() => Resolve(("AI_INFERENCE_PROVIDER", "bitdeer"), ("BITDEER_API_KEY", "k"), ("AI_EMBEDDING_DIMENSIONS", "-3")));
        Assert.Contains("AI_EMBEDDING_DIMENSIONS", width.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://models.example.com/v1")]
    [InlineData("https://user:password@models.example.com/v1")]
    [InlineData("https://models.example.com/v1?key=token")]
    [InlineData("ftp://models.example.com/v1")]
    public void An_endpoint_that_could_leak_the_key_is_refused(string endpoint)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Resolve(("AI_INFERENCE_PROVIDER", "bitdeer"), ("BITDEER_API_KEY", "k"), ("BITDEER_ENDPOINT", endpoint)));
        Assert.DoesNotContain("password", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("token", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_print_without_the_key_and_endpoints_without_path_query_or_user_information()
    {
        var settings = Resolve(("AI_INFERENCE_PROVIDER", "ollama"), ("OLLAMA_MODEL", "qwen3"))!;
        Assert.Equal(("http://127.0.0.1:11434/v1/", "bge-m3", 1024), (settings.Endpoint!.ToString(), settings.EmbeddingModel, settings.EmbeddingDimensions));
        var keyed = Resolve(("AI_INFERENCE_PROVIDER", "bitdeer"), ("BITDEER_API_KEY", "secret-value-123"))!;
        Assert.DoesNotContain("secret-value-123", keyed.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value-123", keyed.Describe(), StringComparison.Ordinal);
        Assert.Equal("https://host.example:8443", SampleModelSettings.SafeEndpoint(new Uri("https://user:pw@host.example:8443/v1/token-in-path?key=token#frag")));
    }

    private static SampleModelSettings? Resolve(params (string Name, string Value)[] variables)
    {
        var environment = variables.ToDictionary(v => v.Name, v => v.Value);
        return SampleModelSettings.FromEnvironment(Recordings, name => environment.GetValueOrDefault(name));
    }
}
