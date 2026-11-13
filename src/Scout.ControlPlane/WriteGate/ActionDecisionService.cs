using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Features;

namespace Scout.ControlPlane.WriteGate;

/// <summary>Thrown when a set decision is refused whole: nothing is decided and nothing is logged.</summary>
/// <remarks>Build the exception with the sentence that says why.</remarks>
public sealed class ActionSetRefusedException(string message) : Exception(message)
{
}

/// <summary>A listed action a set decision did not match, and why.</summary>
public sealed record ActionSetMismatch(string Id, string Reason);

/// <summary>
/// What a set decision did: whether the decision was applied to every listed action, the listed actions as they stand
/// after, in the order they were listed, and the ones that did not match; a set with a mismatch decides none of them.
/// Decided says the decision was applied, never that a send succeeded: each action carries its own status, outcome and
/// result, so one that failed is visible on its row.
/// </summary>
public sealed record ActionSetDecision(bool Decided, IReadOnlyList<PendingAction> Actions, IReadOnlyList<ActionSetMismatch> Mismatches)
{
    /// <summary>
    /// For a findings card's click: the group the click queued its issues in, or, when it queued none, the group of the
    /// first row it answers. Null for a set decision, whose group the caller named.
    /// </summary>
    public string? GroupId { get; init; }
}

/// <summary>
/// The owner's decisions on queued actions, taken in the desktop, and the rejections a cleared grant causes; each one is
/// logged in the write log, so History shows it. An action is approved only while its feature holds the owner's grant and
/// was requested under that grant: without the grant the approval is refused and the action is left waiting; an action
/// requested before the current grant is rejected with <see cref="AskedBeforeGrant"/> and the approval refused. The queue is
/// written before the log, so a log that cannot be written fails the call with the decision already kept. A set decision
/// covers exactly the actions the desktop listed, by id and payload digest, in one group; see <see cref="ApproveSetAsync"/>.
/// </summary>
/// <remarks>Build the service over the feature state, the queue, the log and the clock a group's state is read on.</remarks>
public sealed class ActionDecisionService(IFeatureStateStore states, IActionQueue actions, IWriteLog writes, TimeProvider? time = null)
{
    /// <summary>Why an action requested before its feature's current grant is rejected when the owner approves it.</summary>
    public const string AskedBeforeGrant = "asked before writes were granted; ask again";

    /// <summary>A group whose request may still queue actions.</summary>
    public const string GroupOpen = "open";

    /// <summary>A group whose request ended, as the worker reported or as a quiet spell says.</summary>
    public const string GroupClosed = "closed";

    /// <summary>A group an action joined after it closed.</summary>
    public const string GroupChanged = "changed";

    /// <summary>A group with more than <see cref="MaxGroupSize"/> actions waiting.</summary>
    public const string GroupOverBound = "over_bound";

    /// <summary>The most actions waiting in a group that one decision may cover.</summary>
    public const int MaxGroupSize = 50;

    /// <summary>
    /// How long a group's request may go without queuing an action before the group counts as closed: it covers only a
    /// worker that stopped before it reported the request's end, so it is long enough for a slow model to pause between two
    /// queued calls.
    /// </summary>
    public static readonly TimeSpan GroupQuietClose = TimeSpan.FromMinutes(10);

    /// <summary>Why a listed action that is unknown, in another group, or on another feature or repository is not decided.</summary>
    public const string NotInTheGroup = "not in this group";

    /// <summary>Why a listed action whose payload no longer has the digest the desktop listed is not approved.</summary>
    public const string ChangedAfterListing = "its text changed after it was listed";

    /// <summary>Why a set that names no action, or one action twice, is refused.</summary>
    public const string SetNotAList = "A set names each action once, and at least one.";

    /// <summary>Why a set of more than <see cref="MaxGroupSize"/> actions is refused whole.</summary>
    public const string SetOverTheBound = "One decision covers at most 50 actions; decide these one by one.";

    /// <summary>Why Approve all is refused on a group whose request may still queue actions.</summary>
    public const string StillQueuing = "Scout is still queuing this group; approve all once it has finished, or decide each action now.";

