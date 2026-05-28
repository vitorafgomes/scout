using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Logging;

namespace Scout.Host.Worker.Providers;

/// <summary>
/// DI registration for the local-runtime provider services
/// (Ollama detection + API client, binary locator).
/// </summary>
public static class LocalProvidersServiceCollectionExtensions
{
    public static IServiceCollection AddLocalProviders(
        this IServiceCollection services,
        IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        services
            .AddOptions<OllamaProviderOptions>()
            .Bind(config.GetSection(OllamaProviderOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IBinaryLocator, PathBinaryLocator>();

        services.AddSingleton<OllamaReadinessProbe>();
        services.AddSingleton<IOllamaReadinessProbe>(sp =>
            sp.GetRequiredService<OllamaReadinessProbe>());

        services.AddTransient<OllamaWarmupSuppressingLoggingHandler>();

        services
            .AddHttpClient<IOllamaApiClient, OllamaApiClient>((sp, client) =>
            {
                // No global timeout: long-running streams (model pull) must not
                // be capped here. Short operations (tags ping, delete) apply
                // their own bounded CTS inside the client.
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .RemoveAllLoggers()
            .AddHttpMessageHandler<OllamaWarmupSuppressingLoggingHandler>();

        services.AddSingleton<OllamaProviderService>();

        // Local-runtime lifecycle plumbing. Endpoints + autostart hosted
        // service are layered on top of these in a follow-up change.
        services
            .AddOptions<LocalRuntimeConfigStoreOptions>()
            .Bind(config.GetSection(LocalRuntimeConfigStoreOptions.SectionName));

        services.AddSingleton<IProcessLauncher, SystemProcessLauncher>();
        services.AddSingleton<ILocalRuntimeRegistry, InMemoryLocalRuntimeRegistry>();
        services.AddSingleton<ILocalRuntimeConfigStore, JsonFileLocalRuntimeConfigStore>();

        services.AddHostedService<LocalRuntimeAutostartService>();

        return services;
    }
}
