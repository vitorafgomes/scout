namespace Scout.Plugin.WebSearch;

/// <summary>
/// Configuration for the WebSearch plugin, bound from
/// "Scout:Plugins:WebSearch". The plugin self-gates on <see cref="Enabled"/>:
/// when disabled the registration is a clean no-op. It also gates on
/// <see cref="SearxUrl"/>: a blank value keeps the DuckDuckGo instant-answer and
/// page-fetch surface but drops <c>search_web</c>, so a host with no SearXNG
/// instance still works with zero setup.
/// </summary>
public sealed class WebSearchOptions
{
    /// <summary>
    /// Whether the plugin is active. Defaults to true. Set to false to disable it
    /// without removing configuration.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Base URL of a self-hosted SearXNG instance used for full-text web search,
    /// e.g. "http://localhost:8888". Empty (the default) disables
    /// <c>search_web</c> and leaves only the zero-setup DuckDuckGo surface.
    /// </summary>
    public string SearxUrl { get; set; } = "";
}
