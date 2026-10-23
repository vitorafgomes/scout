namespace Scout.Plugin.GitHub;

/// <summary>
/// Configuration for the GitHub plugin, bound from "Scout:Plugins:GitHub".
/// The plugin self-gates: with neither <see cref="Token"/> nor <see cref="TokenFile"/>
/// set the feature stays inactive (no client, no kernel plugin registered).
/// </summary>
public sealed class GitHubOptions
{
    /// <summary>
    /// Personal Access Token used as a Bearer credential. Read-only permissions are
    /// sufficient. Ignored when <see cref="TokenFile"/> is set.
    /// </summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Path of the JSON file holding the GitHub App user access token as "access_token",
    /// written by the desktop after "Connect with GitHub". Read before every request, so a
    /// refreshed token is picked up without a restart.
    /// </summary>
    public string TokenFile { get; set; } = string.Empty;
}
