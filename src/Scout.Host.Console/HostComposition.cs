using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Scout.Adapter.Discord;
using Scout.Adapter.Slack;
using Scout.Adapter.Teams;
using Scout.Conversation;
using Scout.Knowledge;
using Scout.Llm;
using Scout.Plugin.GitHub;
using Scout.Plugin.Kubernetes;
using Scout.Skills;

namespace Scout.Host.Console;

/// <summary>
/// Single source of truth for the host's service graph. Program.cs and the
/// composition smoke test both call <see cref="AddScoutHost"/>, so the tested
/// wiring cannot drift from what production runs.
/// </summary>
internal static class HostComposition
{
    /// <summary>
    /// Register every service the host needs: readiness signal, LLM, conversation,
    /// project knowledge, the self-gating chat adapters, the GitHub and Kubernetes plugins,
    /// the conversation worker, and the parent watchdog.
    /// </summary>
    public static IServiceCollection AddScoutHost(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IReadinessSignal, StdoutReadinessSignal>();

        services.AddScoutLlm(configuration);

        // Register skills before conversation so the embedding-driven selector
        // wins over the conversation core's NoOp fallback (a TryAdd).
        services.AddScoutSkills(configuration);
        services.AddScoutConversation(configuration);
        services.AddProjectKnowledge(configuration);

        // Each adapter self-gates on its configured credentials, so the host runs
        // whatever combination is configured and starts none when nothing is set.
        services.AddDiscordAdapter(configuration);
        services.AddSlackAdapter(configuration);
        services.AddTeamsAdapter(configuration);

        // Semantic Kernel function-calling plugins. Each self-gates on its own configuration.
        services.AddGitHubPlugin(configuration);
        services.AddKubernetesPlugin(configuration);

        services.AddHostedService<ConversationWorker>();

        // Self-terminate if the supervising parent dies ungracefully. Gated on an env
        // var the supervisor sets, so standalone/interactive runs are never affected.
        services.AddParentWatchdog(
            Environment.GetEnvironmentVariable(ParentWatchdogRegistration.EnableEnvVar));

        return services;
    }

    /// <summary>
    /// Minimal service graph for the one-shot <c>index &lt;project&gt;</c> command:
    /// only the LLM embeddings and project knowledge that <see cref="IndexEntryPoint"/>
    /// needs. Deliberately registers no chat adapters, no conversation, no skills,
    /// and no conversation worker or watchdog so a one-shot index never spins up the
    /// inbound chat path.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddScoutIndexHost(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddScoutLlm(configuration);
        services.AddProjectKnowledge(configuration);

        return services;
    }
}
