using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Scout.Llm.Abstractions;

namespace Scout.Llm.Providers.None;

/// <summary>
/// DI registration for the None LLM provider.
/// </summary>
public static class NoneProviderServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="NoneLlmProviderFactory"/> as the singleton
    /// <see cref="ILlmProviderFactory"/>. Needs no provider settings.
    /// </summary>
    public static IServiceCollection AddNoneLlmProvider(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ILlmProviderFactory, NoneLlmProviderFactory>();
        return services;
    }
}
