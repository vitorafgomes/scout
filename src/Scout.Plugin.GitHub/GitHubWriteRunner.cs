using System.Globalization;
using System.Text.Json;
using Scout.Llm.Abstractions;
using Scout.Plugin.GitHub.Abstractions;

namespace Scout.Plugin.GitHub;

/// <summary>A write GitHub took: where it is, and the login that signed it, when known.</summary>
/// <param name="Url">The browser URL of the review, issue, comment or reply.</param>
/// <param name="Signer">The login that signed the write, or null when it could not be read.</param>
public sealed record GitHubWriteDone(string Url, string? Signer);

/// <summary>
/// Runs a new issue, a comment or a reply the model asked for and the owner approved, from the arguments queued with the
/// action: the action's target names the function (<c>GitHubActions.create_issue</c>, <c>GitHubActions.comment_on_pull_request</c>,
/// <c>GitHubActions.comment_on_issue</c> or <c>GitHubActions.reply_to_review_comment</c>). A commenting review runs from its
/// payload whichever way it was approved: the owner's click on a review card's Post, whose payload carries the comments as an
/// array, or a <c>submit_pull_request_review</c> call the owner approved, whose payload carries them as text. The checks and
/// the post are the function's own: a refusal before anything is sent, or a failure, is a <see cref="WriteFailedException"/>.
/// </summary>
public sealed class GitHubWriteRunner
{
    /// <summary>Why an action whose arguments cannot be read is not run.</summary>
    public const string Unreadable = "The action's data could not be read; ask for it again in chat.";

    private const string CreateIssueTarget = GitHubActionsPlugin.PluginName + "." + GitHubActionsPlugin.CreateIssue;
    private const string CommentOnPullRequestTarget = GitHubActionsPlugin.PluginName + "." + GitHubActionsPlugin.CommentOnPullRequest;
    private const string CommentOnIssueTarget = GitHubActionsPlugin.PluginName + "." + GitHubActionsPlugin.CommentOnIssue;
    private const string ReplyTarget = GitHubActionsPlugin.PluginName + "." + GitHubActionsPlugin.ReplyToReviewComment;

    private readonly GitHubActionsPlugin _actions;
    private readonly IGitHubClient _reads;
    private readonly IGitHubWriteClient _writes;

    internal GitHubWriteRunner(GitHubActionsPlugin actions, IGitHubClient reads, IGitHubWriteClient writes)
    {
        _actions = actions;
        _reads = reads;
        _writes = writes;
    }

    /// <summary>True when <paramref name="target"/> names a function this runner runs.</summary>
    public static bool Runs(string target) =>
        target is CreateIssueTarget or CommentOnPullRequestTarget or CommentOnIssueTarget or ReplyTarget;

    /// <summary>Run the write <paramref name="target"/> names with the arguments in <paramref name="payload"/>.</summary>
    /// <param name="target">The function the model called, as <c>plugin.function</c>.</param>
    /// <param name="payload">The function's arguments, as the JSON object queued with the action.</param>
    /// <param name="ct">Cancels the requests.</param>
    /// <returns>Where the write is and who signed it.</returns>
    /// <exception cref="WriteFailedException">The arguments could not be read, the write was refused, or GitHub or the network failed it.</exception>
    public async Task<GitHubWriteDone> RunAsync(string target, string? payload, CancellationToken ct)
    {
        var arguments = Read(payload) ?? throw new WriteFailedException(Unreadable, "refused");
        if (Text(arguments, "owner") is not { } owner || Text(arguments, "repo") is not { } repo)
        {
            throw new WriteFailedException(Unreadable, "refused");
        }

        string url;
        GitHubWriteKind kind;
        switch (target)
        {
            case CreateIssueTarget when Text(arguments, "title") is { } title && Text(arguments, "body") is { } body:
                url = (await _actions.OpenIssueAsync(owner, repo, title, body, Text(arguments, "labels"), ct).ConfigureAwait(false)).HtmlUrl;
                kind = GitHubWriteKind.Issue;
                break;
            case CommentOnPullRequestTarget or CommentOnIssueTarget when Number(arguments, "number") is { } number and <= int.MaxValue && Text(arguments, "body") is { } body:
                var what = target == CommentOnIssueTarget ? "issue" : "pull request";
                url = (await _actions.CommentAsync(owner, repo, (int)number, body, what, ct).ConfigureAwait(false)).HtmlUrl;
                kind = GitHubWriteKind.Comment;
                break;
            case ReplyTarget when Number(arguments, "number") is { } number and <= int.MaxValue && Number(arguments, "comment_id") is { } commentId && Text(arguments, "body") is { } body:
                url = (await _actions.ReplyAsync(owner, repo, (int)number, commentId, body, ct).ConfigureAwait(false)).HtmlUrl;
                kind = GitHubWriteKind.Comment;
                break;
            default:
                throw new WriteFailedException(Unreadable, "refused");
        }

        return new GitHubWriteDone(url, await SignerAsync(kind, owner, repo, ct).ConfigureAwait(false));
    }

    /// <summary>Post the commenting review <paramref name="payload"/> describes.</summary>
    /// <param name="payload">The review card's request or the function's arguments, as the JSON object queued with the action.</param>
    /// <param name="ct">Cancels the requests.</param>
    /// <returns>Where the review is and who signed it.</returns>
    /// <exception cref="WriteFailedException">The payload could not be read, the review was refused, or GitHub or the network failed it.</exception>
    public async Task<GitHubWriteDone> PostReviewAsync(string? payload, CancellationToken ct)
    {
        var arguments = Read(payload) ?? throw new WriteFailedException(Unreadable, "refused");
        if (Text(arguments, "owner") is not { } owner
            || Text(arguments, "repo") is not { } repo
            || Number(arguments, "number") is not { } number
            || number > int.MaxValue
            || !arguments.TryGetValue("comments", out var comments))
        {
            throw new WriteFailedException(Unreadable, "refused");
        }

        var text = comments.ValueKind == JsonValueKind.String ? comments.GetString() : comments.GetRawText();
        var (posted, _) = await _actions.PostReviewAsync(owner, repo, (int)number, Text(arguments, "body"), text, ct).ConfigureAwait(false);
        return new GitHubWriteDone(posted.HtmlUrl, await SignerAsync(GitHubWriteKind.Review, owner, repo, ct).ConfigureAwait(false));
    }

    private async Task<string?> SignerAsync(GitHubWriteKind kind, string owner, string repo, CancellationToken ct)
    {
        var signer = _writes.SignerFor(kind, owner, repo);
        if (signer.ByBot)
        {
            return signer.Login;
        }

        try
        {
            return await _reads.GetSignedInLoginAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when ((ex is GitHubApiException or HttpRequestException or OperationCanceledException or IOException) && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static Dictionary<string, JsonElement>? Read(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(Dictionary<string, JsonElement> arguments, string name) =>
        arguments.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Number(Dictionary<string, JsonElement> arguments, string name)
    {
        if (!arguments.TryGetValue(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) => n,
            _ => null,
        };
    }
}