    /// <summary>Why Approve all is refused on a group an action joined after it closed.</summary>
    public const string ChangedAfterClose = "This group changed after Scout finished queuing it; decide each action on its own, or reject all.";

    /// <summary>Why Approve all is refused on a group with more than <see cref="MaxGroupSize"/> actions waiting.</summary>
    public const string GroupTooLarge = "This group holds more than 50 waiting actions; decide each one on its own.";

    private readonly IFeatureStateStore _states = states ?? throw new ArgumentNullException(nameof(states));
    private readonly IActionQueue _actions = actions ?? throw new ArgumentNullException(nameof(actions));
    private readonly IWriteLog _writes = writes ?? throw new ArgumentNullException(nameof(writes));
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>
    /// The state at <paramref name="now"/> of the group whose actions, in any status, are <paramref name="group"/>. In order:
    /// <see cref="GroupOverBound"/> with more than <see cref="MaxGroupSize"/> actions waiting; with the worker's report, its
    /// first stamp, <see cref="GroupChanged"/> when an action was asked after it and <see cref="GroupClosed"/> otherwise,
    /// whatever quiet spells came before; without a report, a quiet spell longer than <see cref="GroupQuietClose"/> closes
    /// the group: <see cref="GroupChanged"/> when an action was asked after a spell between two of its actions,
    /// <see cref="GroupClosed"/> after a spell since its newest, and <see cref="GroupOpen"/> otherwise.
    /// </summary>
    public static string GroupStateOf(IReadOnlyCollection<PendingAction> group, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (group.Count(a => a.Status == PendingActionStatus.Pending) > MaxGroupSize)
        {
            return GroupOverBound;
        }

        var asked = group.Select(a => a.RequestedAt).Order().ToList();
        if (group.Min(a => a.GroupClosedAt) is { } reported)
        {
            return asked[^1] > reported ? GroupChanged : GroupClosed;
        }

        for (var i = 0; i + 1 < asked.Count; i++)
        {
            if (asked[i + 1] - asked[i] > GroupQuietClose)
            {
                return GroupChanged;
            }
        }

        return now - asked[^1] > GroupQuietClose ? GroupClosed : GroupOpen;
    }

    /// <summary>
    /// The actions <see cref="IActionQueue.ListPendingAsync"/> lists, each waiting one in a group with its group's state
    /// on this service's clock; the others, and one in no group, with none. The actions and their groups come from one read,
    /// and a state is read only for a group that holds a waiting action.
    /// </summary>
    public async Task<IReadOnlyList<(PendingAction Action, string? GroupState)>> ListPendingAsync(CancellationToken ct)
    {
        var read = await _actions.ListWhereAsync(a => Listed(a) || a.GroupId is not null, ct).ConfigureAwait(false);
        var waiting = read.Where(Grouped).Select(GroupOf).ToHashSet();
        var states = StatesOf(read.Where(a => a.GroupId is not null && waiting.Contains(GroupOf(a))));
        return read.Where(Listed).Select(a => (a, Grouped(a) ? states[GroupOf(a)] : null)).ToList();

        static bool Listed(PendingAction a) =>
            a.Status is PendingActionStatus.Pending or PendingActionStatus.Approved or PendingActionStatus.Executing;
        static bool Grouped(PendingAction a) => a.Status == PendingActionStatus.Pending && a.GroupId is not null;
    }

    /// <summary>
    /// Every action of the request <paramref name="groupId"/>, in any status, oldest first, each with the state on this
    /// service's clock of its group, the actions of that id on its feature and repository; none when the id holds none.
    /// </summary>
    public async Task<IReadOnlyList<(PendingAction Action, string GroupState)>> GetGroupAsync(string groupId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        var read = await _actions.ListWhereAsync(a => string.Equals(a.GroupId, groupId, StringComparison.Ordinal), ct).ConfigureAwait(false);
        var states = StatesOf(read);
        return read.Select(a => (a, states[GroupOf(a)])).ToList();
    }

