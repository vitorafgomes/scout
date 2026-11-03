using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Scout.Adapter.Discord;
using Scout.Adapter.Local;
using Scout.Adapter.Slack;
using Scout.Adapter.Teams;
using Scout.Conversation;
using Scout.Conversation.Access;
using Scout.Knowledge;
using Scout.Llm;
using Scout.Operator;
using Scout.Plugin.GitHub;
using Scout.Plugin.Kubernetes;
using Scout.Plugin.YouTube;
using Scout.Plugin.YouTube.Abstractions;
using Scout.Skills;
using Scout.Transcription.Whisper;

namespace Scout.Host.Console;

/// <summary>
/// Single source of truth for the host's service graph. Program.cs and the
/// composition smoke test both call <see cref="AddScoutHost"/>, so the tested
/// wiring cannot drift from what production runs.
/// </summary>
internal static class HostComposition
{
    /// <summary>
    /// Register every service the host needs: readiness and pairing signals, LLM, conversation, project knowledge, voice transcription,
    /// the self-gating chat adapters (the local one first, so stdin has a reader before any network adapter) and plugins, the write gate,
    /// the YouTube channel operator when YouTube is configured, the worker, the watchdog.
    /// </summary>
    public static IServiceCollection AddScoutHost(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IReadinessSignal, StdoutReadinessSignal>();
        services.AddSingleton<IPairingSignal, StdoutPairingSignal>();

        services.AddScoutLlm(configuration);

        // Register skills before conversation so the embedding-driven selector
        // wins over the conversation core's NoOp fallback (a TryAdd).
        services.AddScoutSkills(configuration);
        services.AddScoutConversation(configuration);
        services.AddProjectKnowledge(configuration);

        // Local voice transcription, only when the configured Whisper model file exists.
        services.AddWhisperTranscription(configuration);

        // Each adapter self-gates on its configured credentials, so the host runs
        // whatever combination is configured and starts none when nothing is set.
        services.AddLocalAdapter(configuration);
        services.AddDiscordAdapter(configuration);
        services.AddSlackAdapter(configuration);
        services.AddTeamsAdapter(configuration);

        // Semantic Kernel function-calling plugins. Each self-gates on its own configuration.
        services.AddGitHubPlugin(configuration);
        services.AddKubernetesPlugin(configuration);
        services.AddYouTubePlugin(configuration);
        var gateOpen = AddWriteGate(services, configuration);
        AddYouTubeOperator(services, configuration, gateOpen);

        services.AddHostedService<ConversationWorker>();

        // Self-terminate if the supervising parent dies ungracefully. Gated on an env
        // var the supervisor sets, so standalone/interactive runs are never affected.
        services.AddParentWatchdog(
            Environment.GetEnvironmentVariable(ParentWatchdogRegistration.EnableEnvVar));

        return services;
    }

    /// <summary>
    /// The control plane's write gate over the socket and token Scout Desktop passes as
    /// <c>SCOUT__WORKERGATE__SOCKETPATH</c> and <c>SCOUT__WORKERGATE__TOKEN</c>; without
    /// both, a gate that refuses every write. Once read, the token leaves the process environment.
    /// </summary>
    /// <returns>True when the gate reaches the control plane.</returns>
    private static bool AddWriteGate(IServiceCollection services, IConfiguration configuration)
    {
        var socketPath = configuration["Scout:WorkerGate:SocketPath"];
        var token = configuration["Scout:WorkerGate:Token"];
        RemoveWorkerGateTokenFromEnvironment();
        if (string.IsNullOrWhiteSpace(socketPath) || string.IsNullOrWhiteSpace(token))
        {
            services.AddSingleton<Scout.Llm.Abstractions.IWriteGate, ClosedWriteGate>();
            return false;
        }

        services.AddSingleton(_ => new GrpcWriteGate(socketPath, token));
        services.AddSingleton<Scout.Llm.Abstractions.IWriteGate>(sp => sp.GetRequiredService<GrpcWriteGate>());
        return true;
    }

    /// <summary>
    /// The YouTube channel operator, only when the YouTube plugin registered its client; it reaches the control
    /// plane on the write gate's channel and token, or through a closed control plane when the gate is closed. The ffmpeg
    /// and font paths bind from <c>Scout:Operator</c>, which the desktop sets as environment variables.
    /// </summary>
    private static void AddYouTubeOperator(IServiceCollection services, IConfiguration configuration, bool gateOpen)
    {
        if (!services.Any(d => d.ServiceType == typeof(IYouTubeClient)))
        {
            return;
        }

        if (gateOpen)
        {
            services.AddSingleton<IOperatorControlPlane>(sp => new GrpcOperatorControlPlane(sp.GetRequiredService<GrpcWriteGate>()));
        }
        else
        {
            services.AddSingleton<IOperatorControlPlane, ClosedOperatorControlPlane>();
        }

        services.AddOptions<OperatorMediaOptions>().Bind(configuration.GetSection(OperatorMediaOptions.Section));
        services.AddYouTubeOperator();
    }

    /// <summary>
    /// Removes every environment variable that configuration reads as the worker gate token, in any
    /// casing or ':' spelling and with or without the <c>DOTNET_</c> or <c>ASPNETCORE_</c> prefix, so no
    /// process the worker starts inherits it.
    /// </summary>
    private static void RemoveWorkerGateTokenFromEnvironment()
    {
        foreach (var key in Environment.GetEnvironmentVariables().Keys.Cast<string>())
        {
            var normalized = key.ToUpperInvariant().Replace(":", "__", StringComparison.Ordinal);
            if (normalized.StartsWith("DOTNET_", StringComparison.Ordinal))
            {
                normalized = normalized["DOTNET_".Length..];
            }
            else if (normalized.StartsWith("ASPNETCORE_", StringComparison.Ordinal))
            {
                normalized = normalized["ASPNETCORE_".Length..];
            }

            if (normalized == "SCOUT__WORKERGATE__TOKEN")
            {
                Environment.SetEnvironmentVariable(key, null);
            }
        }
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
