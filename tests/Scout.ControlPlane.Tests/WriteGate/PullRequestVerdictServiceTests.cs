using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Features;
using Scout.ControlPlane.WriteGate;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.WriteGate;

/// <summary>The owner's verdict click and the gate's payload, over Scout's own state, queue and log files in a temp folder.</summary>
public sealed class PullRequestVerdictServiceTests : IDisposable
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private readonly string _directory = Directory.CreateTempSubdirectory("scout-verdicts-").FullName;
    private readonly JsonFileFeatureStateStore _states;
    private readonly JsonFileActionQueue _actions;
    private readonly JsonLinesWriteLog _writes;

    public PullRequestVerdictServiceTests()
    {
        var options = Options.Create(new FeatureStateStoreOptions { FilePath = Path.Combine(_directory, "features.json") });
        _states = new JsonFileFeatureStateStore(options, NullLogger<JsonFileFeatureStateStore>.Instance);
        _actions = new JsonFileActionQueue(options, NullLogger<JsonFileActionQueue>.Instance);
        _writes = new JsonLinesWriteLog(options, NullLogger<JsonLinesWriteLog>.Instance);
    }

    public void Dispose()
    {
        _states.Dispose();
        _actions.Dispose();
        _writes.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private PullRequestVerdictService Verdicts() => new(_states, _actions, _writes);

    private WriteGateService Gate() => new(new FeatureCatalog(), _states, _actions, _writes);

    private Task<FeatureSnapshot> GrantGitHubAsync() =>
        new FeaturesService(new FeatureCatalog(), _states, new InMemoryCredentialStore(), new NullFeatureActivator(), Array.Empty<IFeatureCredentialValidator>())
            .SetWriteGrantAsync(FeatureCatalog.GitHubId, granted: true, confirmWrites: true, CancellationToken.None);

    private static VerdictFinding Finding(string title = "c is new") => new("a", "fix", title, "src/Checkout.cs", 3, null, "Why.", "Fix.", null);

    private static PullRequestVerdictRequest Approve(string commit = Sha, string title = "c is new") =>
        new("octocat", "hello", 42, "approve", [Finding(title)], commit, "Tests: 3 checks passed.");

    [Fact]
    public async Task The_Owner_s_Click_Queues_The_Verdict_Already_Approved_With_What_The_Worker_Needs_And_Logs_The_Approval()
    {
        await GrantGitHubAsync();

        var action = await Verdicts().GiveAsync(Approve(), CancellationToken.None);

        (action.FeatureId, action.Action, action.Target, action.Summary, action.Status)
            .ShouldBe(("github", "Pull request verdicts", "octocat/hello#42", "Approve octocat/hello#42", PendingActionStatus.Approved));
        var payload = JsonDocument.Parse(action.Payload.ShouldNotBeNull()).RootElement;
        payload.GetProperty("owner").GetString().ShouldBe("octocat");
        payload.GetProperty("repo").GetString().ShouldBe("hello");
        payload.GetProperty("number").GetInt32().ShouldBe(42);
        payload.GetProperty("verdict").GetString().ShouldBe("approve");
        payload.GetProperty("commit").GetString().ShouldBe(Sha);
        payload.GetProperty("testsLine").GetString().ShouldBe("Tests: 3 checks passed.");
        var finding = payload.GetProperty("findings").EnumerateArray().ShouldHaveSingleItem();
        (finding.GetProperty("severity").GetString(), finding.GetProperty("path").GetString(), finding.GetProperty("line").GetInt32(), finding.GetProperty("title").GetString())
            .ShouldBe(("fix", "src/Checkout.cs", 3, "c is new"), "the worker gets the findings to compose the note from");
        payload.GetProperty("content").GetString()!.ShouldMatch("^[0-9a-f]{64}$", "the payload carries the digest of what it sends");
        payload.TryGetProperty("body", out _).ShouldBeFalse("a verdict carries findings, never a body");
        var logged = (await _writes.ListAsync(10, CancellationToken.None)).ShouldHaveSingleItem();
        logged.ShouldBe(logged with { FeatureId = "github", Action = "Pull request verdicts", Target = "octocat/hello#42", Outcome = "approved", ApprovedBy = "owner", ActionId = action.Id });
        (await File.ReadAllTextAsync(_writes.FilePath)).ShouldNotContain("c is new");
    }

    [Fact]
    public async Task A_Click_On_Post_Queues_The_Review_Already_Approved_On_The_Reviews_Row_With_Its_Comments_And_A_Second_Click_Cannot_Post_Again()
    {
        await GrantGitHubAsync();
        var post = Approve() with { Verdict = "comment", TestsLine = null };

        var first = await Verdicts().GiveAsync(post, CancellationToken.None);
        var second = await Verdicts().GiveAsync(post, CancellationToken.None);

        (first.FeatureId, first.Action, first.Target, first.Summary, first.Status)
            .ShouldBe(("github", "Pull request reviews", "octocat/hello#42", "Post a review on octocat/hello#42", PendingActionStatus.Approved));
        var payload = JsonDocument.Parse(first.Payload.ShouldNotBeNull()).RootElement;
        payload.GetProperty("verdict").GetString().ShouldBe("comment");
        payload.GetProperty("findings").EnumerateArray().ShouldHaveSingleItem().GetProperty("title").GetString().ShouldBe("c is new");
        second.Id.ShouldBe(first.Id, "an identical click cannot post again");
        (await _actions.ListOpenAsync("github", CancellationToken.None)).ShouldHaveSingleItem().Id.ShouldBe(first.Id);
        var logged = (await _writes.ListAsync(10, CancellationToken.None)).ShouldHaveSingleItem();
        logged.ShouldBe(logged with { FeatureId = "github", Action = "Pull request reviews", Target = "octocat/hello#42", Outcome = "approved", ApprovedBy = "owner", ActionId = first.Id });
        (await Verdicts().ReadAsync(first.Id, CancellationToken.None)).ShouldBe(new VerdictState("queued", "", "", "octocat/hello", 42, Sha, "comment"));
        (await Verdicts().FindAsync("octocat/hello", 42, Sha, CancellationToken.None)).ShouldNotBeNull().ActionId.ShouldBe(first.Id);
    }

    [Fact]
    public async Task A_Review_The_Model_Asked_For_Waits_For_The_Owner_And_Is_Neither_A_Card_s_Post_Nor_Replaced_By_One()
    {
        await GrantGitHubAsync();
        const string arguments = """{"body":"Two findings.","comments":"[]","number":"42","owner":"octocat","repo":"hello"}""";

        var asked = await Gate().RequestAsync(
            "github", "Pull request reviews", "GitHubActions.submit_pull_request_review", "submit_pull_request_review(owner=octocat, repo=hello, number=42)", arguments, CancellationToken.None);
        var clicked = await Verdicts().GiveAsync(Approve() with { Verdict = "comment", TestsLine = null }, CancellationToken.None);

        asked.Kind.ShouldBe(WriteDecisionKind.Queued);
        var waiting = (await _actions.FindAsync(asked.ActionId!, CancellationToken.None)).ShouldNotBeNull();
        (waiting.Status, waiting.Payload).ShouldBe((PendingActionStatus.Pending, arguments));
        clicked.Id.ShouldNotBe(waiting.Id);
        (await Verdicts().ReadAsync(waiting.Id, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Request_Changes_Is_Summarised_As_Such()
    {
        await GrantGitHubAsync();

        var action = await Verdicts().GiveAsync(Approve() with { Verdict = "request_changes" }, CancellationToken.None);

        action.Summary.ShouldBe("Request changes on octocat/hello#42");
    }

    [Fact]
    public async Task A_Different_Verdict_At_A_Commit_That_Already_Has_One_Is_Queued_And_Logged_As_Its_Own_Approval()
    {
        await GrantGitHubAsync();

        var first = await Verdicts().GiveAsync(Approve(), CancellationToken.None);
        var otherVerdict = await Verdicts().GiveAsync(Approve() with { Verdict = "request_changes" }, CancellationToken.None);

        otherVerdict.Id.ShouldNotBe(first.Id, "a different verdict at the same commit is its own action");
        (otherVerdict.Action, otherVerdict.Status).ShouldBe(("Pull request verdicts", PendingActionStatus.Approved));
        (await _writes.ListAsync(10, CancellationToken.None)).Select(e => (e.Outcome, e.ApprovedBy, e.ActionId))
            .ShouldBe([("approved", "owner", first.Id), ("approved", "owner", otherVerdict.Id)], ignoreOrder: true, customMessage: "each verdict is approved and logged on its own");
    }

    [Fact]
    public async Task The_Same_Verdict_With_Different_Comments_Is_Queued_Again()
    {
        await GrantGitHubAsync();

        var first = await Verdicts().GiveAsync(Approve(), CancellationToken.None);
        var otherComments = await Verdicts().GiveAsync(Approve(title: "Edited by the owner"), CancellationToken.None);
        var otherCommit = await Verdicts().GiveAsync(Approve(commit: "fedcba9"), CancellationToken.None);
        var otherPullRequest = await Verdicts().GiveAsync(Approve() with { Number = 43 }, CancellationToken.None);

        new[] { otherComments.Id, otherCommit.Id, otherPullRequest.Id }.ShouldNotContain(first.Id, "other comments, another commit or another pull request is its own action");
        (await _writes.ListAsync(10, CancellationToken.None)).Count.ShouldBe(4, "each one is approved and logged on its own");
    }

    [Fact]
    public async Task An_Identical_Request_Answers_The_Action_Already_There_Even_After_It_Ran()
    {
        await GrantGitHubAsync();
        var first = await Verdicts().GiveAsync(Approve(), CancellationToken.None);
        var again = await Verdicts().GiveAsync(Approve(), CancellationToken.None);
        await Gate().RecordAsync("github", "Pull request verdicts", "octocat/hello#42", "succeeded", first.Id, "https://github.com/r/88", CancellationToken.None);

        var afterItRan = await Verdicts().GiveAsync(Approve(), CancellationToken.None);

        (again.Id, afterItRan.Id).ShouldBe((first.Id, first.Id), "an identical request is ignored, before and after it ran");
        (await _writes.ListAsync(10, CancellationToken.None)).Count(e => e.Outcome == "approved").ShouldBe(1, "one approval is logged");
    }

    [Fact]
    public async Task An_Action_Without_A_Content_Digest_Is_Never_Identical()
    {
        await GrantGitHubAsync();
        var old = await _actions.EnqueueApprovedOnceAsync(
            "github", "Pull request verdicts", "octocat/hello#42", "Approve octocat/hello#42",
            $$$"""{"owner":"octocat","repo":"hello","number":42,"verdict":"approve","commit":"{{{Sha}}}"}""", _ => false, CancellationToken.None);
        await _actions.ExecuteAsync(old.Action.Id, "succeeded", "https://github.com/r/1", _ => Task.CompletedTask, CancellationToken.None);

        var again = await Verdicts().GiveAsync(Approve(), CancellationToken.None);

        again.Id.ShouldNotBe(old.Action.Id, "an action queued before the digest existed is never the same as a new request");
    }

    [Fact]
    public async Task A_Verdict_Being_Sent_Or_Already_Given_Is_The_One_Answered_Again()
    {
        await GrantGitHubAsync();
        var running = await Verdicts().GiveAsync(Approve(), CancellationToken.None);
        await _actions.ClaimNextAsync("github", TimeSpan.FromMinutes(30), (_, _) => true, CancellationToken.None);
        var whileRunning = await Verdicts().GiveAsync(Approve(), CancellationToken.None);
        await _actions.ExecuteAsync(running.Id, "succeeded", "https://github.com/octocat/hello/pull/42#pullrequestreview-88", _ => Task.CompletedTask, CancellationToken.None);

        var afterGiven = await Verdicts().GiveAsync(Approve(), CancellationToken.None);

        (whileRunning.Id, afterGiven.Id).ShouldBe((running.Id, running.Id));
    }

    [Fact]
    public async Task A_Verdict_That_Failed_Or_Was_Rejected_May_Be_Given_Again()
    {
        await GrantGitHubAsync();
        var failed = await Verdicts().GiveAsync(Approve(), CancellationToken.None);
        await _actions.ExecuteAsync(failed.Id, "failed: github 422", "GitHub rejected the write.", _ => Task.CompletedTask, CancellationToken.None);

        var again = await Verdicts().GiveAsync(Approve(), CancellationToken.None);

        again.Id.ShouldNotBe(failed.Id);
        again.Status.ShouldBe(PendingActionStatus.Approved);
    }

    [Fact]
    public async Task Nothing_Is_Queued_While_The_Owner_Has_Not_Allowed_GitHub_Writes()
    {
        var ex = await Should.ThrowAsync<WriteNotGrantedException>(() => Verdicts().GiveAsync(Approve(), CancellationToken.None));

        ex.Message.ShouldBe("The owner has not allowed github to write; allow it on the Features screen of Scout Desktop.");
        (await _actions.ListOpenAsync("github", CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Malformed_Or_Oversized_Verdict_Is_Refused_Before_Anything_Is_Queued()
    {
        await GrantGitHubAsync();
        var bad = new PullRequestVerdictRequest("octo cat", "hello", 0, "merge", [Finding() with { Path = "", Line = 0 }], "not-a-sha", null);
        var huge = Approve(title: new string('x', 200_001));

        var invalid = await Should.ThrowAsync<FeatureValidationFailedException>(() => Verdicts().GiveAsync(bad, CancellationToken.None));
        var tooLarge = await Should.ThrowAsync<FeatureValidationFailedException>(() => Verdicts().GiveAsync(huge, CancellationToken.None));

        invalid.Errors.Keys.Order(StringComparer.Ordinal).ShouldBe(["commit", "findings", "number", "repository", "verdict"]);
        tooLarge.Errors.ShouldBe(new Dictionary<string, string> { ["payload"] = "The verdict is over 200000 characters; keep fewer findings." });
        (await _actions.ListOpenAsync("github", CancellationToken.None)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("octocat\n", "hello", Sha, "repository")]
    [InlineData("octocat", "hello\n", Sha, "repository")]
    [InlineData("octocat", "hello", Sha + "\n", "commit")]
    public async Task A_Repository_Or_Commit_With_A_Trailing_Newline_Is_Refused_Before_Anything_Is_Queued(
        string owner, string repo, string commit, string refused)
    {
        await GrantGitHubAsync();
        var request = Approve() with { Owner = owner, Repo = repo, Commit = commit };

        var ex = await Should.ThrowAsync<FeatureValidationFailedException>(() => Verdicts().GiveAsync(request, CancellationToken.None));

        ex.Errors.Keys.ShouldBe([refused]);
        (await _actions.ListOpenAsync("github", CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Verdict_Asked_In_Chat_Is_Queued_With_The_Function_s_Arguments_As_Its_Payload()
    {
        await GrantGitHubAsync();
        const string arguments = """{"body":"Ready.","comments_json":"[]","commit":"0123456","number":"42","owner":"octocat","repo":"hello","verdict":"approve"}""";

        var decision = await Gate().RequestAsync("github", "Pull request verdicts", "GitHubActions.give_pull_request_verdict", "give_pull_request_verdict(...)", arguments, CancellationToken.None);

        decision.Kind.ShouldBe(WriteDecisionKind.Queued);
        (await _actions.FindAsync(decision.ActionId!, CancellationToken.None)).ShouldNotBeNull().Payload.ShouldBe(arguments);
    }

    [Fact]
    public async Task A_Write_That_Runs_Now_Keeps_No_Payload_However_Large()
    {
        await GrantGitHubAsync();

        var decision = await Gate().RequestAsync("github", "Scout's data folder", "GitHubActions.snapshot_repository", "snapshot_repository(...)", new string('x', 300_000), CancellationToken.None);

        decision.Kind.ShouldBe(WriteDecisionKind.Allowed);
        (await _actions.ListPendingAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_Irreversible_Write_Whose_Payload_Is_Over_200000_Characters_Is_Refused()
    {
        await GrantGitHubAsync();

        var decision = await Gate().RequestAsync("github", "Pull request verdicts", "t", "s", new string('x', 200_001), CancellationToken.None);

        decision.ShouldBe(new WriteDecision(WriteDecisionKind.Refused, WriteGateService.PayloadTooLarge));
        (await _actions.ListPendingAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Recording_A_Verdict_Keeps_Its_Outcome_And_Link_On_The_Action_Where_The_Desktop_Reads_Them()
    {
        await GrantGitHubAsync();
        var action = await Verdicts().GiveAsync(Approve(), CancellationToken.None);

        await Gate().RecordAsync("github", "Pull request verdicts", "octocat/hello#42", "succeeded", action.Id, "https://github.com/octocat/hello/pull/42#pullrequestreview-88", CancellationToken.None);

        var recorded = (await _actions.FindAsync(action.Id, CancellationToken.None)).ShouldNotBeNull();
        (recorded.Status, recorded.Outcome, recorded.Result)
            .ShouldBe((PendingActionStatus.Executed, "succeeded", "https://github.com/octocat/hello/pull/42#pullrequestreview-88"));
    }

    [Theory]
    [InlineData("succeeded, tests line posted as a comment", "posted")]
    [InlineData("succeeded, tests line not posted", "notPosted")]
    [InlineData("succeeded", "none")]
    [InlineData("succeeded, tests line posted as a comment after revoke", "posted")]
    public async Task A_Given_Verdict_Reads_Who_Signed_It_And_What_Became_Of_Its_Tests_Line_And_One_Not_Given_Yet_Reads_Neither(
        string outcome, string testsLine)
    {
        const string ReviewUrl = "https://github.com/octocat/hello/pull/42#pullrequestreview-88";
        await GrantGitHubAsync();
        var given = await Verdicts().GiveAsync(Approve(), CancellationToken.None);
        var queued = (await Verdicts().ReadAsync(given.Id, CancellationToken.None)).ShouldNotBeNull();

        await Gate().RecordAsync("github", "Pull request verdicts", "octocat/hello#42", outcome, given.Id, ReviewUrl, "scout-bot", CancellationToken.None);

        var expected = new VerdictState("given", ReviewUrl, "", "octocat/hello", 42, Sha, "approve", "scout-bot", testsLine);
        (queued.SignedAs, queued.TestsLine).ShouldBe(("", ""), "a verdict not given yet answers signed as and tests line empty");
        (await Verdicts().ReadAsync(given.Id, CancellationToken.None)).ShouldBe(
            expected, "a given verdict answers the login the write log keeps for it and its tests line as posted, notPosted or none");
        (await Verdicts().FindAsync("octocat/hello", 42, Sha, CancellationToken.None)).ShouldNotBeNull().State.ShouldBe(
            expected, "the verdict found by its commit answers the same signer and tests line");
    }

    [Fact]
    public async Task A_Failed_Verdict_Reads_No_Signer_And_No_Tests_Line()
    {
        await GrantGitHubAsync();
        var given = await Verdicts().GiveAsync(Approve(), CancellationToken.None);

        await Gate().RecordAsync("github", "Pull request verdicts", "octocat/hello#42", "failed: github 403", given.Id, "GitHub denied the request.", "scout-bot", CancellationToken.None);

        (await Verdicts().ReadAsync(given.Id, CancellationToken.None)).ShouldBe(
            new VerdictState("failed", "", "GitHub denied the request.", "octocat/hello", 42, Sha, "approve"),
            "a verdict that was not given answers signed as and tests line empty");
    }

    [Fact]
    public async Task The_Payload_Survives_A_Restart_Of_The_Queue()
    {
        await GrantGitHubAsync();
        var action = await Verdicts().GiveAsync(Approve(), CancellationToken.None);
        var options = Options.Create(new FeatureStateStoreOptions { FilePath = Path.Combine(_directory, "features.json") });
        using var reopened = new JsonFileActionQueue(options, NullLogger<JsonFileActionQueue>.Instance);

        (await reopened.FindAsync(action.Id, CancellationToken.None)).ShouldNotBeNull().Payload.ShouldBe(action.Payload);
    }

    [Fact]
    public async Task The_Queue_Refuses_A_Payload_Over_200000_Characters()
    {
        var ex = await Should.ThrowAsync<ArgumentException>(() => _actions.EnqueueAsync("github", "Pull request verdicts", "t", "s", new string('x', 200_001), CancellationToken.None));

        ex.ParamName.ShouldBe("payload");
    }

    [Fact]
    public async Task A_Verdict_Asked_In_Chat_Reads_Queued_Until_Approved_And_Failed_With_The_Owner_s_Reason_When_Rejected()
    {
        await GrantGitHubAsync();
        var waiting = await _actions.EnqueueAsync("github", "Pull request verdicts", "GitHubActions.give_pull_request_verdict", "s", "{}", CancellationToken.None);
        var rejected = await _actions.EnqueueAsync("github", "Pull request verdicts", "GitHubActions.give_pull_request_verdict", "s", "{}", CancellationToken.None);
        await _actions.RejectAsync(rejected.Id, "Not on a Friday.", CancellationToken.None);
        var issue = await _actions.EnqueueAsync("github", "New issues", "o/r", "s", CancellationToken.None);

        (await Verdicts().ReadAsync(waiting.Id, CancellationToken.None)).ShouldBe(new VerdictState("queued", "", "", "", 0, "", ""));
        (await Verdicts().ReadAsync(rejected.Id, CancellationToken.None)).ShouldBe(new VerdictState("failed", "", "Not on a Friday.", "", 0, "", ""));
        (await Verdicts().ReadAsync(issue.Id, CancellationToken.None)).ShouldBeNull();
        (await Verdicts().ReadAsync("unknown", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task A_Verdict_Reads_Back_With_The_Pull_Request_Commit_And_Verdict_It_Is_On_From_Either_Way_In()
    {
        await GrantGitHubAsync();
        var clicked = await Verdicts().GiveAsync(Approve(), CancellationToken.None);
        var asked = await _actions.EnqueueAsync("github", "Pull request verdicts", "GitHubActions.give_pull_request_verdict", "s",
            """{"commit":"0123456","number":"7","owner":"octocat","repo":"shop","verdict":"request_changes"}""", CancellationToken.None);

        (await Verdicts().ReadAsync(clicked.Id, CancellationToken.None)).ShouldBe(new VerdictState("queued", "", "", "octocat/hello", 42, Sha, "approve"));
        (await Verdicts().ReadAsync(asked.Id, CancellationToken.None)).ShouldBe(new VerdictState("queued", "", "", "octocat/shop", 7, "0123456", "request_changes"));
    }

    [Fact]
    public async Task A_Verdict_Left_Behind_By_A_Revoke_And_Re_Grant_Reads_Failed_And_A_New_Click_Queues_A_New_One()
    {
        await GrantGitHubAsync();
        var before = await Verdicts().GiveAsync(Approve(), CancellationToken.None);
        var features = new FeaturesService(new FeatureCatalog(), _states, new InMemoryCredentialStore(), new NullFeatureActivator(), Array.Empty<IFeatureCredentialValidator>());
        await features.SetWriteGrantAsync(FeatureCatalog.GitHubId, granted: false, confirmWrites: true, CancellationToken.None);
        var whileRevoked = await Verdicts().ReadAsync(before.Id, CancellationToken.None);
        await Task.Delay(10);
        await GrantGitHubAsync();

        var again = await Verdicts().GiveAsync(Approve(), CancellationToken.None);

        whileRevoked.ShouldNotBeNull().State.ShouldBe("failed");
        again.Id.ShouldNotBe(before.Id);
        (await Verdicts().ReadAsync(before.Id, CancellationToken.None)).ShouldNotBeNull().Reason
            .ShouldBe("GitHub's write permission changed after this verdict was given, so it will not be sent; give it again.");
        (await Verdicts().ReadAsync(again.Id, CancellationToken.None)).ShouldNotBeNull().State.ShouldBe("queued");
    }

    [Theory]
    [InlineData("request_changes")]
    [InlineData("approve")]
    public async Task A_Click_On_A_Commit_Whose_Chat_Verdict_Waits_Replaces_It_With_Exactly_The_Card_s_Verdict(string asked)
    {
        await GrantGitHubAsync();
        var arguments = $$$"""{"commit":"{{{Sha}}}","number":"42","owner":"octocat","repo":"hello","verdict":"{{{asked}}}","findings_json":"[]"}""";
        var waiting = await Gate().RequestAsync("github", "Pull request verdicts", "GitHubActions.give_pull_request_verdict", "s", arguments, CancellationToken.None);

        var clicked = await Verdicts().GiveAsync(Approve(), CancellationToken.None);

        var chat = (await _actions.FindAsync(waiting.ActionId!, CancellationToken.None)).ShouldNotBeNull();
        (chat.Status, chat.Reason).ShouldBe((PendingActionStatus.Rejected, "Replaced by the owner's verdict from the review card."));
        clicked.Id.ShouldNotBe(waiting.ActionId);
        (clicked.Status, clicked.Summary, clicked.Target).ShouldBe((PendingActionStatus.Approved, "Approve octocat/hello#42", "octocat/hello#42"));
        var runs = JsonDocument.Parse(clicked.Payload!).RootElement;
        (runs.GetProperty("verdict").GetString(), runs.GetProperty("testsLine").GetString())
            .ShouldBe(("approve", "Tests: 3 checks passed."));
        runs.GetProperty("findings").EnumerateArray().ShouldHaveSingleItem().GetProperty("title").GetString().ShouldBe("c is new");
        (await _writes.ListAsync(10, CancellationToken.None)).Select(e => (e.Outcome, e.ActionId)).ShouldBe([
            ("approved", clicked.Id),
            ("rejected: Replaced by the owner's verdict from the review card.", waiting.ActionId),
        ]);
    }

    [Fact]
    public async Task A_Click_Leaves_A_Chat_Verdict_On_Another_Commit_Waiting()
    {
        await GrantGitHubAsync();
        var waiting = await Gate().RequestAsync("github", "Pull request verdicts", "t", "s",
            """{"commit":"fedcba9","number":"42","owner":"octocat","repo":"hello","verdict":"approve"}""", CancellationToken.None);

        var clicked = await Verdicts().GiveAsync(Approve(), CancellationToken.None);

        clicked.Id.ShouldNotBe(waiting.ActionId);
        (await _actions.FindAsync(waiting.ActionId!, CancellationToken.None)).ShouldNotBeNull().Status.ShouldBe(PendingActionStatus.Pending);
    }

    [Fact]
    public async Task An_Executed_Verdict_Keeps_Only_Which_Pull_Request_Commit_And_Verdict_It_Was_And_Is_Still_Found_By_Them()
    {
        await GrantGitHubAsync();
        var given = await Verdicts().GiveAsync(Approve(), CancellationToken.None);

        await Gate().RecordAsync("github", "Pull request verdicts", "octocat/hello#42", "succeeded", given.Id, "https://github.com/r/88", CancellationToken.None);

        var executed = (await _actions.FindAsync(given.Id, CancellationToken.None)).ShouldNotBeNull();
        executed.Payload.ShouldNotBeNull().ShouldMatch(
            $$$"""^\{"owner":"octocat","repo":"hello","number":42,"verdict":"approve","commit":"{{{Sha}}}","content":"[0-9a-f]{64}"\}$""",
            "an executed verdict keeps which pull request, commit and verdict it was, and its content digest");
        (await File.ReadAllTextAsync(_actions.FilePath)).ShouldNotContain("c is new");
        (await Verdicts().FindAsync("octocat/hello", 42, Sha, CancellationToken.None)).ShouldNotBeNull().ActionId.ShouldBe(given.Id);
    }

    [Fact]
    public async Task A_Verdict_Left_Approved_By_A_Re_Grant_Is_Rejected_At_The_Next_Click_So_It_Ages_Out()
    {
        await GrantGitHubAsync();
        var before = await Verdicts().GiveAsync(Approve(), CancellationToken.None);
        var features = new FeaturesService(new FeatureCatalog(), _states, new InMemoryCredentialStore(), new NullFeatureActivator(), Array.Empty<IFeatureCredentialValidator>());
        await features.SetWriteGrantAsync(FeatureCatalog.GitHubId, granted: false, confirmWrites: true, CancellationToken.None);
        await Task.Delay(10);
        await GrantGitHubAsync();

        await Verdicts().GiveAsync(Approve(commit: "fedcba9"), CancellationToken.None);

        var old = (await _actions.FindAsync(before.Id, CancellationToken.None)).ShouldNotBeNull();
        (old.Status, old.Reason).ShouldBe((PendingActionStatus.Rejected, "GitHub's write permission changed after this verdict was given, so it will not be sent; give it again."));
        (await _actions.ListOpenAsync("github", CancellationToken.None)).ShouldNotContain(a => a.Id == before.Id);
    }

    [Fact]
    public async Task A_Null_Or_Incomplete_Finding_Is_Refused_Before_Anything_Is_Queued()
    {
        await GrantGitHubAsync();
        string[] cards =
        [
            "[null]",
            """[{"id":"a","severity":"major","title":"t","path":"a.cs","line":1,"why":"w","fix":"f"}]""",
            """[{"id":"a","severity":"fix","title":"t","path":"a.cs","line":1,"why":"w"}]""",
            """[{"id":"","severity":"fix","title":"t","path":"a.cs","line":1,"why":"w","fix":"f"}]""",
        ];

        foreach (var findings in cards)
        {
            var invalid = await Should.ThrowAsync<FeatureValidationFailedException>(
                () => Verdicts().GiveAsync(PullRequestVerdictRequest.FromCard("octocat/hello", 42, "approve", findings, Sha, null), CancellationToken.None));
            invalid.Errors.ShouldBe(
                new Dictionary<string, string> { ["findings"] = "Every finding needs an id, a severity of block, fix or nit, a title, a path, a positive line, a why and a fix." },
                $"{findings} is refused");
        }

        (await _actions.ListOpenAsync("github", CancellationToken.None)).ShouldBeEmpty("nothing is queued");
    }

    [Fact]
    public async Task Findings_That_Share_An_Id_Are_Refused_Before_Anything_Is_Queued()
    {
        await GrantGitHubAsync();
        const string Findings = """[{"id":"a","severity":"fix","title":"t","path":"a.cs","line":1,"why":"w","fix":"f"},{"id":"a","severity":"nit","title":"u","path":"b.cs","line":2,"why":"w","fix":"f"}]""";

        var invalid = await Should.ThrowAsync<FeatureValidationFailedException>(
            () => Verdicts().GiveAsync(PullRequestVerdictRequest.FromCard("octocat/hello", 42, "approve", Findings, Sha, null), CancellationToken.None));

        invalid.Errors.ShouldBe(
            new Dictionary<string, string> { ["findings"] = "Each finding needs its own id; two findings share one." },
            "two findings with one id are refused with their own sentence");
        (await _actions.ListOpenAsync("github", CancellationToken.None)).ShouldBeEmpty("nothing is queued for the owner to approve");
    }

    [Fact]
    public async Task Counts_That_Do_Not_Come_Together_Or_Notes_Over_The_Bounds_Are_Refused_Before_Anything_Is_Queued()
    {
        await GrantGitHubAsync();

        var oneCount = await Should.ThrowAsync<FeatureValidationFailedException>(() => Verdicts().GiveAsync(Approve() with { FilesRead = 3 }, CancellationToken.None));
        var moreReadThanChanged = await Should.ThrowAsync<FeatureValidationFailedException>(() => Verdicts().GiveAsync(Approve() with { FilesRead = 4, FilesChanged = 3 }, CancellationToken.None));
        var tooManyNotes = await Should.ThrowAsync<FeatureValidationFailedException>(() => Verdicts().GiveAsync(Approve() with { Notes = [.. Enumerable.Repeat("n", 21)] }, CancellationToken.None));
        var longNote = await Should.ThrowAsync<FeatureValidationFailedException>(() => Verdicts().GiveAsync(Approve() with { Notes = [new string('n', 301)] }, CancellationToken.None));

        new[] { oneCount, moreReadThanChanged, tooManyNotes, longNote }.Select(e => string.Join(",", e.Errors.Keys)).ShouldBe(["files", "files", "notes", "notes"]);
        (await _actions.ListOpenAsync("github", CancellationToken.None)).ShouldBeEmpty("nothing is queued");
        (await Verdicts().GiveAsync(Approve() with { FilesRead = 3, FilesChanged = 3, Notes = ["2 files not read."] }, CancellationToken.None)).Status
            .ShouldBe(PendingActionStatus.Approved, "counts that come together and notes within the bounds are queued");
    }

    [Fact]
    public async Task A_Verdict_Asked_In_Chat_Lists_Its_Findings_For_The_Owner()
    {
        await GrantGitHubAsync();
        const string arguments = """{"findings_json":"[{\"id\":\"a\",\"severity\":\"fix\",\"title\":\"Move has no lock\"}]","commit":"0123456","number":"42","owner":"octocat","repo":"hello","verdict":"approve"}""";

        var decision = await Gate().RequestAsync("github", "Pull request verdicts", "GitHubActions.give_pull_request_verdict", "give_pull_request_verdict(...)", arguments, CancellationToken.None);

        var waiting = (await _actions.FindAsync(decision.ActionId!, CancellationToken.None)).ShouldNotBeNull();
        waiting.ListedText().Body.ShouldBe("""[{"id":"a","severity":"fix","title":"Move has no lock"}]""", "the owner reads what was queued: its findings");
    }

    [Fact]
    public async Task A_Chat_Verdict_A_Card_Click_Replaces_Is_Logged_As_Rejected_With_The_Group_Of_The_Request_That_Asked_It()
    {
        await GrantGitHubAsync();
        var arguments = $$$"""{"commit":"{{{Sha}}}","number":"42","owner":"octocat","repo":"hello","verdict":"approve","body":"b","comments_json":"[]"}""";
        var waiting = await Gate().RequestAsync(
            "github", "Pull request verdicts", "GitHubActions.give_pull_request_verdict", "s", arguments, "turn-7", "octocat/hello", CancellationToken.None);

        await Verdicts().GiveAsync(Approve(), CancellationToken.None);

        (await _writes.ListAsync(10, CancellationToken.None)).Where(e => e.ActionId == waiting.ActionId).Select(e => (e.Outcome, e.GroupId))
            .ShouldBe([("rejected: Replaced by the owner's verdict from the review card.", "turn-7")]);
    }
}
