using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Scout.Llm.Abstractions;

namespace Scout.Llm.Providers.HuggingFace;

/// <summary>
/// DI registration for the HuggingFace LLM provider.
/// </summary>
public static class HuggingFaceProviderServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="HuggingFaceLlmProviderFactory"/> as the singleton
    /// <see cref="ILlmProviderFactory"/>. Expects <c>LlmOptions</c> to be
    /// configured by the caller.
    /// </summary>
    public static IServiceCollection AddHuggingFaceLlmProvider(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ILlmProviderFactory, HuggingFaceLlmProviderFactory>();
        return services;
    }
}