    /// <summary>
    /// Approve, with the owner's optional <paramref name="reason"/>, exactly the actions the desktop listed, by id and the
    /// payload digest it listed, and log one approval per action with the group id. Under the queue's gate every listed
    /// action must be in the request <paramref name="groupId"/> on one feature and one repository, waiting, with that digest,
    /// and asked under the current grant, and the group must be closed; any action that does not match approves none and is
    /// answered with <see cref="ActionSetDecision.Mismatches"/>, while one asked before the grant is rejected and logged as
    /// <see cref="ApproveAsync"/> does. An action of the group not listed is never approved by it. A second call over actions
    /// already approved, executing or executed answers them as they stand and appends nothing; it needs no grant, since it
    /// approves nothing, but each listed action whose payload is still whole must carry the digest listed, and one that does
    /// not is answered with <see cref="ChangedAfterListing"/>; an executed action, whose payload was cut, is answered as it
    /// stands; a listed action whose approval the log never took, as after a log that failed part way, is answered as a
    /// mismatch, never as approved.
    /// </summary>
    /// <exception cref="ActionSetRefusedException">The set names no action, one twice or more than 50, or the group is still open, changed or over the bound.</exception>
    /// <exception cref="WriteNotGrantedException">The feature holds no grant.</exception>
    public Task<ActionSetDecision> ApproveSetAsync(
        string groupId, IReadOnlyList<(string Id, string Digest)> listed, string? reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(listed);
        return DecideSetAsync(groupId, listed.Select(l => (l.Id, (string?)l.Digest)).ToList(), approve: true, reason, ct);
    }

    /// <summary>
    /// Reject, with the owner's optional <paramref name="reason"/>, exactly the actions <paramref name="ids"/> of the request
    /// <paramref name="groupId"/>, and log each rejection. A rejection writes nothing outside Scout, so it needs no digest and
    /// no grant and is taken on a group still open, changed or over the bound; every listed action must be in the group on one
    /// feature and one repository and waiting, or none is rejected. A second identical call answers the same and appends nothing.
    /// </summary>
    /// <exception cref="ActionSetRefusedException">The set names no action, one twice or more than 50.</exception>
    public Task<ActionSetDecision> RejectSetAsync(string groupId, IReadOnlyList<string> ids, string? reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return DecideSetAsync(groupId, ids.Select(id => (id, (string?)null)).ToList(), approve: false, reason, ct);
    }

