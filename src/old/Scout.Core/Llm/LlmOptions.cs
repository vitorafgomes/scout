namespace Scout.Core.Llm;

/// <summary>
/// Top-level LLM configuration. Bound from the "Scout:Llm" section by
/// <c>ScoutHostBuilder</c>. The active provider is selected by
/// <see cref="Provider"/>; provider-specific blocks (<see cref="Ollama"/>,
/// future hosted providers) carry the settings for each implementation.
/// </summary>
public sealed class LlmOptions
{
    /// <summary>Provider factory name, e.g. "Ollama". Must match
    /// <c>ILlmProviderFactory.Name</c>.</summary>
    public string Provider { get; set; } = "Ollama";

    public OllamaOptions Ollama { get; set; } = new();
}

/// <summary>Settings for the Ollama LLM provider.</summary>
public sealed class OllamaOptions
{
    /// <summary>Base URL of the Ollama daemon, e.g. http://localhost:11434.</summary>
    public string Endpoint { get; set; } = "http://localhost:11434";

    /// <summary>Model identifier, e.g. "llama3.1".</summary>
    public string Model { get; set; } = "llama3.1";
}
