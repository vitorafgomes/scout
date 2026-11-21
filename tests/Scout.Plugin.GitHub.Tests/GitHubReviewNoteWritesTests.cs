using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute.ExceptionExtensions;
using Scout.Llm.Abstractions;
using Scout.Plugin.GitHub.Abstractions;
using Scout.Review;

namespace Scout.Plugin.GitHub.Tests;

public sealed partial class GitHubReviewNoteWritesTests : IDisposable
{
    private const string Sha = "abc1234def5678";
    private const string Patch = "@@ -1,3 +1,4 @@\n a\n-b\n+c\n+d\n e";
    private const string Url = "https://github.com/octocat/hello/pull/42#pullrequestreview-88";

    private readonly string _dir = Directory.CreateTempSubdirectory("scout-github-note-").FullName;
    private readonly IGitHubClient _reads = Substitute.For<IGitHubClient>();
    private readonly IGitHubWriteClient _writes = Substitute.For<IGitHubWriteClient>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 19, 10, 0, 0, TimeSpan.Zero));

    public GitHubReviewNoteWritesTests()
    {
        _writes.SignerFor(default, default!, default!).ReturnsForAnyArgs(new GitHubSigner(false, null, false));
        _reads.GetPullRequestHeadAsync("octocat", "hello", 42, Arg.Any<CancellationToken>()).Returns(new PullRequestHead(Sha, "alice", Mergeable: true));
        _reads.ListPullRequestFilesAsync("octocat", "hello", 42, Arg.Any<CancellationToken>())
            .Returns(new List<ChangedFile> { new("src/Checkout.cs", "modified", 2, 1, Patch) });
        _reads.GetSignedInLoginAsync(Arg.Any<CancellationToken>()).Returns("octocat");
        _reads.ListReviewsAsync("octocat", "hello", 42, Arg.Any<CancellationToken>()).Returns(new List<PullRequestReview>());
        _reads.ListCheckRunsAsync("octocat", "hello", Sha, Arg.Any<CancellationToken>())
            .Returns(new CheckRuns([new PullRequestCheck("build", "completed", "success", "https://ci/1")], 1));
        _reads.ListStatusesAsync("octocat", "hello", Sha, Arg.Any<CancellationToken>()).Returns([]);
        _writes.SubmitVerdictAsync(default!, default!, default, default, default!, default!, default!, default).ReturnsForAnyArgs(new PostedReview(88, Url));
        _writes.SubmitReviewAsync(default!, default!, default, default!, default!, default).ReturnsForAnyArgs(new PostedReview(88, Url));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private GitHubActionsPlugin Plugin() => new(
        _reads, _writes, new GitHubProfileStore(Path.Combine(_dir, "profiles")), new RepositorySnapshotStore(Path.Combine(_dir, "snapshots"), 0, _time),
        Substitute.For<IRepositoryProjects>(), _time);

    private static string Finding(string id, string severity, int line, string title = "A title", string why = "Why.", string fix = "Fix.", string path = "src/Checkout.cs", string? context = null) =>
        context is null
            ? JsonSerializer.Serialize(new { id, severity, title, path, line, why, fix })
            : JsonSerializer.Serialize(new { id, severity, title, path, line, context, why, fix });

    private static string Findings(params string[] findings) => "[" + string.Join(',', findings) + "]";

    [GeneratedRegex(@"\A<!-- scout-review [0-9a-f]{16} -->\z")]
    private static partial Regex MarkerLine();

    private (string Body, IReadOnlyList<ReviewComment> Comments) SentVerdict()
    {
        var call = _writes.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IGitHubWriteClient.SubmitVerdictAsync)).GetArguments();
        return ((string)call[4]!, (IReadOnlyList<ReviewComment>)call[5]!);
    }

    private (string Body, IReadOnlyList<ReviewComment> Comments) SentReview()
    {
        var call = _writes.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IGitHubWriteClient.SubmitReviewAsync)).GetArguments();
        return ((string)call[3]!, (IReadOnlyList<ReviewComment>)call[4]!);
    }

    private bool NothingSent() =>
        !_writes.ReceivedCalls().Any(c => c.GetMethodInfo().Name is nameof(IGitHubWriteClient.SubmitVerdictAsync) or nameof(IGitHubWriteClient.SubmitReviewAsync));

    [Fact]
    public async Task An_Approval_Is_Refused_Because_Scout_Does_Not_Approve_On_Its_Own()
    {
        var refused = await Should.ThrowAsync<WriteFailedException>(() =>
            Plugin().GivePullRequestVerdictAsync("octocat", "hello", 42, "approve", "[]", "abc1234", CancellationToken.None));

        refused.Message.ShouldBe(
            $"{GitHubActionsPlugin.NotOffered} Comment, not approve: every changed file was read, nothing needs fixing, and every check passed, which covers the tests, since no test ran here. {ReviewOffers.NotOnItsOwn}",
            "a clean review is never approved through Scout: a person approves it on GitHub");
        NothingSent().ShouldBeTrue("nothing is written on GitHub");
    }

    [Fact]
    public async Task A_Request_For_Changes_Is_Sent_With_One_Composed_Comment_For_Each_Finding()
    {
        await Plugin().GivePullRequestVerdictAsync(
            "octocat", "hello", 42, "request_changes", Findings(Finding("a", "block", 3, "Move has no lock"), Finding("b", "fix", 4, "Retry never ends")), "abc1234", CancellationToken.None);

        SentVerdict().Comments.ShouldBe(
        [
            new ReviewComment("src/Checkout.cs", 3, ":no_entry: **Blocking** · Move has no lock\n\n**What is wrong:** Why.\n\n**Proposed fix:** Fix."),
            new ReviewComment("src/Checkout.cs", 4, ":wrench: **Fix** · Retry never ends\n\n**What is wrong:** Why.\n\n**Proposed fix:** Fix."),
        ], "one comment composed by code for each finding, on its line");
        SentVerdict().Body.Split('\n')[0].ShouldBe("### :robot: Scout review: changes requested");
    }

    [Fact]
    public async Task A_Commenting_Review_Is_Sent_With_One_Composed_Comment_For_Each_Suggestion()
    {
        await Plugin().SubmitPullRequestReviewAsync("octocat", "hello", 42, Findings(Finding("a", "nit", 3, "Name the limit"), Finding("b", "nit", 4, "Say the unit")), CancellationToken.None);

        var (body, comments) = SentReview();
        body.Split('\n')[0].ShouldBe("### :robot: Scout review: commented, not approved", "a review asked in chat is a commenting one");
        comments.Select(c => (c.Line, c.Body.Split('\n')[0])).ShouldBe([(3, ":bulb: **Suggestion** · Name the limit"), (4, ":bulb: **Suggestion** · Say the unit")], "one comment for each suggestion");
        body.ShouldNotContain("scout-review", customMessage: "a commenting review carries no marker");
    }

    [Fact]
    public async Task Each_Comment_Is_Anchored_To_Its_Findings_Line_And_One_Outside_The_Diff_Is_Dropped_And_Named()
    {
        var answer = await Plugin().GivePullRequestVerdictAsync(
            "octocat", "hello", 42, "request_changes", Findings(Finding("a", "block", 3), Finding("b", "fix", 9), Finding("c", "fix", 1, path: "src/Other.cs")), "abc1234", CancellationToken.None);

        answer.ShouldBe($"Requested changes on octocat/hello#42 with 1 comment(s): {Url} Dropped, not on a line of the diff: src/Checkout.cs:9, src/Other.cs:1.");
        SentVerdict().Comments.Select(c => (c.Path, c.Line)).ShouldBe([("src/Checkout.cs", 3)], "only the finding on a line of the diff is a comment");
        SentVerdict().Body.ShouldContain("1 blocking, 2 to fix, 0 optional.\n", customMessage: "the note still lists every finding, and no longer says each one is a comment");
    }

    [Fact]
    public async Task A_Verdict_Whose_Note_Is_Already_On_The_Commit_Is_Not_Sent_Again()
    {
        var findings = Findings(Finding("a", "block", 3));
        _reads.ListReviewsAsync("octocat", "hello", 42, Arg.Any<CancellationToken>()).Returns(new List<PullRequestReview>
        {
            new(5, "octocat", "CHANGES_REQUESTED", Sha, "https://github.com/octocat/hello/pull/42#pullrequestreview-5", "the note\n\n" + ReviewNote.Marker(ReviewNote.Digest(ReviewNote.RequestChanges, ReviewFindings.ParseFindings(findings, "x").Findings!, null, null, null))),
        });

        var answer = await Plugin().GivePullRequestVerdictAsync("octocat", "hello", 42, "request_changes", findings, "abc1234", CancellationToken.None);

        answer.ShouldBe("Requested changes on octocat/hello#42 already, at this commit: https://github.com/octocat/hello/pull/42#pullrequestreview-5");
        NothingSent().ShouldBeTrue("a note already on the commit is not sent a second time");
    }

    [Fact]
    public async Task The_Same_Verdict_With_Other_Findings_Is_Sent_Though_One_Is_Already_On_The_Commit()
    {
        _reads.ListReviewsAsync("octocat", "hello", 42, Arg.Any<CancellationToken>()).Returns(new List<PullRequestReview>
        {
            new(5, "octocat", "CHANGES_REQUESTED", Sha, "https://github.com/octocat/hello/pull/42#pullrequestreview-5", "the note\n\n" + ReviewNote.Marker("0123456789abcdef")),
        });

        var answer = await Plugin().GivePullRequestVerdictAsync("octocat", "hello", 42, "request_changes", Findings(Finding("a", "block", 3)), "abc1234", CancellationToken.None);

        answer.ShouldStartWith("Requested changes on octocat/hello#42 with 1 comment(s): ", customMessage: "a note with other findings at the same commit is sent");
        NothingSent().ShouldBeFalse();
    }

    [Theory]
    [InlineData("server error")]
    [InlineData("timeout")]
    public async Task A_Review_GitHub_Fails_With_A_Server_Error_Or_A_Timeout_Is_Reported_As_Failed_And_May_Already_Be_Posted(string failure)
    {
        Exception ex = failure == "timeout" ? new TaskCanceledException("timed out") : new GitHubApiException("GitHub is currently unavailable. Try again later.", HttpStatusCode.BadGateway);
        _writes.SubmitReviewAsync(default!, default!, default, default!, default!, default).ThrowsAsyncForAnyArgs(ex);

        var failed = await Should.ThrowAsync<WriteFailedException>(() => Plugin().SubmitPullRequestReviewAsync("octocat", "hello", 42, Findings(Finding("a", "nit", 3)), CancellationToken.None));

        (failed.Message, failed.Outcome).ShouldBe(failure == "timeout"
            ? ("GitHub request timed out. Try again later." + GitHubActionsPlugin.MayAlreadyBePosted, "TaskCanceledException")
            : ("GitHub is currently unavailable. Try again later.", "github 502"), "a failed post is reported with its reason, and a lost answer may hide a review already posted");
    }

    [Fact]
    public async Task A_Review_Whose_Head_Cannot_Be_Read_Sends_Nothing()
    {
        _reads.GetPullRequestHeadAsync("octocat", "hello", 42, Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("connection reset"));

        var failed = await Should.ThrowAsync<WriteFailedException>(() => Plugin().SubmitPullRequestReviewAsync("octocat", "hello", 42, Findings(Finding("a", "nit", 3)), CancellationToken.None));

        failed.Message.ShouldBe("Could not reach GitHub. Try again later.", "a head read that fails never says the review may be posted");
        NothingSent().ShouldBeTrue("nothing is sent when the head cannot be read");
    }

    [Fact]
    public async Task A_Note_Whose_Checks_Cannot_Be_Read_Is_Sent_Without_The_Checks()
    {
        _reads.ListCheckRunsAsync("octocat", "hello", Sha, Arg.Any<CancellationToken>()).ThrowsAsync(new GitHubApiException("GitHub is currently unavailable. Try again later.", HttpStatusCode.BadGateway));

        await Plugin().GivePullRequestVerdictAsync("octocat", "hello", 42, "request_changes", Findings(Finding("a", "block", 3)), "abc1234", CancellationToken.None);

        var body = SentVerdict().Body;
        (body.Split('\n')[2], body.Contains("#### :vertical_traffic_light:", StringComparison.Ordinal)).ShouldBe(
            ("> :x: **Changes requested** · findings: 1 blocking, 0 to fix, 0 optional", false), "checks that cannot be read are left out of the note, which is still sent");
    }

    [Fact]
    public async Task A_Verdict_Reads_The_Checks_Once_And_Its_Note_Carries_Them()
    {
        await Plugin().GivePullRequestVerdictAsync("octocat", "hello", 42, "request_changes", Findings(Finding("a", "fix", 3)), "abc1234", CancellationToken.None);

        await _reads.Received(1).ListCheckRunsAsync("octocat", "hello", Sha, Arg.Any<CancellationToken>());
        SentVerdict().Body.ShouldContain(":white_check_mark: 1 passed, :x: 0 failed, :fast_forward: 0 skipped, of 1 on `abc1234`.", customMessage: "the checks the verdict read are the note's");
    }

    [Fact]
    public async Task A_Note_Asked_In_Chat_Carries_The_Checks_Scout_Read_Not_Anything_The_Model_Wrote()
    {
        var findings = "[" + Finding("a", "nit", 3)[..^1] + ""","checks":"5 of 5 passed","read":"9 of 9 files read"}]""";

        await Plugin().GivePullRequestVerdictAsync("octocat", "hello", 42, "request_changes", findings, "abc1234", CancellationToken.None);

        var body = SentVerdict().Body;
        (body.Split('\n')[2], body.Contains("9 of 9", StringComparison.Ordinal), body.Contains("5 of 5", StringComparison.Ordinal)).ShouldBe(
            ("> :x: **Changes requested** · checks: 1 of 1 passed · findings: 0 blocking, 0 to fix, 1 optional", false, false),
            "the counts in the note are the ones Scout read, never what the model wrote");
    }

    [Fact]
    public async Task A_Note_Still_Over_60000_Characters_Is_Refused_Before_Anything_Is_Sent()
    {
        var findings = Findings([.. Enumerable.Range(1, 50).Select(i => Finding($"f{i}", "fix", 3, new string('t', 200), path: $"src/{new string('p', 980)}{i}.cs"))]);

        var failed = await Should.ThrowAsync<WriteFailedException>(() => Plugin().GivePullRequestVerdictAsync("octocat", "hello", 42, "request_changes", findings, "abc1234", CancellationToken.None));

        (failed.Message, failed.Outcome).ShouldBe((GitHubActionsPlugin.NoteTooLong, "refused"), "a note that cannot fit is refused");
        NothingSent().ShouldBeTrue("nothing is sent");
    }

    [Fact]
    public async Task A_Comment_Over_10000_Characters_Is_Refused_Before_Anything_Is_Sent()
    {
        var failed = await Should.ThrowAsync<WriteFailedException>(() => Plugin().GivePullRequestVerdictAsync(
            "octocat", "hello", 42, "request_changes", Findings(Finding("a", "block", 3, why: new string('w', 4_000), fix: new string('f', 4_000), context: new string('c', 4_000))), "abc1234", CancellationToken.None));

        (failed.Message, failed.Outcome).ShouldBe((GitHubActionsPlugin.NoteCommentTooLong, "refused"), "a comment over 10,000 characters is refused");
        NothingSent().ShouldBeTrue("nothing is sent");
        await _reads.DidNotReceive().ListPullRequestFilesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    private static readonly string[] ReviewParameters = ["owner", "repo", "number", "findings_json"];
    private static readonly string[] VerdictParameters = ["owner", "repo", "number", "verdict", "findings_json", "commit"];

    [Fact]
    public void The_Two_Functions_Take_Findings_And_No_Body()
    {
        string[] Parameters(string name) =>
            [.. typeof(GitHubActionsPlugin).GetMethod(name)!.GetParameters().Select(p => p.Name!).Where(n => n != "ct")];

        Parameters(nameof(GitHubActionsPlugin.SubmitPullRequestReviewAsync)).ShouldBe(ReviewParameters, "the model passes the review's findings, never a body");
        Parameters(nameof(GitHubActionsPlugin.GivePullRequestVerdictAsync)).ShouldBe(VerdictParameters, "the model passes the verdict's findings, never a body");
    }

    [Fact]
    public async Task A_Payload_With_A_Body_And_No_Findings_Is_Unreadable()
    {
        const string old = """{"owner":"octocat","repo":"hello","number":42,"verdict":"approve","body":"Ready.","comments":[],"commit":"abc1234"}""";
        var verdicts = new GitHubVerdictRunner(Plugin(), _reads, _writes);
        var reviews = new GitHubWriteRunner(Plugin(), _reads, _writes);

        var verdict = await Should.ThrowAsync<WriteFailedException>(() => verdicts.GiveAsync(old, CancellationToken.None));
        var review = await Should.ThrowAsync<WriteFailedException>(() => reviews.PostReviewAsync(old, CancellationToken.None));

        (verdict.Message, verdict.Outcome, review.Outcome).ShouldBe((GitHubVerdictRunner.Unreadable, "refused", "refused"), "a payload of the old shape is not sent");
        NothingSent().ShouldBeTrue("nothing is sent");
    }

    private static string Payload(string verdict, string findings) =>
        $$"""{"owner":"octocat","repo":"hello","number":42,"verdict":"{{verdict}}","findings":{{findings}},"commit":"abc1234","filesRead":5,"filesChanged":7,"notes":["2 files not read."]}""";

    [Theory]
    [InlineData("request_changes")]
    [InlineData("comment")]
    public async Task A_Preview_Is_The_Text_The_Send_Would_Post(string verdict)
    {
        var payload = Payload(verdict, Findings(Finding("a", "block", 3, "Move has no lock"), Finding("b", "fix", 9, "Off the diff")));
        var runner = new GitHubVerdictRunner(Plugin(), _reads, _writes);

        var preview = JsonDocument.Parse(await runner.PreviewAsync(payload, CancellationToken.None)).RootElement;
        if (verdict == "comment")
        {
            await new GitHubWriteRunner(Plugin(), _reads, _writes).PostReviewAsync(payload, CancellationToken.None);
        }
        else
        {
            await runner.GiveAsync(payload, CancellationToken.None);
        }

        var (body, comments) = verdict == "comment" ? SentReview() : SentVerdict();
        preview.GetProperty("body").GetString().ShouldBe(body, "the preview's note is the one the send posts");
        preview.GetProperty("comments").EnumerateArray()
            .Select(c => (c.GetProperty("id").GetString(), c.GetProperty("path").GetString(), c.GetProperty("line").GetInt32(), c.GetProperty("body").GetString()))
            .ShouldBe(comments.Select(c => ((string?)"a", (string?)c.Path, c.Line, (string?)c.Body)), "the preview's comments are the ones the send posts, each with its finding's id");
        preview.GetProperty("dropped").EnumerateArray().Select(d => d.GetString()).ShouldBe(["src/Checkout.cs:9"], "the preview names the finding off the diff");
    }

    [Fact]
    public async Task A_Preview_Reads_And_Sends_Nothing()
    {
        await new GitHubVerdictRunner(Plugin(), _reads, _writes).PreviewAsync(Payload("request_changes", "[]"), CancellationToken.None);

        _writes.ReceivedCalls().Select(c => c.GetMethodInfo().Name).Where(n => n != nameof(IGitHubWriteClient.SignerFor))
            .ShouldBeEmpty("a preview sends nothing; it only asks who would sign");
        await _reads.Received(1).ListPullRequestFilesAsync("octocat", "hello", 42, Arg.Any<CancellationToken>());
        await _reads.Received(1).ListCheckRunsAsync("octocat", "hello", Sha, Arg.Any<CancellationToken>());
        await _reads.DidNotReceive().ListReviewsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_Preview_Whose_GitHub_Read_Fails_Throws_The_Failures_Sentence_And_Sends_Nothing()
    {
        _reads.GetPullRequestHeadAsync("octocat", "hello", 42, Arg.Any<CancellationToken>())
            .ThrowsAsync(new GitHubApiException("GitHub is currently unavailable. Try again later.", HttpStatusCode.BadGateway));

        var failed = await Should.ThrowAsync<WriteFailedException>(() => new GitHubVerdictRunner(Plugin(), _reads, _writes).PreviewAsync(Payload("request_changes", "[]"), CancellationToken.None));

        (failed.Message, failed.Outcome).ShouldBe(("GitHub is currently unavailable. Try again later.", "github 502"), "the caller gets the failure's sentence");
        NothingSent().ShouldBeTrue();
    }

    [Fact]
    public async Task A_Preview_Over_60000_Characters_Is_Refused_With_The_Sentence_The_Card_Shows()
    {
        var findings = Findings([.. Enumerable.Range(1, 50).Select(i => Finding($"f{i}", "fix", 3, new string('t', 200), path: $"src/{new string('p', 980)}{i}.cs"))]);

        var failed = await Should.ThrowAsync<WriteFailedException>(() => new GitHubVerdictRunner(Plugin(), _reads, _writes).PreviewAsync(Payload("request_changes", findings), CancellationToken.None));

        failed.Message.ShouldBe(GitHubActionsPlugin.NoteTooLong, "a preview refused for its size says why");
    }

    [Fact]
    public async Task A_Payload_Carries_The_Review_Jobs_Measured_Count_And_Notes_Into_The_Note()
    {
        const string payload = """{"owner":"octocat","repo":"hello","number":42,"verdict":"request_changes","findings":[{"id":"a","severity":"fix","title":"t","path":"src/Other.cs","line":1,"why":"Why.","fix":"Fix."}],"commit":"abc1234","filesRead":5,"filesChanged":7,"notes":["2 files not read: the review was stopped."]}""";

        await new GitHubVerdictRunner(Plugin(), _reads, _writes).GiveAsync(payload, CancellationToken.None);

        SentVerdict().Body.ShouldContain(
            "#### :mag: What changed, by area\n\n5 of 7 changed files read.\n\n**Not read:** 2 files.\n\n- 2 files not read: the review was stopped.\n",
            customMessage: "a review job's measured count and notes ride from the payload into the note");
    }

    private static string Extras(string verdict, string findings, string commit = "abc1234", int read = 5, int changed = 7, string note = "2 files not read.") =>
        $$"""{"owner":"octocat","repo":"hello","number":42,"verdict":"{{verdict}}","findings":{{findings}},"commit":"{{commit}}","filesRead":{{read}},"filesChanged":{{changed}},"notes":["{{note}}"]}""";

    [Theory]
    [InlineData("the same extras", 5, 7, "2 files not read.", false)]
    [InlineData("other notes", 5, 7, "Other note.", true)]
    [InlineData("another coverage", 6, 7, "2 files not read.", true)]
    public async Task A_Verdict_With_Other_Notes_Or_Coverage_At_The_Same_Commit_Is_Sent_And_The_Identical_One_Is_Not(string what, int read, int changed, string note, bool sent)
    {
        var findings = Findings(Finding("a", "block", 3));
        var parsed = ReviewFindings.ParseFindings(findings, "x").Findings!;
        _reads.ListReviewsAsync("octocat", "hello", 42, Arg.Any<CancellationToken>()).Returns(new List<PullRequestReview>
        {
            new(5, "octocat", "CHANGES_REQUESTED", Sha, "https://github.com/octocat/hello/pull/42#pullrequestreview-5",
                "the note\n\n" + ReviewNote.Marker(ReviewNote.Digest(ReviewNote.RequestChanges, parsed, 5, 7, ["2 files not read."]))),
        });

        await new GitHubVerdictRunner(Plugin(), _reads, _writes).GiveAsync(Extras("request_changes", findings, read: read, changed: changed, note: note), CancellationToken.None);

        NothingSent().ShouldBe(!sent, $"a verdict with {what} at a commit that has one is {(sent ? "sent" : "not sent again")}");
    }

    [Fact]
    public async Task A_Commenting_Review_Whose_Commit_Is_No_Longer_The_Head_Is_Refused_And_Its_Preview_Says_The_Same()
    {
        var findings = Findings(Finding("a", "nit", 3));
        var moved = Extras("comment", findings, commit: "0000000");

        var post = await Should.ThrowAsync<WriteFailedException>(() => new GitHubWriteRunner(Plugin(), _reads, _writes).PostReviewAsync(moved, CancellationToken.None));
        var preview = await Should.ThrowAsync<WriteFailedException>(() => new GitHubVerdictRunner(Plugin(), _reads, _writes).PreviewAsync(moved, CancellationToken.None));

        (post.Message, preview.Message).ShouldBe(
            (GitHubActionsPlugin.HeadMoved, GitHubActionsPlugin.HeadMoved), "a commenting review at a commit that is no longer the head is refused, and its preview answers the same");
        NothingSent().ShouldBeTrue("nothing is sent for a moved head");
    }

    [Fact]
    public async Task A_Commenting_Review_With_No_Commit_Given_Uses_The_Head()
    {
        await Plugin().SubmitPullRequestReviewAsync("octocat", "hello", 42, Findings(Finding("a", "nit", 3)), CancellationToken.None);

        SentReview().Body.ShouldContain("Commit `abc1234`", Case.Sensitive, "a review with no commit given is composed at the head");
    }

    [Theory]
    [InlineData("request_changes", "block", "request_changes", true)]
    [InlineData("comment", "nit", "comment", false)]
    public async Task V8_The_preview_answers_the_offer_for_the_findings_selected(string verdict, string severity, string offered, bool commentInstead)
    {
        var payload = Payload(verdict, Findings(Finding("a", severity, 3, "Move has no lock")));

        var offer = JsonDocument.Parse(await new GitHubVerdictRunner(Plugin(), _reads, _writes).PreviewAsync(payload, CancellationToken.None)).RootElement.GetProperty("offer");

        (offer.GetProperty("verdict").GetString(), offer.GetProperty("commentInstead").GetBoolean()).ShouldBe(
            (offered, commentInstead), "the preview answers what the one decision offers for the selection");
        offer.GetProperty("reason").GetString().ShouldBe(
            severity == "block"
                ? "Request changes: 1 finding needs fixing."
                : "Comment, not approve: 1 optional suggestion was kept and 2 of 7 changed files were not read.",
            "with its reason, the selection's files read included");
        NothingSent().ShouldBeTrue("a preview still sends nothing");
    }
}
