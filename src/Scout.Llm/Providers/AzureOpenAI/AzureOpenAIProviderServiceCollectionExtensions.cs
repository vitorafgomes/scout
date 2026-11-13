using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Scout.Llm.Abstractions;

namespace Scout.Llm.Providers.AzureOpenAI;

/// <summary>
/// DI registration for the Azure OpenAI LLM provider.
/// </summary>
public static class AzureOpenAIProviderServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="AzureOpenAILlmProviderFactory"/> as the singleton
    /// <see cref="ILlmProviderFactory"/>. Expects <c>LlmOptions</c> to be
    /// configured by the caller.
    /// </summary>
    public static IServiceCollection AddAzureOpenAILlmProvider(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ILlmProviderFactory, AzureOpenAILlmProviderFactory>();
        return services;
    }
}
