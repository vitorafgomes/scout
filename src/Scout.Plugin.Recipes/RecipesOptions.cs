namespace Scout.Plugin.Recipes;

/// <summary>
/// Configuration for the Recipes plugin, bound from "Scout:Plugins:Recipes". The
/// plugin self-gates on <see cref="Enabled"/>: when disabled the registration is a
/// clean no-op (no client, no kernel plugin), so an unconfigured host still starts.
/// </summary>
public sealed class RecipesOptions
{
    /// <summary>
    /// Whether the plugin is active. Defaults to true. Set to false to disable it
    /// without removing configuration.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
