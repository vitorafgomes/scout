namespace Scout.Plugin.GitHub;

/// <summary>
/// Options for the GitHub feature plugin. Bound from the configuration
/// section named by <see cref="SectionName"/>.
/// </summary>
public sealed class GitHubPluginOptions
{
    /// <summary>Configuration section bound by the DI extension.</summary>
    public const string SectionName = "Scout:Plugins:GitHub";

    /// <summary>
    /// Product header sent to the GitHub API. Required by Octokit.
    /// </summary>
    public string UserAgent { get; set; } = "scout-plugin-github";

    /// <summary>
    /// Credential store key used to look up the PAT. Mirrors the catalog
    /// key convention <c>scout/&lt;featureId&gt;/&lt;fieldKey&gt;</c>.
    /// </summary>
    public string CredentialKey { get; set; } = "scout/github/pat";

    /// <summary>
    /// Default page size for list endpoints. GitHub's hard maximum is 100;
    /// 30 matches the GitHub default and balances latency vs round-trips.
    /// </summary>
    public int DefaultPageSize { get; set; } = 30;

    /// <summary>
    /// Remaining-rate-limit threshold below which the plugin emits a
    /// warning log line on every subsequent call.
    /// </summary>
    public int RateLimitWarningThreshold { get; set; } = 100;
}
