namespace Scout.Plugin.Currency;

/// <summary>
/// Configuration for the Currency plugin, bound from "Scout:Plugins:Currency".
/// The plugin self-gates on <see cref="Enabled"/>; the underlying API needs no
/// key, so the feature is on by default.
/// </summary>
public sealed class CurrencyOptions
{
    /// <summary>
    /// Whether the plugin is active. When false the registration is a clean
    /// no-op (no client, no kernel plugin). Defaults to true.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
