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

    /// <summary>
    /// Folder where the conventions profile of each repository Scout audited is kept; empty for
    /// <c>Scout/github-profiles</c> under the user's local application data.
    /// </summary>
    public string ProfilesDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Folder where a repository snapshot taken for a review or an audit is extracted; empty for
    /// <c>Scout/github-snapshots</c> under the user's local application data.
    /// </summary>
    public string SnapshotsDirectory { get; set; } = string.Empty;

    /// <summary>The most a snapshot may hold once extracted, in bytes; 0 for the default of 200 MB.</summary>
    public long SnapshotMaxBytes { get; set; }

    /// <summary>
    /// Path of the JSON file holding the Scout bot's installation token as "access_token", written by the desktop when the
    /// owner created the optional bot; empty when there is no bot, and then every write is signed as the owner.
    /// </summary>
    public string BotTokenFile { get; set; } = string.Empty;

    /// <summary>Comma-separated writes the owner switched to the bot: any of <c>review</c>, <c>verdict</c>, <c>comment</c>, <c>issue</c>.</summary>
    public string BotWrites { get; set; } = string.Empty;

    /// <summary>Comma-separated <c>owner/repo</c> the bot's installation covers; a write on any other repository is signed as the owner.</summary>
    public string BotRepos { get; set; } = string.Empty;

    /// <summary>The bot's login, such as <c>scout-octocat[bot]</c>, so Scout knows a pull request the bot opened.</summary>
    public string BotLogin { get; set; } = string.Empty;
}
