using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Scout.Conversation.Abstractions;

namespace Scout.Adapter.Slack;

/// <summary>
/// DI composition for the Slack chat adapter. Consumer hosts call
/// <c>services.AddSlackAdapter(configuration)</c> after the conversation core
/// is registered. Binds <see cref="SlackOptions"/> from "Scout:Adapters:Slack"
/// and registers the adapter as the <see cref="IChatAdapter"/>.
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

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<SlackMessageTranslator>();
        services.AddSingleton<SlackChatAdapter>();
        services.AddSingleton<IChatAdapter>(sp => sp.GetRequiredService<SlackChatAdapter>());

        return services;
    }
}
