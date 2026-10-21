using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Scout.Core.Chat;

namespace Scout.Plugin.Agent;

/// <summary>
/// DI composition for the agentic tool-calling router. Binds
/// <see cref="AgentRouterOptions"/> from the <c>Scout:Plugins:Agent</c>
/// section and registers <see cref="AgentRouter"/> as an
/// <see cref="IMessageRouter"/>. Must be called AFTER every feature
/// <c>AddXxxPlugin</c> (so their tools are in the resolved kernel) and
/// BEFORE <c>AddLlmChatPlugin</c> (the streaming fallback must run last).
///
/// Does NOT register an <see cref="Microsoft.SemanticKernel.ChatCompletion.IChatCompletionService"/>:
/// the router resolves one from the host's resolved kernel so the call
/// carries the loaded plugins. Registering a standalone service here would
/// reintroduce the tool-less kernel bug.
/// </summary>
public static class AgentServiceCollectionExtensions
{
    /// <summary>Configuration root bound by this extension.</summary>
    public const string ConfigSection = "Scout:Plugins:Agent";

    public static IServiceCollection AddAgentRouter(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<AgentRouterOptions>(configuration.GetSection(ConfigSection));
        services.AddSingleton<IMessageRouter, AgentRouter>();

        return services;
    }
}
