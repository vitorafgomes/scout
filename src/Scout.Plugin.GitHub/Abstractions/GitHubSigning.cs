namespace Scout.Plugin.GitHub.Abstractions;

/// <summary>The kinds of write the owner may switch to the Scout bot, one switch each.</summary>
public enum GitHubWriteKind
{
    /// <summary>A review that comments.</summary>
    Review,

    /// <summary>An approval or a request for changes, and the tests line posted after it.</summary>
    Verdict,

    /// <summary>A comment on a pull request's or an issue's conversation, or a reply to a review comment.</summary>
    Comment,

    /// <summary>A new issue.</summary>
    Issue,
}

/// <summary>Who signs one write.</summary>
/// <param name="ByBot">True when the Scout bot signs it.</param>
/// <param name="Login">The bot's login when the bot signs and it is known; null when the owner signs, whose login is read from GitHub.</param>
/// <param name="BotNotInstalled">True when the owner switched this write to the bot but the bot is not installed on the repository, so the owner signs.</param>
public sealed record GitHubSigner(bool ByBot, string? Login, bool BotNotInstalled);
