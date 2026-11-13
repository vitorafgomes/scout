using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Scout.Llm.Abstractions;

namespace Scout.Llm.Providers.Mistral;

/// <summary>
/// DI registration for the Mistral LLM provider.
/// </summary>
public static class MistralProviderServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="MistralLlmProviderFactory"/> as the singleton
    /// <see cref="ILlmProviderFactory"/>. Expects <c>LlmOptions</c> to be
    /// configured by the caller.
    /// </summary>
    public static IServiceCollection AddMistralLlmProvider(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ILlmProviderFactory, MistralLlmProviderFactory>();
        return services;
    }
}
