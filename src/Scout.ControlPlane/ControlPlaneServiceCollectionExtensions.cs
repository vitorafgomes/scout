using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Scout.ControlPlane.Features;
using Scout.ControlPlane.Providers;

namespace Scout.ControlPlane;

/// <summary>DI registration for the control-plane domain services.</summary>
public static class ControlPlaneServiceCollectionExtensions
{
    /// <summary>Register the provider and feature domain services.</summary>
    public static IServiceCollection AddControlPlaneDomain(
        this IServiceCollection services,
        IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        AddProviders(services, config);
        AddFeatures(services, config);
        return services;
    }

    private static void AddProviders(IServiceCollection services, IConfiguration config)
    {
        services
            .AddOptions<OllamaProviderOptions>()
            .Bind(config.GetSection(OllamaProviderOptions.SectionName))
            .ValidateOnStart();

        services
            .AddOptions<LocalRuntimeConfigStoreOptions>()
            .Bind(config.GetSection(LocalRuntimeConfigStoreOptions.SectionName));

        services.AddSingleton<IBinaryLocator, PathBinaryLocator>();
        services.AddSingleton<OllamaReadinessProbe>();
        services.AddSingleton<IOllamaReadinessProbe>(sp =>
            sp.GetRequiredService<OllamaReadinessProbe>());

        services
            .AddHttpClient<IOllamaApiClient, OllamaApiClient>((_, client) =>
            {
                // No global timeout: long-running streams (model pull) must not
                // be capped here. Short operations apply their own bounded CTS.
                client.Timeout = Timeout.InfiniteTimeSpan;
            });

        services.AddSingleton<IProcessLauncher, SystemProcessLauncher>();
        services.AddSingleton<ILocalRuntimeRegistry, InMemoryLocalRuntimeRegistry>();
        services.AddSingleton<ILocalRuntimeConfigStore, JsonFileLocalRuntimeConfigStore>();

        services.AddSingleton<OllamaProviderService>();
        services.AddSingleton<ProvidersService>();
    }

    private static void AddFeatures(IServiceCollection services, IConfiguration config)
    {
        services
            .AddOptions<FeatureStateStoreOptions>()
            .Bind(config.GetSection(FeatureStateStoreOptions.SectionName));

        services.AddSingleton<IFeatureCatalog, FeatureCatalog>();
        services.AddSingleton<IFeatureStateStore, JsonFileFeatureStateStore>();

        // OS keyring selection is host-specific; the in-memory fallback keeps
        // the domain runnable in CI/containers. The host can override.
        services.TryAddSingleton<ICredentialStore>(_ => new InMemoryCredentialStore());
        services.TryAddSingleton<IFeatureActivator, NullFeatureActivator>();

        services.AddSingleton<FeaturesService>();
    }
}
