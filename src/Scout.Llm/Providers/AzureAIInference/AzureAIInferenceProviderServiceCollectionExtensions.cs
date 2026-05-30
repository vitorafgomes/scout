using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Scout.Llm.Abstractions;

namespace Scout.Llm.Providers.AzureAIInference;

/// <summary>
/// DI registration for the Azure AI Inference LLM provider.
/// </summary>
public static class AzureAIInferenceProviderServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="AzureAIInferenceLlmProviderFactory"/> as the singleton
    /// <see cref="ILlmProviderFactory"/>. Expects <c>LlmOptions</c> to be
    /// configured by the caller.
    /// </summary>
    public static IServiceCollection AddAzureAIInferenceLlmProvider(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ILlmProviderFactory, AzureAIInferenceLlmProviderFactory>();
        return services;
    }
}
