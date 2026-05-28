using Microsoft.Extensions.DependencyInjection;

namespace Scout.Core.Features;

/// <summary>
/// Extensions plugin assemblies call from their own
/// <c>AddXxxFeaturePlugin</c> wiring to register an
/// <see cref="IFeaturePlugin"/> implementation against a feature id.
/// </summary>
public static class FeaturePluginServiceCollectionExtensions
{
    /// <summary>
    /// Register <typeparamref name="TPlugin"/> as the
    /// <see cref="IFeaturePlugin"/> for <paramref name="featureId"/>. The
    /// host-side registrar collects every
    /// <see cref="FeaturePluginFactoryRegistration"/> at runtime.
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
}
