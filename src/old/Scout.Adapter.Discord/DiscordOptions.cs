namespace Scout.Adapter.Discord;

/// <summary>
/// Settings for the Discord chat adapter. Bound from "Scout:Adapters:Discord".
/// </summary>
public sealed class DiscordOptions
{
    /// <summary>
    /// Discord bot token. Must not be committed — inject via environment
    /// variable or User Secrets (e.g. SCOUT__ADAPTERS__DISCORD__BOTTOKEN).
    /// </summary>
    public string BotToken { get; set; } = string.Empty;
}
