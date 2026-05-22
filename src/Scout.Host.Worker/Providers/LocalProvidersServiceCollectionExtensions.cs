using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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

        services
            .AddHttpClient<IOllamaApiClient, OllamaApiClient>((sp, client) =>
            {
                var opts = sp.GetRequiredService<
                    Microsoft.Extensions.Options.IOptions<OllamaProviderOptions>>().Value;
                // Mirror the ping timeout at the HTTP layer so a stuck socket
                // cannot exceed the documented ceiling.
                client.Timeout = opts.PingTimeout;
            });

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
