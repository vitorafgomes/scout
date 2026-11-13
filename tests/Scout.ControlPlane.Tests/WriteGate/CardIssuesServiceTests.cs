using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Features;
using Scout.ControlPlane.Tests.Integration;
using Scout.ControlPlane.WriteGate;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.WriteGate;

/// <summary>
/// A findings card's Open issues, over Scout's own queue, log and feature state in a temp folder: the click is the owner's
/// approval of exactly the issues the card sent, queued already approved on the declared <c>New issues</c> row as one
/// group, one <c>approved</c> entry each, and a second click with the same card and the same set answers the same actions.
/// </summary>
public sealed class CardIssuesServiceTests : IDisposable
{
    private const string Card = "card-1";
    private const string Shop = "acme/shop";

    private readonly string _directory = Directory.CreateTempSubdirectory("scout-card-issues-").FullName;
    private readonly JsonFileFeatureStateStore _states;
    private readonly JsonFileActionQueue _queue;
    private readonly JsonLinesWriteLog _log;
    private readonly CardIssuesService _cards;

    public CardIssuesServiceTests()
    {
        var options = Options.Create(new FeatureStateStoreOptions { FilePath = Path.Combine(_directory, "features.json") });
        _states = new JsonFileFeatureStateStore(options, NullLogger<JsonFileFeatureStateStore>.Instance);
        _queue = new JsonFileActionQueue(options, NullLogger<JsonFileActionQueue>.Instance);
        _log = new JsonLinesWriteLog(options, NullLogger<JsonLinesWriteLog>.Instance);
        _cards = new CardIssuesService(_states, _queue, _log, NullLogger<CardIssuesService>.Instance);
    }