    private async Task<ActionSetDecision> DecideSetAsync(
        string groupId, IReadOnlyList<(string Id, string? Digest)> listed, bool approve, string? reason, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        if (listed.Count == 0 || listed.Select(l => l.Id).Distinct(StringComparer.Ordinal).Count() != listed.Count)
        {
            throw new ActionSetRefusedException(SetNotAList);
        }

        if (listed.Count > MaxGroupSize)
        {
            throw new ActionSetRefusedException(SetOverTheBound);
        }

        var mismatches = new List<ActionSetMismatch>();
        var repeat = false;
        IReadOnlyDictionary<string, (bool Approve, string? Reason)> decided = NoDecision;
        var rows = await _actions.DecideSetAsync(
                groupId,
                async group =>
                {
                    var byId = group.ToDictionary(a => a.Id, StringComparer.Ordinal);
                    var first = listed.Select(l => byId.GetValueOrDefault(l.Id)).FirstOrDefault(a => a is not null);
                    var shown = listed.Select(l => byId.GetValueOrDefault(l.Id)).ToList();
                    for (var i = 0; i < listed.Count; i++)
                    {
                        if (shown[i] is not { } row || first is null || GroupOf(row) != GroupOf(first))
                        {
                            mismatches.Add(new ActionSetMismatch(listed[i].Id, NotInTheGroup));
                        }
                    }

                    if (mismatches.Count > 0)
                    {
                        return NoDecision;
                    }

                    var actions = shown.Select(a => a!).ToList();
                    if (actions.All(a => approve
                            ? a.Status is PendingActionStatus.Approved or PendingActionStatus.Executing or PendingActionStatus.Executed
                            : a.Status == PendingActionStatus.Rejected))
                    {
                        repeat = true;
                        for (var i = 0; approve && i < actions.Count; i++)
                        {
                            if (actions[i].Status != PendingActionStatus.Executed
                                && !string.Equals(listed[i].Digest, actions[i].PayloadDigest(), StringComparison.Ordinal))
                            {
                                mismatches.Add(new ActionSetMismatch(actions[i].Id, ChangedAfterListing));
                            }
                        }

                        return NoDecision;
                    }

                    DateTimeOffset? grantedAt = null;
                    if (approve)
                    {
                        var state = await _states.GetAsync(first!.FeatureId, ct).ConfigureAwait(false);
                        if (!state.Granted)
                        {
                            throw new WriteNotGrantedException(
                                $"The owner has not allowed {first.FeatureId} to write; allow it on the Features screen of Scout Desktop, then ask again.");
                        }

                        grantedAt = state.GrantedAt;
                        var members = group.Where(a => GroupOf(a) == GroupOf(first)).ToList();
                        switch (GroupStateOf(members, _time.GetUtcNow()))
                        {
                            case GroupOpen:
                                throw new ActionSetRefusedException(StillQueuing);
                            case GroupChanged:
                                throw new ActionSetRefusedException(ChangedAfterClose);
                            case GroupOverBound:
                                throw new ActionSetRefusedException(GroupTooLarge);
                        }
                    }

                    var stale = new Dictionary<string, (bool, string?)>(StringComparer.Ordinal);
                    for (var i = 0; i < actions.Count; i++)
                    {
                        var action = actions[i];
                        if (action.Status != PendingActionStatus.Pending)
                        {
                            mismatches.Add(new ActionSetMismatch(action.Id, $"already {action.Status.ToString().ToLowerInvariant()}"));
                        }
                        else if (approve && !string.Equals(listed[i].Digest, action.PayloadDigest(), StringComparison.Ordinal))
                        {
                            mismatches.Add(new ActionSetMismatch(action.Id, ChangedAfterListing));
                        }
                        else if (grantedAt is { } at && action.RequestedAt < at)
                        {
                            mismatches.Add(new ActionSetMismatch(action.Id, AskedBeforeGrant));
                            stale[action.Id] = (false, AskedBeforeGrant);
                        }
                    }

                    decided = mismatches.Count > 0
                        ? stale
                        : actions.ToDictionary(a => a.Id, _ => (approve, reason), StringComparer.Ordinal);
                    return decided;
                },
                ct)
            .ConfigureAwait(false);

        var answered = listed.Select(l => rows.FirstOrDefault(a => string.Equals(a.Id, l.Id, StringComparison.Ordinal)))
            .OfType<PendingAction>()
            .ToList();
        if (repeat)
        {
            if (approve)
            {
                mismatches.AddRange(await ApprovalsNotInLogAsync(
                        _writes, [.. answered.Where(a => mismatches.TrueForAll(m => m.Id != a.Id))], ct)
                    .ConfigureAwait(false));
            }

            return new ActionSetDecision(mismatches.Count == 0, answered, mismatches);
        }

        foreach (var action in answered.Where(a => decided.ContainsKey(a.Id)))
        {
            await _writes.AppendAsync(
                    action.Status == PendingActionStatus.Approved ? ApprovalOf(action) : WriteLogEntry.Rejected(action, action.Reason),
                    ct)
                .ConfigureAwait(false);
        }

        return new ActionSetDecision(mismatches.Count == 0, answered, mismatches);
    }

    private static readonly IReadOnlyDictionary<string, (bool Approve, string? Reason)> NoDecision =
        new Dictionary<string, (bool, string?)>();

    private Dictionary<(string, string, string?), string> StatesOf(IEnumerable<PendingAction> grouped)
    {
        var now = _time.GetUtcNow();
        return grouped.GroupBy(GroupOf).ToDictionary(g => g.Key, g => GroupStateOf(g.ToList(), now));
    }

    private static (string, string, string?) GroupOf(PendingAction a) => (a.GroupId!, a.FeatureId, a.Repository);

    /// <summary>The write log's entry of the owner's approval of <paramref name="approved"/>, with its reason and group.</summary>
    internal static WriteLogEntry ApprovalOf(PendingAction approved) =>
        new(DateTimeOffset.UtcNow, approved.FeatureId, approved.Action, approved.Target, WriteGateService.Approval(approved.Reason), "owner", approved.Id)
        {
            GroupId = approved.GroupId,
        };

