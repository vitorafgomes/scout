using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Scout.Llm.Abstractions;

namespace Scout.Llm.Providers.ChatGptPlan;

/// <summary>
/// DI registration for the ChatGPT plan LLM provider.
/// </summary>
public static class ChatGptPlanProviderServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="ChatGptPlanLlmProviderFactory"/> as the singleton
    /// <see cref="ILlmProviderFactory"/>. Expects <c>LlmOptions</c> to be
    /// configured by the caller.
    /// </summary>
    public static IServiceCollection AddChatGptPlanLlmProvider(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ILlmProviderFactory, ChatGptPlanLlmProviderFactory>();
        return services;
    }
}
