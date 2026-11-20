using System.Globalization;
using System.Text.Json;
using Scout.Conversation.Abstractions;
using Scout.Llm.Abstractions;
using Scout.Plugin.GitHub.Abstractions;

namespace Scout.Plugin.GitHub;

/// <summary>A verdict GitHub took: where it is, and the tests line to post after it, when one came with it.</summary>
/// <param name="Owner">Repository owner or organization login.</param>
/// <param name="Repo">Repository name.</param>
/// <param name="Number">Pull request number.</param>
/// <param name="Url">The review's browser URL.</param>
/// <param name="TestsLine">The line on the tests for the pull request's conversation, or null.</param>
/// <param name="AlreadyGiven">True when the verdict was found on GitHub already, given before an answer was lost.</param>
/// <param name="Signer">The login that signed the verdict, when known.</param>
public sealed record GivenVerdict(string Owner, string Repo, int Number, string Url, string? TestsLine, bool AlreadyGiven = false, string? Signer = null);

/// <summary>
/// Runs a verdict the owner approved, from the payload queued with the action: the review card's shape
/// (<c>findings</c> as an array, a <c>testsLine</c>, and a review job's <c>filesRead</c>, <c>filesChanged</c> and
/// <c>notes</c>) or the arguments of <c>give_pull_request_verdict</c> (<c>findings_json</c> as text). A payload without
/// findings, as one queued before the note was composed by code, is unreadable and nothing is sent. The checks, the
/// note and the post are the function's: a refusal before anything is sent, or a
/// failure, is a <see cref="WriteFailedException"/>. The tests line is posted on its own, after the verdict was given and
/// before it is recorded.
/// </summary>
public sealed class GitHubVerdictRunner : IReviewNotePreview
{
    /// <summary>Why a verdict whose payload cannot be read is not given.</summary>
    public const string Unreadable = "The verdict's data could not be read; give the verdict again from the review card.";

    private readonly GitHubActionsPlugin _actions;
    private readonly IGitHubClient _reads;
    private readonly IGitHubWriteClient _writes;

    internal GitHubVerdictRunner(GitHubActionsPlugin actions, IGitHubClient reads, IGitHubWriteClient writes)
    {
        _actions = actions;
        _reads = reads;
        _writes = writes;
    }

    /// <summary>Give the verdict <paramref name="payload"/> describes.</summary>
    /// <param name="payload">The action's payload, as JSON.</param>
    /// <param name="ct">Cancels the requests.</param>
    /// <returns>Where the verdict is and the tests line that came with it.</returns>
    /// <exception cref="WriteFailedException">The verdict was refused, or GitHub or the network failed it.</exception>
    public async Task<GivenVerdict> GiveAsync(string? payload, CancellationToken ct)
    {
        var request = Read(payload) ?? throw new WriteFailedException(Unreadable, "refused");
        var (posted, _, already, signer) = await _actions.GiveVerdictAsync(
                request.Owner, request.Repo, request.Number, request.Verdict, request.Findings, request.Commit, request.Extras, ct)
            .ConfigureAwait(false);
        return new GivenVerdict(request.Owner, request.Repo, request.Number, posted.HtmlUrl, request.TestsLine, already, signer);
    }

    /// <summary>
    /// The note and comments the verdict <paramref name="requestJson"/> describes would post now, in the shape of a queued
    /// verdict, composed by the same path as <see cref="GiveAsync"/> with the same reads and nothing sent:
    /// <c>{"body", "comments":[{"id","path","line","body"}], "dropped":["path:line"]}</c>.
    /// </summary>
    /// <param name="requestJson">The verdict as the review card would queue it.</param>
    /// <param name="ct">Cancels the reads.</param>
    /// <returns>The preview as JSON.</returns>
    /// <exception cref="WriteFailedException">The verdict cannot be read or would be refused, or a read failed; its sentence says why.</exception>
    public async Task<string> PreviewAsync(string requestJson, CancellationToken ct)
    {
        var request = Read(requestJson) ?? throw new WriteFailedException(Unreadable, "refused");
        var (body, comments, dropped) = await _actions.PreviewAsync(
                request.Owner, request.Repo, request.Number, request.Verdict, request.Findings, request.Commit, request.Extras, ct)
            .ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            body,
            comments = comments.Select(c => new { id = c.Id, path = c.Comment.Path, line = c.Comment.Line, body = c.Comment.Body }),
            dropped,
        });
    }

    /// <summary>
    /// Post the tests line of <paramref name="verdict"/> on the pull request's conversation, when it has one, signed as the
    /// verdict was. A verdict found already given posts it only when no comment by its signer among the latest already
    /// carries it; a failure to read them posts nothing and throws, for the caller to log.
    /// </summary>
    /// <param name="verdict">The verdict GitHub took.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>
    /// True when the line is on the pull request's conversation, posted now or found there already; false when the verdict
    /// carried none.
    /// </returns>
    public async Task<bool> PostTestsLineAsync(GivenVerdict verdict, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        if (string.IsNullOrWhiteSpace(verdict.TestsLine))
        {
            return false;
        }

        if (verdict.AlreadyGiven)
        {
            var latest = await _reads.ListLatestConversationCommentsAsync(verdict.Owner, verdict.Repo, verdict.Number, ct).ConfigureAwait(false) ?? [];
            if (latest.Any(c => (verdict.Signer is null || string.Equals(c.AuthorLogin, verdict.Signer, StringComparison.OrdinalIgnoreCase))
                && c.Body.Contains(verdict.TestsLine, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        await _writes.CommentOnIssueAsync(verdict.Owner, verdict.Repo, verdict.Number, verdict.TestsLine, GitHubWriteKind.Verdict, ct).ConfigureAwait(false);
        return true;
    }

    private static Request? Read(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || Text(root, "owner") is not { } owner
                || Text(root, "repo") is not { } repo
                || Number(root) is not { } number)
            {
                return null;
            }

            var findings = root.TryGetProperty("findings", out var array) && array.ValueKind == JsonValueKind.Array
                ? array.GetRawText()
                : Text(root, "findings_json");
            if (findings is null)
            {
                return null;
            }

            var extras = NoteExtras.Read(Property(root, "filesRead"), Property(root, "filesChanged"), Property(root, "notes"));
            return new Request(owner, repo, number, Text(root, "verdict"), findings, Text(root, "commit"), Text(root, "testsLine"), extras);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? Property(JsonElement root, string name) => root.TryGetProperty(name, out var value) ? value : null;

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Number(JsonElement root)
    {
        if (!root.TryGetProperty("number", out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) => n,
            _ => null,
        };
    }

    private sealed record Request(
        string Owner, string Repo, int Number, string? Verdict, string Findings, string? Commit, string? TestsLine, NoteExtras? Extras);
}
