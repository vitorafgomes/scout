using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Scout.Conversation.Abstractions;

namespace Scout.Adapter.Discord;

/// <summary>
/// DI composition for the Discord chat adapter. Consumer hosts call
/// <c>services.AddDiscordAdapter(configuration)</c> after the conversation core
/// is registered. Binds <see cref="DiscordOptions"/> from
/// "Scout:Adapters:Discord". The adapter is registered as an
/// <see cref="IChatAdapter"/> only when a bot token is present in configuration;
/// when absent the call is a clean no-op so an unconfigured host still starts.
/// </summary>
public static class DiscordServiceCollectionExtensions
{
    public const string ConfigSection = "Scout:Adapters:Discord";

    public static IServiceCollection AddDiscordAdapter(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<DiscordOptions>(configuration.GetSection(ConfigSection));

        var botToken = configuration.GetSection(ConfigSection).GetValue<string>("BotToken");
        if (string.IsNullOrWhiteSpace(botToken))
        {
            return services;
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<DiscordMessageTranslator>();
        services.AddSingleton<DiscordChatAdapter>();
        services.AddSingleton<IChatAdapter>(sp => sp.GetRequiredService<DiscordChatAdapter>());

        return services;
    }
}