    /// <summary>
    /// Each of <paramref name="actions"/> whose approval is not in the write log, as a mismatch with
    /// <see cref="WriteGateService.ApprovalNotInLog"/>, in their order; the log is read back to the oldest one's request, and
    /// not at all when there is none.
    /// </summary>
    internal static async Task<IReadOnlyList<ActionSetMismatch>> ApprovalsNotInLogAsync(
        IWriteLog writes, IReadOnlyList<PendingAction> actions, CancellationToken ct)
    {
        if (actions.Count == 0)
        {
            return [];
        }

        var logged = await writes.ListSinceAsync(actions.Min(a => a.RequestedAt), ct).ConfigureAwait(false);
        return [.. actions
            .Where(a => !logged.Any(e => string.Equals(e.ActionId, a.Id, StringComparison.Ordinal) && WriteGateService.IsApproval(e.Outcome)))
            .Select(a => new ActionSetMismatch(a.Id, WriteGateService.ApprovalNotInLog))];
    }

    /// <summary>Approve the waiting action <paramref name="id"/> with the owner's optional <paramref name="reason"/>, and log it.</summary>
    /// <exception cref="PendingActionNotFoundException">The id is unknown.</exception>
    /// <exception cref="PendingActionAlreadyDecidedException">The action was already decided.</exception>
    /// <exception cref="WriteNotGrantedException">The feature holds no grant, or the action was requested before it.</exception>
    public async Task<PendingAction> ApproveAsync(string id, string? reason, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var action = await _actions.FindAsync(id, ct).ConfigureAwait(false) ?? throw new PendingActionNotFoundException(id);
        if (action.Status != PendingActionStatus.Pending)
        {
            throw new PendingActionAlreadyDecidedException(id, action.Status);
        }

        var state = await _states.GetAsync(action.FeatureId, ct).ConfigureAwait(false);
        if (!state.Granted)
        {
            throw new WriteNotGrantedException(
                $"The owner has not allowed {action.FeatureId} to write; allow it on the Features screen of Scout Desktop, then ask again.");
        }

        if (state.GrantedAt is { } grantedAt && action.RequestedAt < grantedAt)
        {
            var rejected = await _actions.RejectAsync(id, AskedBeforeGrant, ct).ConfigureAwait(false);
            await _writes.AppendAsync(WriteLogEntry.Rejected(rejected, AskedBeforeGrant), ct).ConfigureAwait(false);
            throw new WriteNotGrantedException(
                "This action was asked before writes were granted, so it was rejected; ask again.");
        }

        var approved = await _actions.ApproveAsync(id, reason, ct).ConfigureAwait(false);
        await _writes.AppendAsync(ApprovalOf(approved), ct).ConfigureAwait(false);
        return approved;
    }

    /// <summary>Reject the waiting action <paramref name="id"/> with the owner's optional <paramref name="reason"/>, and log it.</summary>
    /// <exception cref="PendingActionNotFoundException">The id is unknown.</exception>
    /// <exception cref="PendingActionAlreadyDecidedException">The action was already decided.</exception>
    public async Task<PendingAction> RejectAsync(string id, string? reason, CancellationToken ct)
    {
        var rejected = await _actions.RejectAsync(id, reason, ct).ConfigureAwait(false);
        await _writes.AppendAsync(WriteLogEntry.Rejected(rejected, rejected.Reason), ct).ConfigureAwait(false);
        return rejected;
    }

    /// <summary>
    /// Reject, with <see cref="WriteGateService.WritesRevoked"/>, every action of <paramref name="featureId"/> that has not
    /// started, and log each one; called by every call that clears the feature's grant.
    /// </summary>
    public async Task<IReadOnlyList<PendingAction>> RejectUnstartedAsync(string featureId, CancellationToken ct)
    {
        var rejected = await _actions.RejectUnstartedAsync(featureId, WriteGateService.WritesRevoked, ct).ConfigureAwait(false);
        foreach (var action in rejected)
        {
            await _writes.AppendAsync(WriteLogEntry.Rejected(action, WriteGateService.WritesRevoked), ct).ConfigureAwait(false);
        }

        return rejected;
    }
}