    public void Dispose()
    {
        _cards.Dispose();
        _states.Dispose();
        _queue.Dispose();
        _log.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    /// <summary>One row: whether GitHub writes are granted, how many issues the card sends, whether it is clicked twice, and the answer.</summary>
    public static TheoryData<bool, int, bool, string> Clicks() => new()
    {
        { true, 0, false, "refused: issues" },
        { true, 0, true, "refused: issues" },
        { true, 1, false, "queued 1" },
        { true, 1, true, "queued 1" },
        { true, 50, false, "queued 50" },
        { true, 50, true, "queued 50" },
        { true, 51, false, "refused: issues" },
        { true, 51, true, "refused: issues" },
        { false, 0, false, "refused: issues" },
        { false, 0, true, "refused: issues" },
        { false, 1, false, "not granted" },
        { false, 1, true, "not granted" },
        { false, 50, false, "not granted" },
        { false, 50, true, "not granted" },
        { false, 51, false, "refused: issues" },
        { false, 51, true, "refused: issues" },
    };

    [Theory]
    [MemberData(nameof(Clicks))]
    public async Task A_Card_s_Issues_Are_Queued_Approved_Logged_Once_Each_And_A_Second_Click_Answers_The_Same(
        bool granted, int size, bool twice, string expected)
    {
        if (granted)
        {
            await GrantAsync();
        }

        var issues = Issues(size);
        var label = $"granted {granted}, {size} issues, clicked {(twice ? "twice" : "once")}";

        var (first, firstAnswer) = await ClickAsync(Card, Shop, issues);
        var (second, secondAnswer) = twice ? await ClickAsync(Card, Shop, issues) : (first, firstAnswer);

        (firstAnswer, secondAnswer).ShouldBe((expected, expected), label);
        var rows = await _queue.ListWhereAsync(_ => true, CancellationToken.None);
        var entries = (await _log.ListAsync(500, CancellationToken.None)).Reverse().ToList();
        if (first is null)
        {
            rows.ShouldBeEmpty(label);
            entries.ShouldBeEmpty(label);
            return;
        }

        second.ShouldNotBeNull().Actions.Select(a => a.Id).ShouldBe(first.Actions.Select(a => a.Id), label + ": the second click answers the same actions");
        (second.Decided, second.Mismatches.Count).ShouldBe((true, 0), label);
        var group = first.Actions[0].GroupId.ShouldNotBeNull();
        rows.Select(a => a.Id).ShouldBe(first.Actions.Select(a => a.Id), label + ": nothing more is queued");
        rows.ShouldAllBe(a => a.Status == PendingActionStatus.Approved, label + ": nothing is queued as pending");
        rows.ShouldAllBe(a => a.FeatureId == FeatureCatalog.GitHubId && a.Action == "New issues" && a.Target == "GitHubActions.create_issue" && a.GroupId == group && a.Repository == Shop && a.GroupClosedAt != null, label);
        rows.Select(a => a.FullText()).ShouldBe(issues.Select(i => ((string?)i.Title, (string?)i.Body)), label + ": each issue as the card sent it, in order");
        entries.Select(e => (e.ActionId, e.Outcome, e.ApprovedBy, e.GroupId, e.Action)).ShouldBe(
            rows.Select(a => ((string?)a.Id, "approved", (string?)"owner", (string?)group, "New issues")), label + ": one approved entry per issue, once");
    }

    [Fact]
    public async Task The_Queued_Payload_And_Summary_Are_Those_A_Chat_Request_For_The_Same_Issue_Queues()
    {
        await GrantAsync();

        var (answer, _) = await ClickAsync(Card, Shop, [("Total skips the discount", "Checkout.cs:88 sums before the discount applies.")]);

        var action = answer.ShouldNotBeNull().Actions.ShouldHaveSingleItem();
        action.Payload.ShouldBe("""{"body":"Checkout.cs:88 sums before the discount applies.","owner":"acme","repo":"shop","title":"Total skips the discount"}""");
        action.Summary.ShouldBe("create_issue(body, owner=acme, repo=shop, title=Total skips the discount)");
    }

    [Fact]
    public async Task The_Same_Set_In_Another_Order_Answers_The_Same_And_Another_Repository_Or_Card_Queues_Its_Own()
    {
        await GrantAsync();
        (string, string)[] set = [("A", "Body of A."), ("B", "Body of B.")];

        var (first, _) = await ClickAsync(Card, Shop, set);
        var (reordered, _) = await ClickAsync(Card, Shop, [.. set.Reverse()]);
        var (otherRepository, _) = await ClickAsync(Card, "acme/site", set);
        var (otherCard, _) = await ClickAsync("card-2", Shop, set);

        reordered.ShouldNotBeNull().Actions.Select(a => a.Id).ShouldBe(first.ShouldNotBeNull().Actions.Select(a => a.Id).Reverse(), "each issue answers its row, in the order the click lists them");
        otherRepository.ShouldNotBeNull().Actions[0].GroupId.ShouldNotBe(first.Actions[0].GroupId);
        otherCard.ShouldNotBeNull().Actions[0].GroupId.ShouldNotBe(first.Actions[0].GroupId);
        (await _queue.ListWhereAsync(_ => true, CancellationToken.None)).Count.ShouldBe(6);
    }

    public static TheoryData<string, string, string, int, string?, string?, string> Refusals() => new()
    {
        { "a blank card id", " ", Shop, 1, null, null, "cardId" },
        { "a card id over 200 characters", new string('c', 201), Shop, 1, null, null, "cardId" },
        { "a repository without an owner", Card, "shop", 1, null, null, "repository" },
        { "a repository starting with a dot", Card, ".acme/shop", 1, null, null, "repository" },
        { "a repository ending in a newline", Card, "acme/shop\n", 1, null, null, "repository" },
        { "an owner ending in a newline", Card, "acme\n/shop", 1, null, null, "repository" },
        { "no issue", Card, Shop, 0, null, null, "issues" },
        { "51 issues", Card, Shop, 51, null, null, "issues" },
        { "a blank title", Card, Shop, 1, "   ", null, "issues[0].title" },
        { "a title over 256 characters once trimmed", Card, Shop, 1, " " + new string('t', 257), null, "issues[0].title" },
        { "a blank body", Card, Shop, 1, null, " ", "issues[0].body" },
        { "a body over 10,000 characters once trimmed", Card, Shop, 1, null, new string('b', 10_001) + " ", "issues[0].body" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task Each_Refusal_Fires_On_Its_Own_Input_And_Queues_And_Logs_Nothing(
        string label, string cardId, string repository, int count, string? title, string? body, string key)
    {
        await GrantAsync();
        var issues = Issues(count).Select(i => (title ?? i.Title, body ?? i.Body)).ToList();

        var refused = await Should.ThrowAsync<FeatureValidationFailedException>(() => _cards.ApproveAsync(cardId, repository, issues, CancellationToken.None));

        refused.Errors.Keys.ShouldBe([key], label);
        (await _queue.ListWhereAsync(_ => true, CancellationToken.None)).ShouldBeEmpty(label);
        (await _log.ListAsync(10, CancellationToken.None)).ShouldBeEmpty(label);
    }

    [Fact]
    public async Task The_Largest_Issue_The_Plugin_Sends_Is_Queued()
    {
        await GrantAsync();
        var title = new string('t', 256);
        var body = new string('b', 10_000);

        var (answer, text) = await ClickAsync(Card, Shop, [(" " + title + " ", body)]);

        text.ShouldBe("queued 1");
        answer.ShouldNotBeNull().Actions.ShouldHaveSingleItem().FullText().ShouldBe((title, body));
    }

    [Fact]
    public async Task A_Padded_Title_And_Body_Are_Trimmed_Once_Then_Validated_Stored_Summarised_And_Keyed_As_Trimmed()
    {
        await GrantAsync();
        var title = new string('t', 256);
        const string body = "Ten chars.";

        var (first, text) = await ClickAsync(Card, Shop, [("  " + title + new string(' ', 10), body + new string(' ', 300_000))]);
        var (repadded, _) = await ClickAsync(Card, Shop, [(title + "\n", "\t" + body)]);

        text.ShouldBe("queued 1", "a padded body within the bounds once trimmed is queued, never failed at the queue's payload bound");
        var action = first.ShouldNotBeNull().Actions.ShouldHaveSingleItem();
        action.FullText().ShouldBe(((string?)title, (string?)body), "the trimmed text is what is stored and sent");
        action.Summary.ShouldBe($"create_issue(body, owner=acme, repo=shop, title={title[..200]})");
        repadded.ShouldNotBeNull().Actions.Select(a => a.Id).ShouldBe([action.Id], "the same issue with other padding is the same issue");
        (await _queue.ListWhereAsync(_ => true, CancellationToken.None)).Count.ShouldBe(1, "a second click with other padding opens nothing more");
    }

    [Fact]
    public async Task A_Log_Failing_Part_Way_Rejects_What_The_Click_Queued_And_The_Next_Click_Queues_Them_Again()
    {
        await GrantAsync();
        var failing = true;
        var approvals = 0;
        var log = FailingCalls.Wrap<IWriteLog>(_log, (method, args) =>
            failing && method.Name == nameof(IWriteLog.AppendAsync)
            && ((WriteLogEntry)args![0]!).Outcome == WriteGateService.ApprovedOutcome
            && Interlocked.Increment(ref approvals) > 1
                ? new IOException("disk full")
                : null);
        using var cards = new CardIssuesService(_states, _queue, log, NullLogger<CardIssuesService>.Instance);
        var issues = Issues(3);

        await Should.ThrowAsync<IOException>(() => cards.ApproveAsync(Card, Shop, issues, CancellationToken.None));
        var afterTheFailure = await _queue.ListWhereAsync(_ => true, CancellationToken.None);
        var entriesAfterTheFailure = (await _log.ListAsync(10, CancellationToken.None)).Reverse().Select(e => (e.ActionId, e.Outcome)).ToList();
        failing = false;
        var again = await cards.ApproveAsync(Card, Shop, issues, CancellationToken.None);

        afterTheFailure.Select(a => (a.Status, a.Reason)).ShouldBe(
            Enumerable.Repeat((PendingActionStatus.Rejected, (string?)CardIssuesService.ApprovalNotLogged), 3), "no issue of a click whose approvals the log did not all take may be sent");
        entriesAfterTheFailure.ShouldBe([
            (afterTheFailure[0].Id, "approved"),
            .. afterTheFailure.Select(a => ((string?)a.Id, $"rejected: {CardIssuesService.ApprovalNotLogged}"))]);
        again.Decided.ShouldBeTrue();
        again.Actions.Select(a => a.Id).ShouldNotContain(id => afterTheFailure.Any(f => f.Id == id));
        again.Actions[0].GroupId.ShouldNotBe(afterTheFailure[0].GroupId);
        again.Actions.ShouldAllBe(a => a.Status == PendingActionStatus.Approved);
    }

    [Fact]
    public async Task A_Repeat_Click_Names_An_Issue_Whose_Approval_The_Log_Never_Took_And_Never_Answers_The_Set_As_Approved()
    {
        await GrantAsync();
        var failing = true;
        var approvals = 0;
        var log = FailingCalls.Wrap<IWriteLog>(_log, (method, args) =>
            failing && method.Name == nameof(IWriteLog.AppendAsync)
            && ((WriteLogEntry)args![0]!).Outcome == WriteGateService.ApprovedOutcome
            && Interlocked.Increment(ref approvals) > 1
                ? new IOException("disk full")
                : null);
        var queue = FailingCalls.Wrap<IActionQueue>(_queue, (method, args) =>
            method.Name == nameof(IActionQueue.RejectApprovedAsync) && (string)args![2]! == CardIssuesService.ApprovalNotLogged
                ? new IOException("disk full")
                : null);
        using var cards = new CardIssuesService(_states, queue, log, NullLogger<CardIssuesService>.Instance);
        var issues = Issues(2);

        await Should.ThrowAsync<IOException>(() => cards.ApproveAsync(Card, Shop, issues, CancellationToken.None));
        failing = false;
        var repeat = await cards.ApproveAsync(Card, Shop, issues, CancellationToken.None);

        repeat.Decided.ShouldBeFalse("a set whose approvals the log did not all take is never answered as approved");
        repeat.Mismatches.ShouldBe([new ActionSetMismatch(repeat.Actions[1].Id, WriteGateService.ApprovalNotInLog)]);
        (await _log.ListAsync(10, CancellationToken.None)).Select(e => e.ActionId).ShouldBe([repeat.Actions[0].Id], "the repeat appends nothing");
    }

    [Fact]
    public async Task A_Second_Click_While_The_First_Is_Still_Logging_Its_Approvals_Waits_And_Answers_The_First_Click_s_Rows()
    {
        await GrantAsync();
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readWhileParked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = Substitute.For<IWriteLog>();
        log.ListSinceAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(call => ListSinceAsync(call.Arg<DateTimeOffset>()));
        log.AppendAsync(Arg.Any<WriteLogEntry>(), Arg.Any<CancellationToken>()).Returns(call => AppendAsync(call.Arg<WriteLogEntry>()));
        using var cards = new CardIssuesService(_states, _queue, log, NullLogger<CardIssuesService>.Instance);
        var issues = Issues(3);

        var first = cards.ApproveAsync(Card, Shop, issues, CancellationToken.None);
        await parked.Task;
        var second = cards.ApproveAsync(Card, Shop, issues, CancellationToken.None);
        await Task.WhenAny(readWhileParked.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        release.SetResult();
        var (firstAnswer, secondAnswer) = (await first, await second);

        secondAnswer.Mismatches.ShouldBeEmpty("a click that arrives while the first still logs its approvals waits for it");
        secondAnswer.Decided.ShouldBeTrue();
        secondAnswer.Actions.Select(a => a.Id).ShouldBe(firstAnswer.Actions.Select(a => a.Id), "the second click answers the first click's rows");
        (await _queue.ListWhereAsync(_ => true, CancellationToken.None)).Count.ShouldBe(3, "nothing is queued twice");
        (await _log.ListAsync(10, CancellationToken.None)).Select(e => e.Outcome).ShouldBe(Enumerable.Repeat("approved", 3));

        async Task AppendAsync(WriteLogEntry entry)
        {
            if (parked.TrySetResult())
            {
                await release.Task;
            }

            await _log.AppendAsync(entry, CancellationToken.None);
        }

        async Task<IReadOnlyList<WriteLogEntry>> ListSinceAsync(DateTimeOffset since)
        {
            var read = await _log.ListSinceAsync(since, CancellationToken.None);
            if (!release.Task.IsCompleted)
            {
                readWhileParked.TrySetResult();
            }

            return read;
        }
    }

    /// <summary>One row: how each of two issues the first click queued ended, and what a second click does with each.</summary>
    public static TheoryData<string, string[], string[]> Outcomes() => new()
    {
        { "every issue failed", ["failed", "failed"], ["again", "again"] },
        { "every issue rejected", ["rejected", "rejected"], ["again", "again"] },
        { "one failed and one rejected", ["failed", "rejected"], ["again", "again"] },
        { "both approved before the current grant", ["stale", "stale"], ["again", "again"] },
        { "one sent and one failed", ["sent", "failed"], ["answered", "again"] },
        { "one rejected and one still waiting to be sent", ["rejected", "approved"], ["again", "answered"] },
        { "one being sent and one failed", ["executing", "failed"], ["answered", "again"] },
        { "one not confirmed, so maybe on GitHub, and one failed", ["not confirmed", "failed"], ["answered", "again"] },
        { "every issue sent, its payload cut", ["sent", "sent"], ["answered", "answered"] },
        { "one rejected after its claim because writes were revoked before the send", ["revoked after its claim", "sent"], ["again", "answered"] },
        { "one rejected because its approval was not in the write log", ["approval not in the log", "sent"], ["again", "answered"] },
        { "one rejected for another reason", ["rejected for another reason", "sent"], ["again", "answered"] },
    };

    [Theory]
    [MemberData(nameof(Outcomes))]
    public async Task A_Second_Click_Queues_Again_Exactly_The_Issues_That_Failed_Or_Were_Rejected_And_Answers_The_Others(
        string label, string[] outcomes, string[] expected)
    {
        await GrantAsync();
        var gate = new WriteGateService(new FeatureCatalog(), _states, _queue, _log);
        var issues = Issues(2);
        var first = (await _cards.ApproveAsync(Card, Shop, issues, CancellationToken.None)).Actions;
        for (var i = 0; i < first.Count; i++)
        {
            var id = first[i].Id;
            switch (outcomes[i])
            {
                case "sent":
                    await _queue.ExecuteAsync(id, "succeeded", $"https://github.com/acme/shop/issues/{i + 1}", _ => Task.CompletedTask, CancellationToken.None);
                    break;
                case "failed":
                    await _queue.ExecuteAsync(id, "failed: github 502", "GitHub is currently unavailable. Try again later.", _ => Task.CompletedTask, CancellationToken.None);
                    break;
                case "not confirmed":
                    await _queue.ExecuteAsync(id, WriteGateService.NotConfirmedOutcome, "Scout stopped before it recorded this.", _ => Task.CompletedTask, CancellationToken.None);
                    break;
                case "executing":
                    await _queue.ClaimNextAsync(FeatureCatalog.GitHubId, TimeSpan.FromMinutes(5), (a, _) => a.Id == id, CancellationToken.None);
                    break;
                case "rejected":
                    await _queue.RejectApprovedAsync(FeatureCatalog.GitHubId, (a, _) => a.Id == id, WriteGateService.WritesRevoked, CancellationToken.None);
                    break;
                case "revoked after its claim":
                    await _queue.ClaimNextAsync(FeatureCatalog.GitHubId, TimeSpan.FromMinutes(5), (a, _) => a.Id == id, CancellationToken.None);
                    await _states.SetAsync(FeatureCatalog.GitHubId, new FeatureState(true, null, new Dictionary<string, string?>(), Granted: false), CancellationToken.None);
                    (await gate.CheckGrantAsync(FeatureCatalog.GitHubId, id, CancellationToken.None)).ShouldBeFalse(label);
                    await GrantAsync();
                    break;
                case "approval not in the log":
                    await _queue.ClaimNextAsync(FeatureCatalog.GitHubId, TimeSpan.FromMinutes(5), (a, _) => a.Id == id, CancellationToken.None);
                    await _queue.RejectExecutingAsync(FeatureCatalog.GitHubId, a => a.Id == id, WriteGateService.ApprovalNotInLog, CancellationToken.None);
                    break;
                case "rejected for another reason":
                    await _queue.RejectApprovedAsync(FeatureCatalog.GitHubId, (a, _) => a.Id == id, "not this one", CancellationToken.None);
                    break;
            }
        }

        if (outcomes.Contains("stale"))
        {
            await GrantAsync(first[0].RequestedAt.AddTicks(1));
        }

        var second = await _cards.ApproveAsync(Card, Shop, issues, CancellationToken.None);

        second.Actions.Select((a, i) => a.Id == first[i].Id ? "answered" : "again").ShouldBe(expected, label);
        second.Decided.ShouldBeTrue(label);
        var again = second.Actions.Where(a => !first.Any(f => f.Id == a.Id)).ToList();
        again.ShouldAllBe(a => a.Status == PendingActionStatus.Approved && a.GroupId != first[0].GroupId, label + ": an issue queued again is approved in a new group");
        var grantedAt = (await _states.GetAsync(FeatureCatalog.GitHubId, CancellationToken.None)).GrantedAt.ShouldNotBeNull();
        again.ShouldAllBe(a => a.RequestedAt >= grantedAt, label + ": the issues queued again are asked under the current grant, so a worker claims them");
        var rows = await _queue.ListWhereAsync(_ => true, CancellationToken.None);
        rows.Count.ShouldBe(2 + again.Count, label + ": only the issues queued again are new rows");
        rows.Where(a => first.Any(f => f.Id == a.Id) && !second.Actions.Any(s => s.Id == a.Id))
            .ShouldAllBe(a => a.Status != PendingActionStatus.Approved, label + ": an earlier row queued again is never handed out");
        (await _log.ListAsync(20, CancellationToken.None)).Count(e => e.Outcome == "approved").ShouldBe(2 + again.Count, label + ": one approval per issue queued");
    }

    [Fact]
    public async Task An_Issue_Whose_Claim_Lapsed_Across_A_Re_Grant_Is_Rejected_As_Maybe_On_GitHub_And_A_Later_Click_Answers_It_And_Never_Queues_It_Again()
    {
        await GrantAsync();
        var gate = new WriteGateService(new FeatureCatalog(), _states, _queue, _log);
        var issues = Issues(1);
        var queued = (await _cards.ApproveAsync(Card, Shop, issues, CancellationToken.None)).Actions.ShouldHaveSingleItem();
        (await gate.ClaimNextApprovedAsync(FeatureCatalog.GitHubId, CancellationToken.None)).ShouldNotBeNull().Id.ShouldBe(queued.Id);
        await _states.SetAsync(FeatureCatalog.GitHubId, new FeatureState(true, null, new Dictionary<string, string?>(), Granted: false), CancellationToken.None);
        await GrantAsync(DateTimeOffset.UtcNow);
        await LapseClaimAsync(queued.Id);
        (await gate.ClaimNextApprovedAsync(FeatureCatalog.GitHubId, CancellationToken.None)).ShouldBeNull();
        var rejected = (await _queue.FindAsync(queued.Id, CancellationToken.None)).ShouldNotBeNull();

        var again = await _cards.ApproveAsync(Card, Shop, issues, CancellationToken.None);

        (rejected.Status, rejected.Reason).ShouldBe((PendingActionStatus.Rejected, (string?)WriteGateService.NotConfirmed("GitHub")), "the claim path rejects a lapsed claim across a re-grant as maybe on GitHub");
        var answered = again.Actions.ShouldHaveSingleItem();
        (answered.Id, answered.Status, answered.Reason).ShouldBe(
            (queued.Id, PendingActionStatus.Rejected, (string?)WriteGateService.NotConfirmed("GitHub")), "a row that may already be on GitHub answers the click, with its status and reason");
        (await _queue.ListWhereAsync(_ => true, CancellationToken.None)).Count.ShouldBe(1, "the issue is never queued again");
        (await gate.ClaimNextApprovedAsync(FeatureCatalog.GitHubId, CancellationToken.None)).ShouldBeNull("nothing is handed to a worker to send");
    }

    [Fact]
    public async Task The_Same_Card_Clicked_Again_With_One_Finding_Edited_And_Then_With_One_Removed_Never_Opens_An_Issue_Already_Queued_Again()
    {
        await GrantAsync();
        var first = await _cards.ApproveAsync(Card, Shop, Issues(3), CancellationToken.None);
        List<(string Title, string Body)> edited = [.. Issues(3)];
        edited[2] = (edited[2].Title, "An edited body.");

        var afterTheEdit = await _cards.ApproveAsync(Card, Shop, edited, CancellationToken.None);
        var afterTheRemoval = await _cards.ApproveAsync(Card, Shop, edited[..2], CancellationToken.None);

        afterTheEdit.Actions.Select(a => a.Id).Take(2).ShouldBe(first.Actions.Select(a => a.Id).Take(2), "the unchanged issues answer their rows");
        afterTheEdit.Actions[2].Id.ShouldNotBe(first.Actions[2].Id, "an issue whose text changed is a new issue");
        afterTheEdit.Actions[2].FullText().ShouldBe(((string?)edited[2].Title, (string?)"An edited body."));
        afterTheRemoval.Actions.Select(a => a.Id).ShouldBe(first.Actions.Select(a => a.Id).Take(2), "a set with one finding removed answers the rest");
        (await _queue.ListWhereAsync(_ => true, CancellationToken.None)).Count.ShouldBe(4, "only the edited issue was queued");
        (await _log.ListAsync(20, CancellationToken.None)).Count.ShouldBe(4, "one approval per row queued, none for an answered one");
    }

    [Fact]
    public async Task An_Old_Queue_File_Without_Card_Keys_Still_Reads_And_A_Card_Queues_Its_Own()
    {
        await GrantAsync();
        await File.WriteAllTextAsync(_queue.FilePath, $$"""
            [{"id":"old","featureId":"github","action":"New issues","target":"GitHubActions.create_issue","summary":"create_issue(body, owner=acme, repo=shop, title=Old)","requestedAt":"{{DateTimeOffset.UtcNow.AddDays(-1):O}}","status":"executed","decidedAt":"{{DateTimeOffset.UtcNow.AddDays(-1):O}}","payload":"{\"owner\":\"acme\",\"repo\":\"shop\"}","outcome":"succeeded"}]
            """);

        var old = await _queue.FindAsync("old", CancellationToken.None);
        var (answer, text) = await ClickAsync(Card, Shop, [("Old", "Body.")]);

        (old.ShouldNotBeNull().Status, old.CardKey).ShouldBe((PendingActionStatus.Executed, null));
        text.ShouldBe("queued 1");
        answer.ShouldNotBeNull().Actions.ShouldHaveSingleItem().Id.ShouldNotBe("old");
        (await _queue.FindAsync("old", CancellationToken.None)).ShouldNotBeNull().CardKey.ShouldBeNull();
    }

    [Fact]
    public async Task A_Row_Keyed_By_The_Whole_Set_Is_Answered_While_Its_Text_Is_Whole_And_A_Click_Over_Such_Rows_Never_Throws()
    {
        await GrantAsync();
        var asked = DateTimeOffset.UtcNow.AddSeconds(-30);
        var setKey = $"{Card}:{new string('a', 64)}";
        await File.WriteAllTextAsync(_queue.FilePath, $$"""
            [{"id":"waiting","featureId":"github","action":"New issues","target":"GitHubActions.create_issue","summary":"create_issue(body, owner=acme, repo=shop, title=A)","requestedAt":"{{asked:O}}","status":"approved","decidedAt":"{{asked:O}}","payload":"{\"body\":\"Body of A. \",\"owner\":\"acme\",\"repo\":\"shop\",\"title\":\"A\"}","groupId":"old","repository":"acme/shop","groupClosedAt":"{{asked:O}}","cardKey":"{{setKey}}"},
             {"id":"sent","featureId":"github","action":"New issues","target":"GitHubActions.create_issue","summary":"create_issue(body, owner=acme, repo=shop, title=B)","requestedAt":"{{asked:O}}","status":"executed","decidedAt":"{{asked:O}}","payload":"{\"owner\":\"acme\",\"repo\":\"shop\"}","outcome":"succeeded","groupId":"old","repository":"acme/shop","groupClosedAt":"{{asked:O}}","cardKey":"{{setKey}}"}]
            """);
        await _log.AppendAsync(new WriteLogEntry(asked, FeatureCatalog.GitHubId, "New issues", "GitHubActions.create_issue", "approved", "owner", "waiting"), CancellationToken.None);

        var (answer, text) = await ClickAsync(Card, Shop, [("A", "Body of A."), ("B", "Body of B.")]);

        text.ShouldBe("queued 2");
        var actions = answer.ShouldNotBeNull().Actions;
        (answer.Decided, actions[0].Id).ShouldBe((true, "waiting"), "a row of the set-level key whose text is whole is recognised and answered");
        (actions[1].Status, actions[1].FullText(), actions[1].GroupId == "old").ShouldBe(
            (PendingActionStatus.Approved, ((string?)"B", (string?)"Body of B."), false),
            "an executed row of the set-level key lost its text, so its issue cannot be recognised and is queued again, approved in a new group");
        (await _queue.ListWhereAsync(_ => true, CancellationToken.None)).Count.ShouldBe(3, "the click added one row, nothing for the recognised one");
    }

    private async Task<(ActionSetDecision? Answer, string Text)> ClickAsync(string cardId, string repository, IReadOnlyList<(string Title, string Body)> issues)
    {
        try
        {
            var answer = await _cards.ApproveAsync(cardId, repository, issues, CancellationToken.None);
            return (answer, $"queued {answer.Actions.Count}");
        }
        catch (FeatureValidationFailedException ex)
        {
            return (null, $"refused: {string.Join(", ", ex.Errors.Keys)}");
        }
        catch (WriteNotGrantedException)
        {
            return (null, "not granted");
        }
    }

    private static List<(string Title, string Body)> Issues(int count) =>
        [.. Enumerable.Range(0, count).Select(i => ($"Finding {i}", $"What, why, how and where of finding {i}."))];

    private async Task LapseClaimAsync(string actionId)
    {
        var all = JsonNode.Parse(await File.ReadAllTextAsync(_queue.FilePath))!.AsArray();
        all.Single(a => a!["id"]!.GetValue<string>() == actionId)!["claimedAt"] =
            (DateTimeOffset.UtcNow - WriteGateService.ClaimLease - TimeSpan.FromMinutes(1)).ToString("O", CultureInfo.InvariantCulture);
        await File.WriteAllTextAsync(_queue.FilePath, all.ToJsonString());
    }

    private Task GrantAsync(DateTimeOffset? at = null) =>
        _states.SetAsync(
            FeatureCatalog.GitHubId,
            new FeatureState(true, null, new Dictionary<string, string?>(), Granted: true, GrantedAt: at ?? DateTimeOffset.UtcNow.AddMinutes(-1)),
            CancellationToken.None);
}
