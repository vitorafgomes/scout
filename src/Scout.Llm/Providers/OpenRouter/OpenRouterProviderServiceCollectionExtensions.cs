using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Scout.Llm.Abstractions;

namespace Scout.Llm.Providers.OpenRouter;

/// <summary>
/// DI registration for the OpenRouter LLM provider.
/// </summary>
public static class OpenRouterProviderServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="OpenRouterLlmProviderFactory"/> as the singleton
    /// <see cref="ILlmProviderFactory"/>. Expects <c>LlmOptions</c> to be
    /// configured by the caller.
    /// </summary>
    public static IServiceCollection AddOpenRouterLlmProvider(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ILlmProviderFactory, OpenRouterLlmProviderFactory>();
        return services;
    }
}
