namespace Scout.Plugin.News;

/// <summary>
/// Configuration for the News plugin, bound from "Scout:Plugins:News". The plugin
/// self-gates on <see cref="Enabled"/>: when disabled the registration is a clean
/// no-op (no clients, no store, no kernel plugin), so an unconfigured host still
/// starts. Hacker News access needs no key, and feeds are plain public URLs, so no
/// secret is required.
/// </summary>
public sealed class NewsOptions
{
    /// <summary>
    /// Whether the plugin is active. Defaults to true. Set to false to disable it
    /// without removing configuration.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Alias-to-URL map of RSS or Atom feeds the model may pull headlines from,
    /// e.g. "bbc" -> "https://feeds.bbci.co.uk/news/rss.xml". The alias is what the
    /// model names; the URL is never exposed to it. Empty means
    /// <c>get_top_headlines</c> replies with a corrective hint.
    /// </summary>
    public Dictionary<string, string> Feeds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
