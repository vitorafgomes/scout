namespace Scout.Core.Features;

/// <summary>
/// Marker DI registration produced by
/// <see cref="FeaturePluginServiceCollectionExtensions.AddFeaturePlugin{TPlugin}"/>.
/// The host-side feature-plugin factory registry collects every instance
/// to build the feature-id to factory map.
/// </summary>
/// <param name="FeatureId">Feature id served by this plugin.</param>
/// <param name="PluginType">Concrete plugin type to resolve from DI.</param>
public sealed record FeaturePluginFactoryRegistration(string FeatureId, Type PluginType);
