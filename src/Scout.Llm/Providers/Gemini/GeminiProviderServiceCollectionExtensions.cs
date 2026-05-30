using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Scout.Llm.Abstractions;

namespace Scout.Llm.Providers.Gemini;

/// <summary>
/// DI registration for the Gemini LLM provider.
/// </summary>
public static class GeminiProviderServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="GeminiLlmProviderFactory"/> as the singleton
    /// <see cref="ILlmProviderFactory"/>. Expects <c>LlmOptions</c> to be
    /// configured by the caller.
    /// </summary>
    public static IServiceCollection AddGeminiLlmProvider(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ILlmProviderFactory, GeminiLlmProviderFactory>();
        return services;
    }
}
