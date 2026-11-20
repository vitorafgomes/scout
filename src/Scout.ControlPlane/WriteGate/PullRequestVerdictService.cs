using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Features;

namespace Scout.ControlPlane.WriteGate;

/// <summary>One finding of a verdict, from which the worker composes the note and the comment on its line.</summary>
/// <param name="Id">The finding's id, unique in the verdict.</param>
/// <param name="Severity"><c>block</c>, <c>fix</c> or <c>nit</c>.</param>
/// <param name="Title">What the finding names.</param>
/// <param name="Path">Repository-relative path of the changed file.</param>
/// <param name="Line">Line number in the file after the change.</param>
/// <param name="Context">Why it matters, or null.</param>
/// <param name="Why">What is wrong.</param>
/// <param name="Fix">The proposed fix.</param>
/// <param name="Rule">The rule or document it departs from, or null.</param>
public sealed record VerdictFinding(string Id, string Severity, string Title, string Path, int Line, string? Context, string Why, string Fix, string? Rule);

/// <summary>A verdict the owner gave on a review card: what to send to GitHub once, pinned to the commit reviewed.</summary>
/// <param name="Owner">Repository owner or organization login.</param>
/// <param name="Repo">Repository name.</param>
/// <param name="Number">Pull request number.</param>
/// <param name="Verdict"><c>approve</c>, <c>request_changes</c>, or <c>comment</c> for the card's Post: a review that only comments.</param>
/// <param name="Findings">The findings the owner kept, with the text the owner edited.</param>
/// <param name="Commit">The head commit the review read; an approval or a request for changes is refused when the pull request moved past it.</param>
/// <param name="TestsLine">The line on the tests posted on the pull request's conversation after the verdict; null for none.</param>
/// <param name="FilesRead">The files a review job read, measured by code; null when not measured.</param>
/// <param name="FilesChanged">The files the pull request changes, as the review job counted them; null when not measured.</param>
/// <param name="Notes">The review job's sentences on what it did not read; null for none.</param>
/// <param name="Content">The digest of what the verdict sends, set when it is queued; two requests with the same digest send the same note.</param>
public sealed record PullRequestVerdictRequest(
    string Owner,
    string Repo,
    int Number,
    string Verdict,
    IReadOnlyList<VerdictFinding>? Findings,
    string Commit,
    string? TestsLine,
    int? FilesRead = null,
    int? FilesChanged = null,
    IReadOnlyList<string>? Notes = null,
    string? Content = null)
{
    /// <summary>
    /// The verdict as a review card sends it: the repository as <c>owner/repo</c> and the findings as the JSON array the
    /// card holds; findings that are not such an array are null, which validation refuses.
    /// </summary>
    public static PullRequestVerdictRequest FromCard(
        string? repository, int number, string? verdict, string? findingsJson, string? commit, string? testsLine,
        int? filesRead = null, int? filesChanged = null, IReadOnlyList<string>? notes = null)
    {
        var parts = (repository ?? string.Empty).Split('/');
        return new PullRequestVerdictRequest(
            parts.Length == 2 ? parts[0] : string.Empty,
            parts.Length == 2 ? parts[1] : string.Empty,
            number,
            verdict ?? string.Empty,
            ParseFindings(findingsJson),
            commit ?? string.Empty,
            string.IsNullOrWhiteSpace(testsLine) ? null : testsLine,
            filesRead,
            filesChanged,
            notes is { Count: > 0 } ? notes : null);
    }

    private static readonly JsonSerializerOptions FindingsJson = new(JsonSerializerDefaults.Web);

    private static List<VerdictFinding>? ParseFindings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<VerdictFinding>>(json, FindingsJson);
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
/// <param name="SignedAs">The login that signed the verdict once given, as the write log keeps it; empty otherwise or when unknown.</param>
/// <param name="TestsLine">
/// Once given, <c>posted</c> when its tests line is on the pull request's conversation, <c>notPosted</c> when posting it
/// failed, <c>none</c> when the verdict carried none; empty otherwise.
/// </param>
public sealed record VerdictState(
    string State, string Url, string Reason, string Repository, int Number, string Commit, string Verdict, string SignedAs = "", string TestsLine = "");

/// <summary>Thrown when a write the owner asked for in the desktop cannot be queued because the feature holds no grant.</summary>
/// <remarks>Build the exception with the sentence the desktop shows.</remarks>
public sealed class WriteNotGrantedException(string message) : Exception(message)
{
}

/// <summary>
/// The owner's click on a review card's Approve, Request changes or Post: the click is the owner's approval of that one
/// action, so the verdict, or for Post the review that only comments, is queued already approved, on its own catalog row,
/// with the findings and what code measured as the action's payload, and the approval goes to the write log. A different
/// verdict, or other content, at a commit that already has one is its own action, approved and logged on its own; an
/// identical request, the same commit, verdict and content digest, answers the action already there, queued, running
/// or given, unless it was rejected, failed, or asked before the owner's current GitHub grant, which no worker will run
/// and which reads failed; an approved verdict of that kind is rejected, and logged, when the next card verdict is given.
/// An action with no content digest, asked in chat or queued before the digest existed, is never identical. A chat
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

    private const string TestsLinePosted = "succeeded, tests line posted as a comment";

    private const string TestsLineNotPosted = "succeeded, tests line not posted";

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
        if (errors.Count == 0)
        {
            request = request with { Content = ContentOf(request) };
        }

        var payload = JsonSerializer.Serialize(request, Json);
        if (payload.Length > PendingAction.MaxPayloadLength)
        {
            errors["payload"] = $"The verdict is over {PendingAction.MaxPayloadLength.ToString(CultureInfo.InvariantCulture)} characters; keep fewer findings.";
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
        if (state != "given")
        {
            return new VerdictState(state, url, reason, repository, number, commit, verdict);
        }

        var outcome = action.Outcome ?? string.Empty;
        var testsLine = outcome.StartsWith(TestsLinePosted, StringComparison.Ordinal) ? "posted"
            : outcome.StartsWith(TestsLineNotPosted, StringComparison.Ordinal) ? "notPosted"
            : "none";
        var signedAs = (await _writes.ListSinceAsync(action.RequestedAt, ct).ConfigureAwait(false))
            .FirstOrDefault(e => WriteGateService.IsRecordOf(e, action))
            ?.SignedAs;
        return new VerdictState(state, url, reason, repository, number, commit, verdict, signedAs ?? string.Empty, testsLine);
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

        if (request.Findings is null || request.Findings.Count > MaxFindings)
        {
            errors["findings"] = "The findings must be a JSON array of at most 50 findings.";
        }
        else if (request.Findings.Any(f => f is null
                     || string.IsNullOrWhiteSpace(f.Id)
                     || f.Severity is not ("block" or "fix" or "nit")
                     || string.IsNullOrWhiteSpace(f.Title)
                     || string.IsNullOrWhiteSpace(f.Path)
                     || f.Line <= 0
                     || string.IsNullOrWhiteSpace(f.Why)
                     || string.IsNullOrWhiteSpace(f.Fix)))
        {
            errors["findings"] = "Every finding needs an id, a severity of block, fix or nit, a title, a path, a positive line, a why and a fix.";
        }
        else if (request.Findings.Select(f => f.Id).Distinct(StringComparer.Ordinal).Count() != request.Findings.Count)
        {
            errors["findings"] = DuplicateFindingIds;
        }

        if (request.FilesRead.HasValue != request.FilesChanged.HasValue || request.FilesRead < 0 || request.FilesRead > request.FilesChanged)
        {
            errors["files"] = "The files read and the files changed come together, the files read from 0 to the files changed.";
        }

        if (request.Notes is { } notes && (notes.Count > MaxNotes || notes.Any(n => string.IsNullOrWhiteSpace(n) || n.Length > MaxNoteLength)))
        {
            errors["notes"] = "At most 20 notes, each of 1 to 300 characters.";
        }

        return errors;
    }

    /// <summary>Why a verdict whose findings repeat an id is refused before it is queued.</summary>
    internal const string DuplicateFindingIds = "Each finding needs its own id; two findings share one.";

    private const int MaxFindings = 50;
    private const int MaxNotes = 20;
    private const int MaxNoteLength = 300;

    /// <summary>The SHA-256, in hexadecimal, of what the verdict sends: the verdict, the findings, the counts and the notes.</summary>
    private static string ContentOf(PullRequestVerdictRequest request) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { request.Verdict, request.Findings, request.FilesRead, request.FilesChanged, request.Notes }, Json)));

    /// <summary>
    /// An action the request repeats: not waiting for the owner, at the same commit, the same verdict and the same content
    /// digest. An action with no digest is never the same.
    /// </summary>
    private static bool Same(PendingAction action, PullRequestVerdictRequest request) =>
        action.Status != PendingActionStatus.Pending
        && SameKey(action, request)
        && string.Equals(Key(action.Payload).Verdict, request.Verdict, StringComparison.Ordinal)
        && ContentOf(action.Payload) is { Length: > 0 } content
        && string.Equals(content, request.Content, StringComparison.Ordinal);

    private static string ContentOf(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind == JsonValueKind.Object ? Text(document.RootElement, "content") : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

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
