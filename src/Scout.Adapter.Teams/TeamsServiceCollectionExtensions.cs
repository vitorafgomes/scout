using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Scout.Conversation.Abstractions;

namespace Scout.Adapter.Teams;

/// <summary>
/// DI composition for the Teams chat adapter. Consumer hosts call
/// <c>services.AddTeamsAdapter(configuration)</c> after the conversation core is
/// registered. Binds <see cref="TeamsOptions"/> from "Scout:Adapters:Teams".
/// The adapter is registered as an <see cref="IChatAdapter"/> only when an app id
/// is present in configuration; when absent the call is a clean no-op so an
/// unconfigured host still starts.
///
/// Live hosting additionally needs the Bot Framework webhook plumbing — a
/// <c>CloudAdapter</c>, an <c>IBot</c> that forwards turns to
/// <c>TeamsChatAdapter.ProcessActivityAsync</c>, and a <c>POST /api/messages</c>
/// endpoint. That part requires real Azure (bot registration + credentials) and
/// is deliberately NOT registered here; this method wires only the unit-testable
/// adapter, translator, and sink seam.
/// </summary>
public static class TeamsServiceCollectionExtensions
{
    public const string ConfigSection = "Scout:Adapters:Teams";

    public static IServiceCollection AddTeamsAdapter(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<TeamsOptions>(configuration.GetSection(ConfigSection));

        var appId = configuration.GetSection(ConfigSection).GetValue<string>("AppId");
        if (string.IsNullOrWhiteSpace(appId))
        {
            return services;
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<TeamsMessageTranslator>();
        services.AddSingleton<TeamsChatAdapter>();
        services.AddSingleton<IChatAdapter>(sp => sp.GetRequiredService<TeamsChatAdapter>());

        return services;
    }
}
