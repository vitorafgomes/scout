using Microsoft.Extensions.DependencyInjection;
using Scout.Plugin.CodeQA.Embedding;

namespace Scout.Llm.Ollama;

/// <summary>
/// DI extension for registering the Ollama embedder against the
/// <see cref="IEmbedder"/> abstraction. Options binding stays out of this
/// helper so hosts can pick their own configuration root.
/// </summary>
internal static class OllamaEmbedderServiceCollectionExtensions
{
    internal static IServiceCollection AddOllamaEmbedder(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IEmbedder, OllamaEmbedder>();
        return services;
    }
}
