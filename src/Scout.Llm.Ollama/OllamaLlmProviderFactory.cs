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
    private readonly Uri _endpoint;
    private readonly string _model;

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

        if (!Uri.TryCreate(ollama.Endpoint, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                $"OllamaOptions.Endpoint '{ollama.Endpoint}' is not a valid absolute " +
                $"http or https URI.",
                nameof(options));
        }

        _endpoint = parsed;
        _model = ollama.Model;
    }

    public string Name => "Ollama";

    public void AddToKernel(IKernelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddOllamaChatCompletion(modelId: _model, endpoint: _endpoint);
    }
}
