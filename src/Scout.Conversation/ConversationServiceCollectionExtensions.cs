using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Routing;
using Scout.Conversation.Streaming;
using Scout.Skills;

namespace Scout.Conversation;

/// <summary>
/// DI composition for the platform-agnostic conversation core. Registers the
/// router, stream pump, and conductor. Does NOT register a chat adapter — the
/// host adds exactly one (Discord today) on top.
/// </summary>
public static class ConversationServiceCollectionExtensions
{
    public const string ConfigSection = "Scout:Conversation";

    public static IServiceCollection AddScoutConversation(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<LlmStreamingRouterOptions>(configuration.GetSection(ConfigSection));

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new StreamPumpOptions
        {
            TimeProvider = sp.GetRequiredService<TimeProvider>(),
            Logger = sp.GetService<ILoggerFactory>()?.CreateLogger<StreamPump>(),
        });
        services.TryAddSingleton(sp => new StreamPump(sp.GetRequiredService<StreamPumpOptions>()));
        // Fallback skill selector so the conversation core resolves standalone.
        // The host registers the real embedding-driven selector via AddScoutSkills
        // beforehand; this TryAdd then no-ops and the real one wins.
        services.TryAddSingleton<ISkillSelector, NoOpSkillSelector>();

        services.TryAddSingleton<IConversationRouter, LlmStreamingRouter>();
        services.TryAddSingleton<ConversationConductor>();

        return services;
    }
}
