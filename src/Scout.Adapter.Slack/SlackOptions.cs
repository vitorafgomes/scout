namespace Scout.Adapter.Slack;

/// <summary>
/// Settings for the Slack chat adapter. Bound from "Scout:Adapters:Slack".
/// Inbound runs over Socket Mode, so no public URL is required: only a bot
/// token and an app-level token. Neither is ever logged.
/// </summary>
public sealed class SlackOptions
{
    /// <summary>
    /// Bot (xoxb-) token used for Web API calls (chat.postMessage, chat.update,
    /// auth.test). Must not be committed — inject via environment variable or
    /// User Secrets (e.g. SCOUT__ADAPTERS__SLACK__BOTTOKEN).
    /// </summary>
    public string BotToken { get; set; } = string.Empty;

    /// <summary>
    /// App-level (xapp-) token used to open the Socket Mode connection. Must not
    /// be committed — inject via environment variable or User Secrets
    /// (e.g. SCOUT__ADAPTERS__SLACK__APPLEVELTOKEN).
    /// </summary>
    public string AppLevelToken { get; set; } = string.Empty;
}
