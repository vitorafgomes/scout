namespace Scout.Plugin.Wikipedia;

/// <summary>
/// Configuration for the Wikipedia plugin, bound from "Scout:Plugins:Wikipedia".
/// The Wikimedia REST API needs no key, so the plugin gates on a single
/// <see cref="Enabled"/> flag (default true). Setting it to false leaves the
/// feature inactive: no client and no kernel plugin are registered.
/// </summary>
public sealed class WikipediaOptions
{
    /// <summary>
    /// Whether the plugin is active. Defaults to true; set to false to disable
    /// the plugin without removing its configuration section.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
