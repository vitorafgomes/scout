using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Scout.Adapter.Discord;
using Scout.Adapter.Slack;
using Scout.Adapter.Teams;
using Scout.Contracts;
using Scout.ControlPlane;
using Scout.Conversation;
using Scout.Knowledge;
using Scout.Llm;
using Scout.Plugin.CodeReview;
using Scout.Plugin.Converter;
using Scout.Plugin.Currency;
using Scout.Plugin.Dates;
using Scout.Plugin.Dictionary;
using Scout.Plugin.GitHub;
using Scout.Plugin.Holidays;
using Scout.Plugin.Ledger;
using Scout.Plugin.Lists;
using Scout.Plugin.Markets;
using Scout.Plugin.News;
using Scout.Plugin.Notes;
using Scout.Plugin.Nutrition;
using Scout.Plugin.Parcels;
using Scout.Plugin.Recipes;
using Scout.Plugin.Reminders;
using Scout.Plugin.SelfInfo;
using Scout.Plugin.Sports;
using Scout.Plugin.Weather;
using Scout.Plugin.WebSearch;
using Scout.Plugin.WhatToWatch;
using Scout.Plugin.Wikipedia;
using Scout.Plugin.WorldClock;
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
    /// project knowledge, the self-gating chat adapters, the GitHub plugin, the
    /// conversation worker, and the parent watchdog.
    /// </summary>
    public static IServiceCollection AddScoutHost(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IReadinessSignal, StdoutReadinessSignal>();

        // Ambient per-request identity, registered once explicitly so there is a
        // single shared instance the conductor writes and the SelfInfo plugin reads.
        // The conversation and plugin composition roots also TryAdd it; TryAdd keeps
        // this single registration.
        services.AddScoutRequestContext();

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

        // Semantic Kernel function-calling plugins. Self-gates on its configured token.
        services.AddGitHubPlugin(configuration);

        // Code review plugin. Snippet functions always work; repository analysis
        // self-gates on the GitHub token.
        services.AddCodeReviewPlugin(configuration);

        // Daily-assistant plugins. Each self-gates on its "Enabled" flag
        // (default true) and only reads free data sources or Scout's own local
        // store — no external mutation.
        services.AddWeatherPlugin(configuration);
        services.AddListsPlugin(configuration);
        services.AddWikipediaPlugin(configuration);
        services.AddCurrencyPlugin(configuration);
        services.AddDatesPlugin(configuration);
        services.AddHolidaysPlugin(configuration);
        services.AddNotesPlugin(configuration);

        // Wave 2 daily-assistant plugins. Same self-gating "Enabled" contract;
        // read-only or Scout-local descriptive-write only. WebSearch degrades to
        // its DuckDuckGo-only surface when SearxUrl is blank.
        services.AddRemindersPlugin(configuration);
        services.AddWorldClockPlugin(configuration);
        services.AddConverterPlugin(configuration);
        services.AddNewsPlugin(configuration);
        services.AddWebSearchPlugin(configuration);
        services.AddDictionaryPlugin(configuration);
        services.AddRecipesPlugin(configuration);

        // Wave 3 daily-assistant plugins. Same self-gating contract; read-only
        // or Scout-local descriptive-write only. Parcels self-gates on its
        // required ApiKey (no-op until configured), like the GitHub plugin.
        services.AddMarketsPlugin(configuration);
        services.AddSportsPlugin(configuration);
        services.AddNutritionPlugin(configuration);
        services.AddWhatToWatchPlugin(configuration);
        services.AddLedgerPlugin(configuration);
        services.AddParcelsPlugin(configuration);

        // Host self-diagnostics. Disabled by default: it reports host/install
        // facts to chat users and plugins have no per-request identity to gate
        // on, so a host must consciously opt in.
        services.AddSelfInfoPlugin(configuration);

        // Local-runtime and feature control surface. The domain services are
        // transport-agnostic; the worker exposes them over a loopback HTTP
        // endpoint the desktop UI drives. Registered before the conversation
        // worker so the endpoint marker is printed before the readiness marker.
        // note: no OS keyring is wired here, so the domain keeps its in-memory
        // credential store default; feature credentials are process-lifetime
        // only (feature toggles/settings still persist to disk). Follow-up.
        services.AddControlPlaneDomain(configuration);
        services.AddHostedService<ControlPlaneHttpHost>();

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
