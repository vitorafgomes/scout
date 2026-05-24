using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Scout.Host.Worker.Features.Internal;

namespace Scout.Host.Worker.Features;

/// <summary>
/// DI registration for the Features subsystem. Mirrors
/// <c>LocalProvidersServiceCollectionExtensions</c>.
/// </summary>
public static class FeaturesServiceCollectionExtensions
{
    /// <summary>
    /// Register the catalog, state store, credential store, registrar,
    /// validators and bootstrap hosted service on
    /// <paramref name="services"/>.
    /// </summary>
    public static IServiceCollection AddFeatures(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<FeatureStateStoreOptions>()
            .Bind(configuration.GetSection(FeatureStateStoreOptions.SectionName));

        services.AddSingleton<IFeatureCatalog, FeatureCatalog>();
        services.AddSingleton<IFeatureStateStore, JsonFileFeatureStateStore>();
        services.AddSingleton<FeaturePluginFactoryRegistry>();
        services.AddSingleton<IFeatureKernelRegistrar, FeatureKernelRegistrar>();

        services.AddSingleton<ICredentialStore>(sp => SelectCredentialStore(sp));

        services.AddSingleton<IFeatureCredentialValidator, GitHubPlaceholderValidator>();
        services.AddSingleton<IFeatureCredentialValidator, KubernetesPlaceholderValidator>();

        services.AddHostedService<FeatureBootstrapHostedService>();

        return services;
    }

    /// <summary>
    /// Register a feature plugin implementation. Phase 2/3 use this to
    /// wire their <see cref="IFeaturePlugin"/> in a single line.
    /// </summary>
    public static IServiceCollection AddFeaturePlugin<TPlugin>(
        this IServiceCollection services,
        string featureId)
        where TPlugin : class, IFeaturePlugin
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);

        services.AddSingleton<TPlugin>();
        services.AddSingleton(new FeaturePluginFactoryRegistration(featureId, typeof(TPlugin)));
        return services;
    }

    private static ICredentialStore SelectCredentialStore(IServiceProvider sp)
    {
        if (OperatingSystem.IsLinux())
        {
            var store = ActivatorUtilities.CreateInstance<LibsecretCredentialStore>(sp);
            if (store.IsAvailable)
            {
                return store;
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            var store = ActivatorUtilities.CreateInstance<MacKeychainCredentialStore>(sp);
            if (store.IsAvailable)
            {
                return store;
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            var store = ActivatorUtilities.CreateInstance<WinCredCredentialStore>(sp);
            if (store.IsAvailable)
            {
                return store;
            }
        }

        return new InMemoryCredentialStore();
    }
}
