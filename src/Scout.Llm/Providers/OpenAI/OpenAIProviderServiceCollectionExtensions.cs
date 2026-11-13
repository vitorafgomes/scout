using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Scout.Llm.Abstractions;

namespace Scout.Llm.Providers.OpenAI;

/// <summary>
/// DI registration for the OpenAI-compatible LLM provider.
/// </summary>
public static class OpenAIProviderServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="OpenAILlmProviderFactory"/> as the singleton
    /// <see cref="ILlmProviderFactory"/>. Expects <c>LlmOptions</c> to be
    /// configured by the caller.
    /// </summary>
    public static IServiceCollection AddOpenAILlmProvider(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ILlmProviderFactory, OpenAILlmProviderFactory>();
        return services;
    }
}
