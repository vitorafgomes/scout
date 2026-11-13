using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Features;
using Scout.ControlPlane.WriteGate;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.WriteGate;

/// <summary>
/// The owner's one decision on a set of actions one request queued, over Scout's own queue, log and feature state in a temp
/// folder: Approve set and Reject set beside the single Approve and Reject, crossed with the states a group can be in, and
/// the set fixed by the ids and digests the desktop listed.
/// </summary>
public sealed class ActionSetDecisionTests : IDisposable
{
    private const string Group = "turn-1";
    private const string Shop = "acme/shop";
    private const string AskedBefore = "rejected: asked before writes were granted; ask again";

    private readonly string _directory = Directory.CreateTempSubdirectory("scout-set-decisions-").FullName;
    private readonly JsonFileFeatureStateStore _states;
    private readonly JsonFileActionQueue _queue;
    private readonly JsonLinesWriteLog _log;
    private readonly ActionDecisionService _decisions;

    public ActionSetDecisionTests()
    {
        var options = Options.Create(new FeatureStateStoreOptions { FilePath = Path.Combine(_directory, "features.json") });
        _states = new JsonFileFeatureStateStore(options, NullLogger<JsonFileFeatureStateStore>.Instance);
        _queue = new JsonFileActionQueue(options, NullLogger<JsonFileActionQueue>.Instance);
        _log = new JsonLinesWriteLog(options, NullLogger<JsonLinesWriteLog>.Instance);
        _decisions = new ActionDecisionService(_states, _queue, _log);
    }

