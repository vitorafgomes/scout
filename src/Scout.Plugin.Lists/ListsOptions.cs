namespace Scout.Plugin.Lists;

/// <summary>
/// Configuration for the Lists plugin, bound from "Scout:Plugins:Lists". The
/// plugin self-gates on <see cref="Enabled"/>: when false the registration is a
/// clean no-op (no kernel plugin), so a host can turn the feature off entirely.
/// </summary>
public sealed class ListsOptions
{
    /// <summary>
    /// Whether the plugin is active. Defaults to true. Set false to disable the
    /// feature without removing configuration.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
