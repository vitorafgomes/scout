namespace Scout.Plugin.Dictionary;

/// <summary>
/// Configuration for the Dictionary plugin, bound from "Scout:Plugins:Dictionary".
/// Both back-ends (dictionaryapi.dev and Datamuse) are keyless, so the plugin gates
/// on a single <see cref="Enabled"/> flag (default true). Setting it to false leaves
/// the feature inactive: no clients and no kernel plugin are registered, so an
/// unconfigured host still starts.
/// </summary>
public sealed class DictionaryOptions
{
    /// <summary>
    /// Whether the plugin is active. Defaults to true; set to false to disable the
    /// plugin without removing its configuration section.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
