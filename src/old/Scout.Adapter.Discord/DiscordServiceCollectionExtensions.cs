using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Scout.Adapter.Discord.Streaming;
using Scout.Core.Chat;

namespace Scout.Adapter.Discord;

/// <summary>
/// DI composition for the Discord chat adapter. Consumer apps call
/// <c>services.AddDiscordAdapter(configuration)</c> after <c>AddScout(...)</c>.
/// Binds <see cref="DiscordOptions"/> from "Scout:Adapters:Discord" and
/// registers the adapter both by concrete type and through the
/// <see cref="IChatAdapter"/> contract.
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

        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<DiscordMessageTranslator>();
        services.AddSingleton<DiscordMessageHandler>();
        services.AddSingleton<IDiscordReplyPoster, DiscordEmbedReplyPoster>();
        services.AddSingleton<DiscordChatAdapter>();
        services.AddSingleton<IChatAdapter>(
            sp => sp.GetRequiredService<DiscordChatAdapter>());

        return services;
    }
}
