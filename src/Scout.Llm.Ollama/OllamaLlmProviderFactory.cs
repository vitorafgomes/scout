using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Scout.Core.Llm;

namespace Scout.Llm.Ollama;

/// <summary>
/// <see cref="ILlmProviderFactory"/> implementation that wires Semantic
/// Kernel's Ollama chat-completion connector. Lives in its own project so
/// Scout.Core does not depend on the Connectors.Ollama prerelease package.
/// </summary>
public sealed class OllamaLlmProviderFactory : ILlmProviderFactory
{
    private readonly OllamaOptions _options;

    public OllamaLlmProviderFactory(IOptions<LlmOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Value);
        ArgumentNullException.ThrowIfNull(options.Value.Ollama);

        var ollama = options.Value.Ollama;

        if (string.IsNullOrWhiteSpace(ollama.Endpoint))
        {
            throw new ArgumentException(
                "OllamaOptions.Endpoint must be non-empty.", nameof(options));
        }
        if (string.IsNullOrWhiteSpace(ollama.Model))
        {
            throw new ArgumentException(
                "OllamaOptions.Model must be non-empty.", nameof(options));
        }

        _options = ollama;
    }

    public string Name => "Ollama";

    public void AddToKernel(IKernelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddOllamaChatCompletion(
            modelId: _options.Model,
            endpoint: new Uri(_options.Endpoint));
    }
}
