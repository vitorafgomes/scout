namespace Scout.Plugin.Nutrition;

/// <summary>
/// Configuration for the Nutrition plugin, bound from "Scout:Plugins:Nutrition".
/// The plugin self-gates on <see cref="Enabled"/>; Open Food Facts needs no key,
/// so the feature is on by default.
/// </summary>
public sealed class NutritionOptions
{
    /// <summary>
    /// Whether the plugin is active. When false the registration is a clean no-op
    /// (no client, no kernel plugin), so an unconfigured host still starts.
    /// Defaults to true.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
