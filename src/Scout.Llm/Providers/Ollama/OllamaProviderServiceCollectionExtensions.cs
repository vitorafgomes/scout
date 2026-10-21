using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Scout.Llm.Abstractions;

namespace Scout.Llm.Providers.Ollama;

/// <summary>
/// DI registration for the Ollama LLM provider.
/// </summary>
public static class OllamaProviderServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="OllamaLlmProviderFactory"/> as the singleton
    /// <see cref="ILlmProviderFactory"/>. Expects <c>LlmOptions</c> to be
    /// configured by the caller.
    /// </summary>
    public static IServiceCollection AddOllamaLlmProvider(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ILlmProviderFactory, OllamaLlmProviderFactory>();
        return services;
    }
}
