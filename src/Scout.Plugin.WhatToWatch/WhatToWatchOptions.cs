namespace Scout.Plugin.WhatToWatch;

/// <summary>
/// Configuration for the WhatToWatch plugin, bound from
/// "Scout:Plugins:WhatToWatch". The plugin self-gates on <see cref="Enabled"/>:
/// when disabled the registration is a clean no-op (no clients, no kernel plugin),
/// so an unconfigured host still starts. The search, next-episode, and
/// recommendation functions run against TVMaze without any key; only
/// <c>where_to_watch</c> needs <see cref="TmdbApiKey"/>, and it degrades to a
/// friendly hint when the key is blank.
/// </summary>
public sealed class WhatToWatchOptions
{
    /// <summary>
    /// Whether the plugin is active. Defaults to true. Set to false to disable it
    /// without removing configuration.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Free TMDB API key (v3) used only by <c>where_to_watch</c> to resolve
    /// streaming availability. Empty (the default) disables that one function,
    /// which then returns a friendly hint; the other functions are unaffected.
    /// </summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>
    /// ISO 3166 country code used by <c>where_to_watch</c> when a call omits one,
    /// e.g. "GB" or "BR".
    /// </summary>
    public string DefaultCountry { get; set; } = "GB";

    /// <summary>
    /// IANA time zone used to resolve "today" when reporting how far away a next
    /// episode airs. Empty (the default) means the machine's local time zone.
    /// </summary>
    public string TimeZone { get; set; } = "";

    /// <summary>
    /// Maximum number of results returned by the search and recommendation
    /// functions. Defaults to 5.
    /// </summary>
    public int MaxResults { get; set; } = 5;
}