    public void Dispose()
    {
        _states.Dispose();
        _queue.Dispose();
        _log.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    /// <summary>
    /// One cell: the group's state, the decision, the answer, the rows after it, one letter per action in the order they were
    /// queued (P waiting, A approved, R rejected), and the write log's entries, oldest first.
    /// </summary>
    public static TheoryData<string, string, string, string, string[]> Cells()
    {
        var cells = new TheoryData<string, string, string, string, string[]>();
        string[] Each(string outcome, params int[] indexes) => indexes.Select(i => $"{outcome} a{i} {Group}").ToArray();
        var fifty = Enumerable.Range(0, 50).ToArray();

        cells.Add("closed", "approve set", "decided", "AAA", Each("approved", 0, 1, 2));
        cells.Add("closed", "reject set", "decided", "RRR", Each("rejected", 0, 1, 2));
        cells.Add("closed", "approve", "approved", "PPA", Each("approved", 2));
        cells.Add("closed", "reject", "rejected", "PPR", Each("rejected", 2));

        cells.Add("partly decided", "approve set", "decided", "AAA", Each("approved", 0, 1, 2));
        cells.Add("partly decided", "reject set", "decided", "ARR", [.. Each("approved", 0), .. Each("rejected", 1, 2)]);
        cells.Add("partly decided", "approve", "approved", "APA", Each("approved", 0, 2));
        cells.Add("partly decided", "reject", "rejected", "APR", [.. Each("approved", 0), .. Each("rejected", 2)]);

        cells.Add("partly decided, listed before", "approve set", "mismatch a0 already approved", "APP", Each("approved", 0));
        cells.Add("partly decided, listed before", "reject set", "mismatch a0 already approved", "APP", Each("approved", 0));
        cells.Add("partly decided, listed before", "approve", "approved", "APA", Each("approved", 0, 2));
        cells.Add("partly decided, listed before", "reject", "rejected", "APR", [.. Each("approved", 0), .. Each("rejected", 2)]);

        cells.Add("open", "approve set", ActionDecisionService.StillQueuing, "PPP", []);
        cells.Add("open", "reject set", "decided", "RRR", Each("rejected", 0, 1, 2));
        cells.Add("open", "approve", "approved", "PPA", Each("approved", 2));
        cells.Add("open", "reject", "rejected", "PPR", Each("rejected", 2));

        cells.Add("changed", "approve set", ActionDecisionService.ChangedAfterClose, "PPPP", []);
        cells.Add("changed", "reject set", "decided", "RRRP", Each("rejected", 0, 1, 2));
        cells.Add("changed", "approve", "approved", "PPAP", Each("approved", 2));
        cells.Add("changed", "reject", "rejected", "PPRP", Each("rejected", 2));

        cells.Add("over the bound", "approve set", ActionDecisionService.GroupTooLarge, new string('P', 51), []);
        cells.Add("over the bound", "reject set", "decided", new string('R', 50) + "P", Each("rejected", fifty));
        cells.Add("over the bound", "approve", "approved", new string('P', 50) + "A", Each("approved", 50));
        cells.Add("over the bound", "reject", "rejected", new string('P', 50) + "R", Each("rejected", 50));

        cells.Add("without the grant", "approve set", "not granted", "PPP", []);
        cells.Add("without the grant", "reject set", "decided", "RRR", Each("rejected", 0, 1, 2));
        cells.Add("without the grant", "approve", "not granted", "PPP", []);
        cells.Add("without the grant", "reject", "rejected", "PPR", Each("rejected", 2));

        cells.Add("one asked before the grant", "approve set", $"mismatch a0 {ActionDecisionService.AskedBeforeGrant}", "RPP", [$"{AskedBefore} a0 {Group}"]);
        cells.Add("one asked before the grant", "reject set", "decided", "RRR", Each("rejected", 0, 1, 2));
        cells.Add("one asked before the grant", "approve", "not granted", "RPP", [$"{AskedBefore} a0 {Group}"]);
        cells.Add("one asked before the grant", "reject", "rejected", "RPP", Each("rejected", 0));
        return cells;
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public async Task A_Set_Or_Single_Decision_Leaves_The_Rows_And_Entries_Of_Its_Cell(
        string state, string decision, string answer, string rows, string[] entries)
    {
        var (actions, listed, single) = await ArrangeAsync(state);

        var answered = decision switch
        {
            "approve set" => await AnswerAsync(() => _decisions.ApproveSetAsync(Group, [.. listed.Select(a => (a.Id, a.PayloadDigest()))], null, CancellationToken.None), actions),
            "reject set" => await AnswerAsync(() => _decisions.RejectSetAsync(Group, [.. listed.Select(a => a.Id)], null, CancellationToken.None), actions),
            "approve" => await AnswerAsync(async () => (await _decisions.ApproveAsync(single.Id, null, CancellationToken.None)).Status.ToString().ToLowerInvariant()),
            _ => await AnswerAsync(async () => (await _decisions.RejectAsync(single.Id, null, CancellationToken.None)).Status.ToString().ToLowerInvariant()),
        };

        answered.ShouldBe(answer, $"{state} x {decision}");
        (await RowsAsync(actions)).ShouldBe(rows, $"{state} x {decision}");
        (await EntriesAsync(actions)).ShouldBe(entries, $"{state} x {decision}");
    }

    [Fact]
    public async Task A_Payload_Changed_After_The_Listing_Approves_None_And_Answers_That_Id()
    {
        await GrantAsync();
        var actions = await QueueAsync(3);
        await _queue.CloseGroupAsync(Group, CancellationToken.None);
        var listing = actions.Select(a => (a.Id, a.PayloadDigest())).ToList();
        await RewriteBodyAsync(actions[1].Id, "Now with a link the owner never saw.");

        var answer = await _decisions.ApproveSetAsync(Group, listing, null, CancellationToken.None);

        answer.Decided.ShouldBeFalse();
        answer.Mismatches.ShouldBe([new ActionSetMismatch(actions[1].Id, ActionDecisionService.ChangedAfterListing)]);
        (await RowsAsync(actions)).ShouldBe("PPP", "no action of a set with a changed text may be approved");
        (await EntriesAsync(actions)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_Action_Added_After_The_Listing_Stays_Pending()
    {
        await GrantAsync();
        var shown = await QueueAsync(2);
        var listing = shown.Select(a => (a.Id, a.PayloadDigest())).ToList();
        var late = await QueueAsync(1, first: 2);
        await _queue.CloseGroupAsync(Group, CancellationToken.None);
        List<PendingAction> actions = [.. shown, .. late];

        var answer = await _decisions.ApproveSetAsync(Group, listing, null, CancellationToken.None);

        answer.Decided.ShouldBeTrue();
        answer.Actions.Select(a => a.Id).ShouldBe(shown.Select(a => a.Id));
        (await RowsAsync(actions)).ShouldBe("AAP", "the action that joined after the listing is not covered by it");
        (await EntriesAsync(actions)).ShouldBe([$"approved a0 {Group}", $"approved a1 {Group}"]);
    }

    [Fact]
    public async Task An_Open_Group_Refuses_Approve_Set_A_Closed_One_Accepts_A_Changed_One_Only_Rejects()
    {
        await GrantAsync();
        var actions = await QueueAsync(3);
        List<(string, string)> Listing(params int[] indexes) => [.. indexes.Select(i => (actions[i].Id, actions[i].PayloadDigest()))];

        var whileOpen = await Should.ThrowAsync<ActionSetRefusedException>(() => _decisions.ApproveSetAsync(Group, Listing(0, 1, 2), null, CancellationToken.None));
        await _queue.CloseGroupAsync(Group, CancellationToken.None);
        var onceClosed = await _decisions.ApproveSetAsync(Group, Listing(0), null, CancellationToken.None);
        actions.AddRange(await QueueAsync(1, first: 3));
        var onceChanged = await Should.ThrowAsync<ActionSetRefusedException>(() => _decisions.ApproveSetAsync(Group, Listing(1, 2), null, CancellationToken.None));
        var rejectAll = await _decisions.RejectSetAsync(Group, [actions[1].Id, actions[2].Id, actions[3].Id], null, CancellationToken.None);

        whileOpen.Message.ShouldBe(ActionDecisionService.StillQueuing);
        onceClosed.Decided.ShouldBeTrue();
        onceChanged.Message.ShouldBe(ActionDecisionService.ChangedAfterClose);
        rejectAll.Decided.ShouldBeTrue();
        (await RowsAsync(actions)).ShouldBe("ARRR");
        (await EntriesAsync(actions)).ShouldBe([$"approved a0 {Group}", $"rejected a1 {Group}", $"rejected a2 {Group}", $"rejected a3 {Group}"]);
    }

    [Fact]
    public async Task Fifty_One_Ids_Are_Refused_Whole_And_Nothing_Is_Logged()
    {
        await GrantAsync();
        var actions = await QueueAsync(51);
        await _queue.CloseGroupAsync(Group, CancellationToken.None);

        var approve = await Should.ThrowAsync<ActionSetRefusedException>(
            () => _decisions.ApproveSetAsync(Group, [.. actions.Select(a => (a.Id, a.PayloadDigest()))], null, CancellationToken.None));
        var reject = await Should.ThrowAsync<ActionSetRefusedException>(
            () => _decisions.RejectSetAsync(Group, [.. actions.Select(a => a.Id)], null, CancellationToken.None));

        (approve.Message, reject.Message).ShouldBe((ActionDecisionService.SetOverTheBound, ActionDecisionService.SetOverTheBound));
        (await RowsAsync(actions)).ShouldBe(new string('P', 51), "a set over the bound is refused whole, never split");
        (await EntriesAsync(actions)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Second_Identical_Approve_Set_Answers_The_Same_And_Appends_Nothing()
    {
        await GrantAsync();
        var actions = await QueueAsync(3);
        await _queue.CloseGroupAsync(Group, CancellationToken.None);
        var listing = actions.Select(a => (a.Id, a.PayloadDigest())).ToList();

        var first = await _decisions.ApproveSetAsync(Group, listing, null, CancellationToken.None);
        var second = await _decisions.ApproveSetAsync(Group, listing, null, CancellationToken.None);

        second.Decided.ShouldBe(first.Decided);
        second.Decided.ShouldBeTrue();
        second.Actions.Select(a => (a.Id, a.Status)).ShouldBe(first.Actions.Select(a => (a.Id, a.Status)));
        second.Mismatches.ShouldBeEmpty();
        (await EntriesAsync(actions)).ShouldBe([$"approved a0 {Group}", $"approved a1 {Group}", $"approved a2 {Group}"]);
    }

    [Fact]
    public async Task A_Repeated_Approve_Set_Checks_The_Digest_Of_Each_Listed_Action_Whose_Payload_Is_Whole_And_Answers_Each_Row_With_Its_Outcome()
    {
        await GrantAsync();
        var actions = await QueueAsync(3);
        await _queue.CloseGroupAsync(Group, CancellationToken.None);
        var listing = actions.Select(a => (a.Id, a.PayloadDigest())).ToList();
        await _decisions.ApproveSetAsync(Group, listing, null, CancellationToken.None);
        await _queue.ExecuteAsync(actions[0].Id, "failed: github 502", "GitHub is currently unavailable. Try again later.", _ => Task.CompletedTask, CancellationToken.None);

        var repeated = await _decisions.ApproveSetAsync(Group, listing, null, CancellationToken.None);
        await RewriteBodyAsync(actions[1].Id, "Now with a link the owner never saw.");
        var afterTheChange = await _decisions.ApproveSetAsync(Group, listing, null, CancellationToken.None);

        (repeated.Decided, repeated.Mismatches.Count).ShouldBe((true, 0), "the executed row's payload was cut, so it is answered as it stands");
        repeated.Actions.Select(a => (a.Status, a.Outcome)).ShouldBe([
            (PendingActionStatus.Executed, (string?)"failed: github 502"),
            (PendingActionStatus.Approved, null),
            (PendingActionStatus.Approved, null)], "decided means the decision was applied; each row says how its send went");
        afterTheChange.Decided.ShouldBeFalse("a repeat never answers as decided a set whose listed text no longer matches");
        afterTheChange.Mismatches.ShouldBe([new ActionSetMismatch(actions[1].Id, ActionDecisionService.ChangedAfterListing)]);
        (await EntriesAsync(actions)).ShouldBe([$"approved a0 {Group}", $"approved a1 {Group}", $"approved a2 {Group}"], "a repeat appends nothing");
    }

    [Fact]
    public async Task A_Repeated_Approve_Set_Needs_No_Grant_Because_It_Approves_Nothing()
    {
        await GrantAsync();
        var actions = await QueueAsync(2);
        await _queue.CloseGroupAsync(Group, CancellationToken.None);
        var listing = actions.Select(a => (a.Id, a.PayloadDigest())).ToList();
        await _decisions.ApproveSetAsync(Group, listing, null, CancellationToken.None);
        await _states.SetAsync(FeatureCatalog.GitHubId, new FeatureState(true, null, new Dictionary<string, string?>(), Granted: false), CancellationToken.None);

        var repeated = await _decisions.ApproveSetAsync(Group, listing, null, CancellationToken.None);

        (repeated.Decided, repeated.Mismatches.Count).ShouldBe((true, 0));
        repeated.Actions.Select(a => a.Status).ShouldBe([PendingActionStatus.Approved, PendingActionStatus.Approved]);
        (await EntriesAsync(actions)).ShouldBe([$"approved a0 {Group}", $"approved a1 {Group}"]);
    }

    [Fact]
    public async Task A_Set_Approves_An_Action_Whose_Listed_Text_Was_Only_A_Preview_Without_Every_Text_Being_Opened()
    {
        await GrantAsync();
        var actions = await QueueAsync(1);
        var longBody = new string('b', PendingAction.MaxListedBodyLength + 1);
        actions.Add(await _queue.EnqueueAsync(
            FeatureCatalog.GitHubId, "New issues", "GitHubActions.create_issue", $"Open an issue on {Shop}",
            $$"""{"owner":"acme","repo":"shop","title":"Finding 1","body":"{{longBody}}"}""", Group, Shop, CancellationToken.None));
        await _queue.CloseGroupAsync(Group, CancellationToken.None);
        var listed = await _decisions.ListPendingAsync(CancellationToken.None);

        var answer = await _decisions.ApproveSetAsync(Group, [.. listed.Select(l => (l.Action.Id, l.Action.PayloadDigest()))], null, CancellationToken.None);

        listed.Select(l => l.Action.ListedText().Clipped).ShouldBe([false, true], "the second action's list text is a preview");
        answer.Decided.ShouldBeTrue("listed, with the full text one call away, is enough to approve a set");
        answer.Mismatches.ShouldBeEmpty();
        (await RowsAsync(actions)).ShouldBe("AA");
        (await EntriesAsync(actions)).ShouldBe([$"approved a0 {Group}", $"approved a1 {Group}"]);
    }

    [Theory]
    [InlineData("an id the queue does not hold")]
    [InlineData("an action of another request")]
    [InlineData("an action of the same request on another repository")]
    public async Task A_Listed_Action_Outside_The_Group_Decides_None_And_Is_Answered_As_Not_In_This_Group(string outside)
    {
        await GrantAsync();
        var actions = await QueueAsync(2);
        var stranger = outside switch
        {
            "an id the queue does not hold" => null,
            "an action of another request" => await _queue.EnqueueAsync(FeatureCatalog.GitHubId, "New issues", "GitHubActions.create_issue", "s", Payload(9), "turn-2", Shop, CancellationToken.None),
            _ => await _queue.EnqueueAsync(FeatureCatalog.GitHubId, "New issues", "GitHubActions.create_issue", "s", Payload(9), Group, "acme/site", CancellationToken.None),
        };
        await _queue.CloseGroupAsync(Group, CancellationToken.None);
        var strangerId = stranger?.Id ?? "no-such-action";

        var approve = await _decisions.ApproveSetAsync(
            Group, [(actions[0].Id, actions[0].PayloadDigest()), (actions[1].Id, actions[1].PayloadDigest()), (strangerId, stranger?.PayloadDigest() ?? "x")], null, CancellationToken.None);
        var reject = await _decisions.RejectSetAsync(Group, [actions[0].Id, actions[1].Id, strangerId], null, CancellationToken.None);

        approve.Mismatches.ShouldBe([new ActionSetMismatch(strangerId, ActionDecisionService.NotInTheGroup)]);
        reject.Mismatches.ShouldBe([new ActionSetMismatch(strangerId, ActionDecisionService.NotInTheGroup)]);
        (approve.Decided, reject.Decided).ShouldBe((false, false));
        (await RowsAsync(actions)).ShouldBe("PP");
        (await _log.ListAsync(10, CancellationToken.None)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("no action")]
    [InlineData("one action twice")]
    public async Task A_Set_Naming_No_Action_Or_One_Twice_Is_Refused_Whole(string set)
    {
        await GrantAsync();
        var actions = await QueueAsync(2);
        await _queue.CloseGroupAsync(Group, CancellationToken.None);
        string[] ids = set == "no action" ? [] : [actions[0].Id, actions[1].Id, actions[0].Id];

        var approve = await Should.ThrowAsync<ActionSetRefusedException>(
            () => _decisions.ApproveSetAsync(Group, [.. ids.Select(id => (id, actions.Single(a => a.Id == id).PayloadDigest()))], null, CancellationToken.None));
        var reject = await Should.ThrowAsync<ActionSetRefusedException>(() => _decisions.RejectSetAsync(Group, ids, null, CancellationToken.None));

        (approve.Message, reject.Message).ShouldBe((ActionDecisionService.SetNotAList, ActionDecisionService.SetNotAList));
        (await RowsAsync(actions)).ShouldBe("PP");
        (await _log.ListAsync(10, CancellationToken.None)).ShouldBeEmpty();
    }

    private async Task<(List<PendingAction> Actions, List<PendingAction> Listed, PendingAction Single)> ArrangeAsync(string state)
    {
        switch (state)
        {
            case "closed":
                {
                    await GrantAsync();
                    var actions = await QueueAsync(3);
                    await _queue.CloseGroupAsync(Group, CancellationToken.None);
                    return (actions, actions, actions[2]);
                }

            case "partly decided":
            case "partly decided, listed before":
                {
                    await GrantAsync();
                    var actions = await QueueAsync(3);
                    await _queue.CloseGroupAsync(Group, CancellationToken.None);
                    await _decisions.ApproveAsync(actions[0].Id, null, CancellationToken.None);
                    return (actions, state == "partly decided" ? actions[1..] : actions, actions[2]);
                }

            case "open":
                {
                    await GrantAsync();
                    var actions = await QueueAsync(3);
                    return (actions, actions, actions[2]);
                }

            case "changed":
                {
                    await GrantAsync();
                    var shown = await QueueAsync(3);
                    await _queue.CloseGroupAsync(Group, CancellationToken.None);
                    var actions = shown.Concat(await QueueAsync(1, first: 3)).ToList();
                    return (actions, shown, actions[2]);
                }

            case "over the bound":
                {
                    await GrantAsync();
                    var actions = await QueueAsync(51);
                    await _queue.CloseGroupAsync(Group, CancellationToken.None);
                    return (actions, actions[..50], actions[50]);
                }

            case "without the grant":
                {
                    var actions = await QueueAsync(3);
                    await _queue.CloseGroupAsync(Group, CancellationToken.None);
                    return (actions, actions, actions[2]);
                }

            default:
                {
                    var actions = await QueueAsync(1);
                    await GrantAsync(actions[0].RequestedAt.AddTicks(1));
                    actions.AddRange(await QueueAsync(2, first: 1));
                    await _queue.CloseGroupAsync(Group, CancellationToken.None);
                    return (actions, actions, actions[0]);
                }
        }
    }

    private static async Task<string> AnswerAsync(Func<Task<ActionSetDecision>> decide, IReadOnlyList<PendingAction> actions)
    {
        try
        {
            var decision = await decide();
            return decision.Decided
                ? "decided"
                : string.Join("; ", decision.Mismatches.Select(m => $"mismatch a{actions.ToList().FindIndex(a => a.Id == m.Id)} {m.Reason}"));
        }
        catch (ActionSetRefusedException ex)
        {
            return ex.Message;
        }
        catch (WriteNotGrantedException)
        {
            return "not granted";
        }
    }

    private static async Task<string> AnswerAsync(Func<Task<string>> decide)
    {
        try
        {
            return await decide();
        }
        catch (WriteNotGrantedException)
        {
            return "not granted";
        }
    }

    private Task GrantAsync(DateTimeOffset? at = null) =>
        _states.SetAsync(
            FeatureCatalog.GitHubId,
            new FeatureState(true, null, new Dictionary<string, string?>(), Granted: true, GrantedAt: at ?? DateTimeOffset.UtcNow.AddMinutes(-1)),
            CancellationToken.None);

    private async Task<List<PendingAction>> QueueAsync(int count, int first = 0)
    {
        var queued = new List<PendingAction>();
        for (var i = first; i < first + count; i++)
        {
            queued.Add(await _queue.EnqueueAsync(
                FeatureCatalog.GitHubId, "New issues", "GitHubActions.create_issue", $"Open an issue on {Shop}", Payload(i), Group, Shop, CancellationToken.None));
        }

        return queued;
    }

    private static string Payload(int i) => $$"""{"owner":"acme","repo":"shop","title":"Finding {{i}}","body":"Body of finding {{i}}."}""";

    private async Task<string> RowsAsync(IReadOnlyList<PendingAction> actions)
    {
        var letters = new List<char>();
        foreach (var action in actions)
        {
            letters.Add((await _queue.FindAsync(action.Id, CancellationToken.None)).ShouldNotBeNull().Status switch
            {
                PendingActionStatus.Pending => 'P',
                PendingActionStatus.Approved => 'A',
                PendingActionStatus.Rejected => 'R',
                var other => other.ToString()[0],
            });
        }

        return new string([.. letters]);
    }

    private async Task<string[]> EntriesAsync(IReadOnlyList<PendingAction> actions) =>
        [.. (await _log.ListAsync(200, CancellationToken.None)).Reverse()
            .Select(e => $"{e.Outcome} a{actions.ToList().FindIndex(a => a.Id == e.ActionId)} {e.GroupId}")];

    private async Task RewriteBodyAsync(string id, string body)
    {
        var all = JsonNode.Parse(await File.ReadAllTextAsync(_queue.FilePath))!.AsArray();
        var row = all.Single(a => a!["id"]!.GetValue<string>() == id)!;
        var payload = JsonNode.Parse(row["payload"]!.GetValue<string>())!;
        payload["body"] = body;
        row["payload"] = payload.ToJsonString();
        await File.WriteAllTextAsync(_queue.FilePath, all.ToJsonString());
    }
}
