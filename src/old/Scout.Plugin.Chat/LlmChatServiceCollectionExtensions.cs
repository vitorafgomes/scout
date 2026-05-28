using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Core.Chat;
using Scout.Core.Llm;
using Scout.Core.Plugins;

namespace Scout.Plugin.Chat;

/// <summary>
/// DI composition for the fallback chat plugin. Binds
/// <see cref="LlmChatOptions"/> from the <c>Scout:Plugins:Chat</c>
/// section and registers <see cref="LlmFallbackRouter"/> as an
/// <see cref="IMessageRouter"/>. The host is expected to call this
/// AFTER every other <c>AddXxxPlugin</c> so the fallback runs last.
/// </summary>
public static class LlmChatServiceCollectionExtensions
{
    /// <summary>Configuration root bound by this extension.</summary>
    public const string ConfigSection = "Scout:Plugins:Chat";

    public static IServiceCollection AddLlmChatPlugin(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<LlmChatOptions>(configuration.GetSection(ConfigSection));
        services.AddSingleton<IScoutPlugin, LlmChatPlugin>();
        services.AddSingleton<IMessageRouter, LlmFallbackRouter>();

        // Only register IChatCompletionService when no other component
        // already provides one (e.g. the CodeQA plugin when it is
        // configured). Scout.Core's kernel is constructed lazily inside
        // ScoutHost and is not itself a DI service, so the router needs
        // its own chat completion service resolved from the active
        // ILlmProviderFactory.
        if (!services.Any(d => d.ServiceType == typeof(IChatCompletionService)))
        {
            services.AddSingleton<IChatCompletionService>(sp =>
            {
                var llmFactory = sp.GetRequiredService<ILlmProviderFactory>();
                var builder = Kernel.CreateBuilder();
                llmFactory.AddToKernel(builder);
                var kernel = builder.Build();
                return kernel.GetRequiredService<IChatCompletionService>();
            });
        }

        return services;
    }
}
