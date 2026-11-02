namespace Scout.Plugin.YouTube;

/// <summary>
/// Configuration for the YouTube plugin, bound from "Scout:Plugins:YouTube".
/// The plugin self-gates: with neither <see cref="ApiKey"/> nor <see cref="TokenFile"/>
/// set the feature stays inactive (no client, no kernel plugin registered).
/// </summary>
public sealed class YouTubeOptions
{
    /// <summary>
    /// YouTube Data API key; reads public data only. Ignored when <see cref="TokenFile"/> is set.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Path of the JSON file holding the Google access token as "access_token", written by the
    /// desktop after the owner signs in. Read before every request, so a refreshed token is
    /// picked up without a restart.
    /// </summary>
    public string TokenFile { get; set; } = string.Empty;
}
