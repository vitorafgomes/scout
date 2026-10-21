using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Scout.Conversation.Abstractions;

namespace Scout.Adapter.Slack;

/// <summary>
/// DI composition for the Slack chat adapter. Consumer hosts call
/// <c>services.AddSlackAdapter(configuration)</c> after the conversation core
/// is registered. Binds <see cref="SlackOptions"/> from "Scout:Adapters:Slack".
/// The adapter is registered as an <see cref="IChatAdapter"/> only when both the
/// bot token and the app-level token are present in configuration; when either
/// is absent the call is a clean no-op so an unconfigured host still starts.
/// </summary>
public static class SlackServiceCollectionExtensions
{
    public const string ConfigSection = "Scout:Adapters:Slack";

    public static IServiceCollection AddSlackAdapter(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<SlackOptions>(configuration.GetSection(ConfigSection));

        var section = configuration.GetSection(ConfigSection);
        var botToken = section.GetValue<string>("BotToken");
        var appLevelToken = section.GetValue<string>("AppLevelToken");
        if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(appLevelToken))
        {
            return services;
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<SlackMessageTranslator>();
        services.AddSingleton<SlackChatAdapter>();
        services.AddSingleton<IChatAdapter>(sp => sp.GetRequiredService<SlackChatAdapter>());

        return services;
    }
}
