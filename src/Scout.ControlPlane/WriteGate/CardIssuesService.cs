using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Features;

namespace Scout.ControlPlane.WriteGate;

/// <summary>
/// The owner's click on a findings card's Open issues: the click is the owner's approval of exactly the issues the card
/// listed, so each one is queued already approved on the declared <see cref="Scope"/> row, as the call a chat request for
/// it would queue, in one new group that is closed at once, and the approval of each goes to the write log; the unchanged
/// executor sends them one by one, at most once each. Nothing is queued while the owner has not allowed GitHub writes. Each
/// issue is keyed on its own, by the card's id and the digest of its repository and its trimmed title and body, kept on its
/// row, which outlives the payload cut once the issue is sent: a later click on the same card answers an issue whose row is
/// waiting, approved, being sent, sent, or recorded or rejected as not confirmed, since that one may already be on GitHub,
/// and queues again only an issue whose rows all failed or were rejected otherwise, whatever else the set holds; an issue whose text changed is a new issue. Clicks run one at a time. A
/// log that cannot take one of the approvals rejects the actions the click queued, each rejection logged when the log
/// takes it, so none is sent without its entry.
/// </summary>
/// <remarks>Build the service over the feature state, the queue, the write log and a logger.</remarks>
public sealed partial class CardIssuesService(IFeatureStateStore states, IActionQueue actions, IWriteLog writes, ILogger<CardIssuesService> logger)
    : IDisposable
{
    /// <summary>The catalog row, and the action name, of an issue.</summary>
    public const string Scope = "New issues";

    /// <summary>The function a chat request for an issue calls, which the executor runs.</summary>
    public const string Target = "GitHubActions.create_issue";

    /// <summary>Why the issues of a click are rejected when an approval of one of them cannot be logged.</summary>
    public const string ApprovalNotLogged = "its approval could not be logged; open the issues again from the card";

    private const int MaxCardIdLength = 200;
    private const int MaxTitleLength = 256;
    private const int MaxBodyLength = 10_000;
    private const int MaxSummaryValueLength = 200;

    private const string GrantChanged =
        "GitHub's write permission changed after this issue was opened from the card, so it will not be sent; open it again.";

    private readonly IFeatureStateStore _states = states ?? throw new ArgumentNullException(nameof(states));
    private readonly IActionQueue _actions = actions ?? throw new ArgumentNullException(nameof(actions));
    private readonly IWriteLog _writes = writes ?? throw new ArgumentNullException(nameof(writes));
    private readonly ILogger<CardIssuesService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly SemaphoreSlim _click = new(1, 1);

    [GeneratedRegex(@"\A[A-Za-z0-9](?:[A-Za-z0-9._-]{0,99})\z")]
    private static partial Regex Name();

    /// <summary>
    /// Queue the card's <paramref name="issues"/>, each a title and a body, on <paramref name="repository"/> as approved by
    /// the owner, each title and body trimmed once and then checked, stored, keyed and sent as trimmed, or answer the row an
    /// earlier click on the same <paramref name="cardId"/> queued for that issue. The answer lists one row per issue, in the
    /// order given; an answered row whose approval is not in the write log is a mismatch, never answered as approved. A click
    /// that arrives while another runs waits for it.
    /// </summary>
    /// <exception cref="FeatureValidationFailedException">The card id, the repository, the number of issues, a title or a body is out of bounds.</exception>
    /// <exception cref="WriteNotGrantedException">The owner has not allowed GitHub writes.</exception>
    public async Task<ActionSetDecision> ApproveAsync(
        string cardId, string repository, IReadOnlyList<(string Title, string Body)> issues, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(issues);
        issues = [.. issues.Select(i => ((i.Title ?? string.Empty).Trim(), (i.Body ?? string.Empty).Trim()))];
        var errors = Validate(cardId, repository, issues);
        if (errors.Count > 0)
        {
            throw new FeatureValidationFailedException(errors);
        }

        await _click.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ApproveOneClickAsync(cardId, repository, issues, ct).ConfigureAwait(false);
        }
        finally
        {
            _click.Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _click.Dispose();

    private async Task<ActionSetDecision> ApproveOneClickAsync(
        string cardId, string repository, IReadOnlyList<(string Title, string Body)> issues, CancellationToken ct)
    {
        var state = await _states.GetAsync(FeatureCatalog.GitHubId, ct).ConfigureAwait(false);
        if (!state.Granted)
        {
            throw new WriteNotGrantedException(
                "The owner has not allowed github to write; allow it on the Features screen of Scout Desktop.");
        }

        var keyed = issues.Select(i => (Key: KeyOf(cardId, repository, i.Title, i.Body), i.Title, i.Body)).ToList();
        bool SameIssue(PendingAction a, string key) =>
            string.Equals(a.CardKey, key, StringComparison.Ordinal)
            || (a.CardKey?.StartsWith(cardId + ":", StringComparison.Ordinal) == true
                && string.Equals(a.Repository, repository, StringComparison.Ordinal)
                && a.FullText() is ({ } title, { } body)
                && string.Equals(KeyOf(cardId, repository, title.Trim(), body.Trim()), key, StringComparison.Ordinal));
        foreach (var swept in await _actions.RejectApprovedAsync(
                         FeatureCatalog.GitHubId,
                         (a, _) => a.RequestedAt < state.GrantedAt && keyed.Exists(k => SameIssue(a, k.Key)),
                         GrantChanged,
                         ct)
                     .ConfigureAwait(false))
        {
            await _writes.AppendAsync(WriteLogEntry.Rejected(swept, GrantChanged), ct).ConfigureAwait(false);
        }

        var (owner, repo) = (repository.Split('/')[0], repository.Split('/')[1]);
        var answered = await _actions.EnqueueApprovedOnceEachAsync(
                FeatureCatalog.GitHubId,
                repository,
                [.. keyed.Select(k => (k.Key, Scope, Target, Summary(owner, repo, k.Title), Payload(owner, repo, k.Title, k.Body)))],
                (a, key) => Answers(a) && SameIssue(a, key),
                ct)
            .ConfigureAwait(false);
        var added = answered.Where(a => a.Added).Select(a => a.Action).ToList();
        try
        {
            foreach (var action in added)
            {
                await _writes.AppendAsync(ActionDecisionService.ApprovalOf(action), ct).ConfigureAwait(false);
            }
        }
        catch
        {
            try
            {
                foreach (var rejected in await _actions.RejectApprovedAsync(
                                 FeatureCatalog.GitHubId, (a, _) => added.Exists(q => q.Id == a.Id), ApprovalNotLogged, CancellationToken.None)
                             .ConfigureAwait(false))
                {
                    await _writes.AppendAsync(WriteLogEntry.Rejected(rejected, ApprovalNotLogged), CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Scout could not log the approval of the issues a card opened, and rejecting them or logging that rejection failed too; check the Approvals screen.");
            }

            throw;
        }

        var rows = answered.Select(a => a.Action).ToList();
        var mismatches = await ActionDecisionService.ApprovalsNotInLogAsync(
                _writes, [.. rows.Where(a => !added.Exists(q => q.Id == a.Id))], ct)
            .ConfigureAwait(false);
        return new ActionSetDecision(mismatches.Count == 0, rows, mismatches)
        {
            GroupId = added.Count > 0 ? added[0].GroupId : rows[0].GroupId,
        };
    }

    private static bool Answers(PendingAction a) =>
        (a.Status != PendingActionStatus.Rejected || WriteGateService.IsNotConfirmed(a.Reason))
        && !(a.Status == PendingActionStatus.Executed
            && (a.Outcome?.StartsWith("failed", StringComparison.Ordinal) ?? false)
            && !string.Equals(a.Outcome, WriteGateService.NotConfirmedOutcome, StringComparison.Ordinal));

    private static Dictionary<string, string> Validate(string? cardId, string? repository, IReadOnlyList<(string Title, string Body)> issues)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(cardId) || cardId.Length > MaxCardIdLength)
        {
            errors["cardId"] = "A card is named by its id, of at most 200 characters.";
        }

        var parts = (repository ?? string.Empty).Split('/');
        if (parts.Length != 2 || !Name().IsMatch(parts[0]) || !Name().IsMatch(parts[1]))
        {
            errors["repository"] = "The repository must be owner/repo, each part starting with a letter or digit, then letters, digits, dots, hyphens or underscores.";
        }

        if (issues.Count == 0 || issues.Count > ActionDecisionService.MaxGroupSize)
        {
            errors["issues"] = "One click opens 1 to 50 issues.";
            return errors;
        }

        for (var i = 0; i < issues.Count; i++)
        {
            if (issues[i].Title.Length is 0 or > MaxTitleLength)
            {
                errors[$"issues[{i.ToString(CultureInfo.InvariantCulture)}].title"] = "The issue title must be 1 to 256 characters.";
            }

            if (issues[i].Body.Length is 0 or > MaxBodyLength)
            {
                errors[$"issues[{i.ToString(CultureInfo.InvariantCulture)}].body"] = "The issue body must be 1 to 10,000 characters.";
            }
        }

        return errors;
    }

    private static string Payload(string owner, string repo, string title, string body) =>
        JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["body"] = body,
            ["owner"] = owner,
            ["repo"] = repo,
            ["title"] = title,
        });

    private static string Summary(string owner, string repo, string title) =>
        $"create_issue(body, owner={owner}, repo={repo}, title={title[..Math.Min(title.Length, MaxSummaryValueLength)]})";

    private static string KeyOf(string cardId, string repository, string title, string body) =>
        $"{cardId}:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { repository, title, body }))))}";
}
