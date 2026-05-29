namespace Scout.Plugin.GitHub;

/// <summary>
/// Configuration for the GitHub plugin, bound from "Scout:Plugins:GitHub".
/// The plugin self-gates: an empty <see cref="Token"/> leaves the feature
/// inactive (no client, no kernel plugin registered).
/// </summary>
public sealed class GitHubOptions
{
    /// <summary>
    /// Personal Access Token used as a Bearer credential. Read-only scopes are
    /// sufficient. Empty disables the plugin.
    /// </summary>
    public string Token { get; set; } = string.Empty;
}
