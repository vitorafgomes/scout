namespace Scout.Host.Worker.Features;

/// <summary>
/// Marker DI registration produced by
/// <see cref="FeaturesServiceCollectionExtensions.AddFeaturePlugin{TPlugin}"/>.
/// <see cref="FeaturePluginFactoryRegistry"/> collects every instance to
/// build the feature-id → factory map.
/// </summary>
/// <param name="FeatureId">Feature id served by this plugin.</param>
/// <param name="PluginType">Concrete plugin type to resolve from DI.</param>
public sealed record FeaturePluginFactoryRegistration(string FeatureId, Type PluginType);
