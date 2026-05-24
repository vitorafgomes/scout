using Microsoft.Extensions.DependencyInjection;

namespace Scout.Host.Worker.Features;

/// <summary>
/// Resolves feature ids to <see cref="IFeaturePlugin"/> instances via DI.
/// Phase 1 ships zero registrations: both endpoints return
/// <c>501 features/not-yet-implemented</c>. Phase 2/3 add plugins through
/// <see cref="FeaturesServiceCollectionExtensions.AddFeaturePlugin{TPlugin}"/>.
/// </summary>
public sealed class FeaturePluginFactoryRegistry
{
    private readonly IServiceProvider _services;
    private readonly Dictionary<string, Type> _byId;

    /// <summary>Build the registry from every registered factory marker.</summary>
    public FeaturePluginFactoryRegistry(
        IEnumerable<FeaturePluginFactoryRegistration> registrations,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        _services = services ?? throw new ArgumentNullException(nameof(services));

        _byId = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var registration in registrations)
        {
            _byId[registration.FeatureId] = registration.PluginType;
        }
    }

    /// <summary>True when a factory is registered for this feature id.</summary>
    public bool HasFactory(string featureId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        return _byId.ContainsKey(featureId);
    }

    /// <summary>
    /// Resolves a plugin instance for <paramref name="featureId"/>.
    /// Returns null when no factory is registered.
    /// </summary>
    public IFeaturePlugin? Resolve(string featureId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);

        if (!_byId.TryGetValue(featureId, out var type))
        {
            return null;
        }

        return (IFeaturePlugin)_services.GetRequiredService(type);
    }
}
