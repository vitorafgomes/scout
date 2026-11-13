using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Features;

namespace Scout.ControlPlane.WriteGate;

/// <summary>One comment of a verdict, anchored to a line of the pull request's diff.</summary>
/// <param name="Path">Repository-relative path of the changed file.</param>
/// <param name="Line">Line number in the file after the change.</param>
/// <param name="Body">The comment text.</param>
public sealed record VerdictComment(string Path, int Line, string Body);

/// <summary>A verdict the owner gave on a review card: what to send to GitHub once, pinned to the commit reviewed.</summary>
/// <param name="Owner">Repository owner or organization login.</param>
/// <param name="Repo">Repository name.</param>
/// <param name="Number">Pull request number.</param>
/// <param name="Verdict"><c>approve</c>, <c>request_changes</c>, or <c>comment</c> for the card's Post: a review that only comments.</param>
/// <param name="Body">The review's summary.</param>
/// <param name="Comments">The comments the owner kept.</param>
/// <param name="Commit">The head commit the review read; an approval or a request for changes is refused when the pull request moved past it.</param>
/// <param name="TestsLine">The line on the tests posted on the pull request's conversation after the verdict; null for none.</param>
public sealed record PullRequestVerdictRequest(
    string Owner,
    string Repo,
    int Number,
    string Verdict,
    string Body,
    IReadOnlyList<VerdictComment>? Comments,
    string Commit,
    string? TestsLine)
{
    /// <summary>
    /// The verdict as a review card sends it: the repository as <c>owner/repo</c> and the comments as a JSON array of
    /// <c>{"path", "line", "body"}</c>; comments that are not such an array are null, which validation refuses.
    /// </summary>
    public static PullRequestVerdictRequest FromCard(
        string? repository, int number, string? verdict, string? body, string? commentsJson, string? commit, string? testsLine)
    {
        var parts = (repository ?? string.Empty).Split('/');
        return new PullRequestVerdictRequest(
            parts.Length == 2 ? parts[0] : string.Empty,
            parts.Length == 2 ? parts[1] : string.Empty,
            number,
            verdict ?? string.Empty,
            body ?? string.Empty,
            ParseComments(commentsJson),
            commit ?? string.Empty,
            string.IsNullOrWhiteSpace(testsLine) ? null : testsLine);
    }

    private static readonly JsonSerializerOptions CommentsJson = new(JsonSerializerDefaults.Web);

    private static List<VerdictComment>? ParseComments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<VerdictComment>>(json, CommentsJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Where a verdict stands, as the review card shows it, with what it is on, so any window can key it.</summary>
/// <param name="State"><c>queued</c>, <c>running</c>, <c>given</c> or <c>failed</c>.</param>
/// <param name="Url">The review's link once given; empty otherwise.</param>
/// <param name="Reason">Why it failed; empty otherwise.</param>
/// <param name="Repository">The repository as <c>owner/repo</c>; empty when the payload does not say.</param>
/// <param name="Number">The pull request number; 0 when the payload does not say.</param>
/// <param name="Commit">The commit reviewed; empty when the payload does not say.</param>
/// <param name="Verdict"><c>approve</c>, <c>request_changes</c> or <c>comment</c>; empty when the payload does not say.</param>
public sealed record VerdictState(string State, string Url, string Reason, string Repository, int Number, string Commit, string Verdict);

/// <summary>Thrown when a write the owner asked for in the desktop cannot be queued because the feature holds no grant.</summary>
/// <remarks>Build the exception with the sentence the desktop shows.</remarks>
public sealed class WriteNotGrantedException(string message) : Exception(message)
{
}

/// <summary>
/// The owner's click on a review card's Approve, Request changes or Post: the click is the owner's approval of that one
/// action, so the verdict, or for Post the review that only comments, is queued already approved, on its own catalog row,
/// with what the worker needs to send it as the action's payload, and the approval goes to the write log. One commit of
/// one pull request gets one such action: asking again, with any of the three,
/// answers the action already there, queued, running or given, from a card or approved from chat, unless it was rejected,
/// failed, or asked before the owner's current GitHub grant, which no worker will run and which reads failed; an approved
/// verdict of that kind is rejected, and logged, when the next card verdict is given. A chat
/// verdict on the same commit still waiting for the owner is rejected, replaced by the card's, so what runs is exactly
/// what the card showed. Nothing is queued while the owner has not allowed GitHub writes.
/// </summary>
/// <remarks>Build the service over the feature state, the queue and the write log.</remarks>
public sealed partial class PullRequestVerdictService(IFeatureStateStore states, IActionQueue actions, IWriteLog writes)
{
    /// <summary>The catalog row, and the action name, of a verdict.</summary>
    public const string Scope = "Pull request verdicts";

    /// <summary>The catalog row, and the action name, of a review that only comments.</summary>
    public const string ReviewScope = "Pull request reviews";

    /// <summary>The verdict value of a card's Post.</summary>
    public const string Comment = "comment";

    private const string Rejected = "The owner rejected this verdict.";
    private const string ReplacedByTheCard = "Replaced by the owner's verdict from the review card.";

    private const string GrantChanged =
        "GitHub's write permission changed after this verdict was given, so it will not be sent; give it again.";

    private const string NotRecorded = "The verdict ended without a result; check the pull request.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IFeatureStateStore _states = states ?? throw new ArgumentNullException(nameof(states));
    private readonly IActionQueue _actions = actions ?? throw new ArgumentNullException(nameof(actions));
    private readonly IWriteLog _writes = writes ?? throw new ArgumentNullException(nameof(writes));

    [GeneratedRegex(@"\A[A-Za-z0-9._-]{1,100}\z")]
    private static partial Regex Name();

    [GeneratedRegex(@"\A[0-9a-fA-F]{7,40}\z")]
    private static partial Regex Sha();

    /// <summary>Queue the verdict as approved by the owner, or answer the one already queued for it.</summary>
    /// <exception cref="FeatureValidationFailedException">A value is missing or malformed, or the payload is too large.</exception>
    /// <exception cref="WriteNotGrantedException">The owner has not allowed GitHub writes.</exception>
    public async Task<PendingAction> GiveAsync(PullRequestVerdictRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = Validate(request);
        var payload = JsonSerializer.Serialize(request, Json);
        if (payload.Length > PendingAction.MaxPayloadLength)
        {
            errors["payload"] = $"The verdict is over {PendingAction.MaxPayloadLength.ToString(CultureInfo.InvariantCulture)} characters; keep fewer comments.";
        }

        if (errors.Count > 0)
        {
            throw new FeatureValidationFailedException(errors);
        }

        var state = await _states.GetAsync(FeatureCatalog.GitHubId, ct).ConfigureAwait(false);
        if (!state.Granted)
        {
            throw new WriteNotGrantedException(
                "The owner has not allowed github to write; allow it on the Features screen of Scout Desktop.");
        }

        foreach (var swept in await _actions.RejectApprovedAsync(
                         FeatureCatalog.GitHubId, (a, _) => FromACard(a) && Stale(a, state.GrantedAt), GrantChanged, ct)
                     .ConfigureAwait(false))
        {
            await _writes.AppendAsync(WriteLogEntry.Rejected(swept, GrantChanged), ct).ConfigureAwait(false);
        }

        foreach (var waiting in (await _actions.ListAllAsync(FeatureCatalog.GitHubId, ct).ConfigureAwait(false))
                     .Where(a => a.Status == PendingActionStatus.Pending && SameKey(a, request)))
        {
            try
            {
                var rejected = await _actions.RejectAsync(waiting.Id, ReplacedByTheCard, ct).ConfigureAwait(false);
                await _writes.AppendAsync(WriteLogEntry.Rejected(rejected, ReplacedByTheCard), ct).ConfigureAwait(false);
            }
            catch (PendingActionAlreadyDecidedException)
            {
            }
        }

        var target = $"{request.Owner}/{request.Repo}#{request.Number.ToString(CultureInfo.InvariantCulture)}";
        var scope = request.Verdict == Comment ? ReviewScope : Scope;
        var summary = request.Verdict switch
        {
            "approve" => "Approve ",
            Comment => "Post a review on ",
            _ => "Request changes on ",
        } + target;
        var (action, added) = await _actions.EnqueueApprovedOnceAsync(
                FeatureCatalog.GitHubId, scope, target, summary, payload, a => Same(a, request) && !Stale(a, state.GrantedAt), ct)
            .ConfigureAwait(false);
        if (added)
        {
            await _writes.AppendAsync(
                    new WriteLogEntry(DateTimeOffset.UtcNow, FeatureCatalog.GitHubId, scope, target, WriteGateService.ApprovedOutcome, "owner", action.Id),
                    ct)
                .ConfigureAwait(false);
        }

        return action;
    }

    /// <summary>Where the verdict queued as <paramref name="actionId"/> stands; null when no verdict has that id.</summary>
    public async Task<VerdictState?> ReadAsync(string actionId, CancellationToken ct)
    {
        var action = string.IsNullOrWhiteSpace(actionId) ? null : await _actions.FindAsync(actionId, ct).ConfigureAwait(false);
        return action is null || action.FeatureId != FeatureCatalog.GitHubId || !FromACard(action)
            ? null
            : await StateOfAsync(action, ct).ConfigureAwait(false);
    }

    private async Task<VerdictState> StateOfAsync(PendingAction action, CancellationToken ct)
    {

        var (repository, number, commit, verdict) = Key(action.Payload);
        var grantedAt = (await _states.GetAsync(FeatureCatalog.GitHubId, ct).ConfigureAwait(false)).GrantedAt;
        var (state, url, reason) = action.Status switch
        {
            _ when Stale(action, grantedAt) => ("failed", string.Empty, GrantChanged),
            PendingActionStatus.Pending or PendingActionStatus.Approved => ("queued", string.Empty, string.Empty),
            PendingActionStatus.Executing => ("running", string.Empty, string.Empty),
            PendingActionStatus.Rejected => ("failed", string.Empty, action.Reason ?? Rejected),
            _ when action.Outcome?.StartsWith("succeeded", StringComparison.Ordinal) == true => ("given", action.Result ?? string.Empty, string.Empty),
            _ => ("failed", string.Empty, action.Result ?? NotRecorded),
        };
        return new VerdictState(state, url, reason, repository, number, commit, verdict);
    }

    /// <summary>
    /// The latest verdict on <paramref name="commit"/> of pull request <paramref name="number"/> of
    /// <paramref name="repository"/>, given from a card or asked in chat, with its id; null when there is none.
    /// </summary>
    public async Task<(string ActionId, VerdictState State)?> FindAsync(string repository, int number, string commit, CancellationToken ct)
    {
        var actions = await _actions.ListAllAsync(FeatureCatalog.GitHubId, ct).ConfigureAwait(false);
        for (var i = actions.Count - 1; i >= 0; i--)
        {
            var action = actions[i];
            if (!FromACard(action))
            {
                continue;
            }

            var (keyRepository, keyNumber, keyCommit, _) = Key(action.Payload);
            if (string.Equals(keyRepository, repository, StringComparison.OrdinalIgnoreCase)
                && keyNumber == number
                && keyCommit.Length > 0
                && string.Equals(keyCommit, commit, StringComparison.OrdinalIgnoreCase))
            {
                return (action.Id, await StateOfAsync(action, ct).ConfigureAwait(false));
            }
        }

        return null;
    }

    private static (string Repository, int Number, string Commit, string Verdict) Key(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return (string.Empty, 0, string.Empty, string.Empty);
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (string.Empty, 0, string.Empty, string.Empty);
            }

            var owner = Text(root, "owner");
            var repo = Text(root, "repo");
            var number = !root.TryGetProperty("number", out var n) ? 0
                : n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var value) ? value
                : n.ValueKind == JsonValueKind.String && int.TryParse(n.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var text) ? text
                : 0;
            return (owner.Length > 0 && repo.Length > 0 ? $"{owner}/{repo}" : string.Empty, number, Text(root, "commit"), Text(root, "verdict"));
        }
        catch (JsonException)
        {
            return (string.Empty, 0, string.Empty, string.Empty);
        }
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static Dictionary<string, string> Validate(PullRequestVerdictRequest request)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Name().IsMatch(request.Owner ?? string.Empty) || !Name().IsMatch(request.Repo ?? string.Empty))
        {
            errors["repository"] = "The repository must be owner/repo, each part letters, digits, dots, hyphens or underscores.";
        }

        if (request.Number <= 0)
        {
            errors["number"] = "The pull request number must be positive.";
        }

        if (request.Verdict is not ("approve" or "request_changes" or Comment))
        {
            errors["verdict"] = "The verdict must be approve, request_changes or comment.";
        }

        if (!Sha().IsMatch(request.Commit ?? string.Empty))
        {
            errors["commit"] = "The commit reviewed must be a commit SHA.";
        }

        if (request.Comments is null || request.Comments.Any(c => c is null || string.IsNullOrWhiteSpace(c.Path) || c.Line <= 0 || string.IsNullOrWhiteSpace(c.Body)))
        {
            errors["comments"] = "Every comment needs a path, a positive line and a body.";
        }

        return errors;
    }

    private static bool Same(PendingAction action, PullRequestVerdictRequest request) =>
        action.Status != PendingActionStatus.Pending && SameKey(action, request);

    /// <summary>
    /// A verdict, or a review that carries the commit it read as a card's Post does; a review the model asked for in chat
    /// carries only the function's arguments, with no commit, and is not one of these.
    /// </summary>
    private static bool FromACard(PendingAction action) =>
        action.Action == Scope || (action.Action == ReviewScope && Key(action.Payload).Commit.Length > 0);

    private static bool SameKey(PendingAction action, PullRequestVerdictRequest request)
    {
        if (!FromACard(action))
        {
            return false;
        }

        var (repository, number, commit, _) = Key(action.Payload);
        return string.Equals(repository, $"{request.Owner}/{request.Repo}", StringComparison.OrdinalIgnoreCase)
            && number == request.Number
            && commit.Length > 0
            && string.Equals(commit, request.Commit, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An action still open that no worker will run: it was asked before the owner's current GitHub grant, or there is no
    /// grant now. The worker claims only actions asked under the grant it runs under.
    /// </summary>
    private static bool Stale(PendingAction action, DateTimeOffset? grantedAt) =>
        action.Status is PendingActionStatus.Pending or PendingActionStatus.Approved or PendingActionStatus.Executing
        && (grantedAt is null || action.RequestedAt < grantedAt);
}
