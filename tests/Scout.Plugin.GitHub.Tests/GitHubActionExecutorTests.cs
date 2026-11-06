using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Scout.Conversation.Abstractions;
using Scout.Llm.Abstractions;
using Scout.Plugin.GitHub.Abstractions;
using Scout.Plugin.GitHub.Tests.Fakes;

namespace Scout.Plugin.GitHub.Tests;

public sealed class GitHubActionExecutorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("scout-github-executor-").FullName;
    private readonly RecordingWriteGate _gate = new();
    private readonly IGitHubWriteClient _github = Substitute.For<IGitHubWriteClient>();
    private readonly IOriginNotifier _notifier = Substitute.For<IOriginNotifier>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero));

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private GitHubDraftStore Drafts => new(_dir);

    private GitHubActionExecutor Executor() =>
        new(_gate, _github, _gate, Drafts, _notifier, _time, NullLogger<GitHubActionExecutor>.Instance);

    private async Task QueuedReviewAsync(string actionId = "act-1") =>
        await Drafts.SaveAsync(new GitHubDraft("d1", GitHubDraft.ReviewKind, "octocat", "hello", 42, string.Empty, "Ready.",
            [new ReviewComment("src/Checkout.cs", 3, "c is new")], [], "discord:1/2", _time.GetUtcNow(), actionId), CancellationToken.None);

    private static ApprovedAction Review(string id = "act-1") => new(id, "github", "Pull request reviews", "octocat/hello#42", "Post a review on octocat/hello#42: 1 comment(s)");

    [Fact]
    public async Task An_Approved_Review_Is_Posted_Recorded_Deleted_And_Told_To_The_Chat()
    {
        await QueuedReviewAsync();
        _gate.Approved.Enqueue(Review());
        _github.SubmitReviewAsync("octocat", "hello", 42, "Ready.", Arg.Any<IReadOnlyList<ReviewComment>>(), Arg.Any<CancellationToken>())
            .Returns(new PostedReview(77, "https://github.com/octocat/hello/pull/42#pullrequestreview-77"));

        await Executor().RunOnceAsync(CancellationToken.None);

        await _github.Received(1).SubmitReviewAsync("octocat", "hello", 42, "Ready.",
            Arg.Is<IReadOnlyList<ReviewComment>>(c => c.Single() == new ReviewComment("src/Checkout.cs", 3, "c is new")), Arg.Any<CancellationToken>());
        _gate.Records.ShouldHaveSingleItem().ShouldBe(("github", "Pull request reviews", "octocat/hello#42", "succeeded", "act-1"));
        (await Drafts.FindAsync("d1", CancellationToken.None)).ShouldBeNull();
        await _notifier.Received(1).PostAsync("discord:1/2", "Review posted on octocat/hello#42: https://github.com/octocat/hello/pull/42#pullrequestreview-77", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_Approved_Issue_Is_Opened_And_Its_Number_Told()
    {
        await Drafts.SaveAsync(new GitHubDraft("d2", GitHubDraft.IssueKind, "octocat", "hello", 0, "CI never runs the tests", "## What", [], ["ci"], "slack:C1/9", _time.GetUtcNow(), "act-2"), CancellationToken.None);
        _gate.Approved.Enqueue(new ApprovedAction("act-2", "github", "New issues", "octocat/hello", "Open an issue on octocat/hello: CI never runs the tests"));
        _github.CreateIssueAsync("octocat", "hello", "CI never runs the tests", "## What", Arg.Is<IReadOnlyList<string>>(l => l.Single() == "ci"), Arg.Any<CancellationToken>())
            .Returns(new CreatedIssue(13, "https://github.com/octocat/hello/issues/13"));

        await Executor().RunOnceAsync(CancellationToken.None);

        _gate.Records.ShouldHaveSingleItem().ShouldBe(("github", "New issues", "octocat/hello", "succeeded", "act-2"));
        await _notifier.Received(1).PostAsync("slack:C1/9", "Issue #13 opened on octocat/hello: https://github.com/octocat/hello/issues/13", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_Action_Without_A_Draft_Is_Recorded_Failed_So_The_Queue_Moves_On()
    {
        _gate.Approved.Enqueue(Review("act-lost"));

        await Executor().RunOnceAsync(CancellationToken.None);

        _gate.Records.ShouldHaveSingleItem().ShouldBe(("github", "Pull request reviews", "octocat/hello#42", GitHubActionExecutor.DraftMissing, "act-lost"));
        await _github.DidNotReceiveWithAnyArgs().SubmitReviewAsync(default!, default!, default, default!, default!, default);
    }

    [Fact]
    public async Task A_Recoverable_Failure_Leaves_The_Action_And_The_Draft_Unmarked_For_The_Next_Run()
    {
        await QueuedReviewAsync();
        for (var i = 0; i < 5; i++)
        {
            _gate.Approved.Enqueue(Review());
        }

        _github.SubmitReviewAsync(default!, default!, default, default!, default!, default)
            .ReturnsForAnyArgs<PostedReview>(
                _ => throw new HttpRequestException("reset"),
                _ => throw new GitHubApiException("GitHub is currently unavailable. Try again later.", HttpStatusCode.BadGateway),
                _ => throw new GitHubApiException("GitHub rate limit reached. Try again later.", HttpStatusCode.Forbidden),
                _ => throw new GitHubApiException("GitHub authentication failed. Check the configured token.", HttpStatusCode.Unauthorized),
                _ => throw new GitHubApiException("GitHub request failed (429).", HttpStatusCode.TooManyRequests));
        var executor = Executor();

        for (var i = 0; i < 5; i++)
        {
            await executor.RunOnceAsync(CancellationToken.None);
        }

        _gate.Records.ShouldBeEmpty();
        (await Drafts.FindAsync("d1", CancellationToken.None)).ShouldNotBeNull().PostingAt.ShouldBeNull("a retry starts from a clean draft");
        await _notifier.DidNotReceiveWithAnyArgs().PostAsync(default!, default!, default);
    }

    [Fact]
    public async Task A_Draft_Another_Run_Left_Posting_Is_Not_Posted_Again_But_Recorded_As_Possibly_Posted()
    {
        await Drafts.SaveAsync(new GitHubDraft("d1", GitHubDraft.ReviewKind, "octocat", "hello", 42, string.Empty, "Ready.",
            [], [], "discord:1/2", _time.GetUtcNow(), "act-1", PostingAt: _time.GetUtcNow() - TimeSpan.FromMinutes(40)), CancellationToken.None);
        _gate.Approved.Enqueue(Review());

        await Executor().RunOnceAsync(CancellationToken.None);

        await _github.DidNotReceiveWithAnyArgs().SubmitReviewAsync(default!, default!, default, default!, default!, default);
        _gate.Records.ShouldHaveSingleItem().Outcome.ShouldBe(GitHubActionExecutor.MayBePosted);
        (await Drafts.FindAsync("d1", CancellationToken.None)).ShouldBeNull();
        await _notifier.Received(1).PostAsync("discord:1/2", GitHubActionExecutor.MayAlreadyBePosted("octocat/hello#42"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_Post_That_Succeeded_But_Was_Not_Recorded_Is_Recorded_On_The_Next_Run_Without_A_Second_Post()
    {
        await QueuedReviewAsync();
        _gate.Approved.Enqueue(Review());
        _gate.Approved.Enqueue(Review());
        _github.SubmitReviewAsync(default!, default!, default, default!, default!, default)
            .ReturnsForAnyArgs(new PostedReview(77, "u"));
        var failingGate = new ThrowingOnceGate(_gate);
        var executor = new GitHubActionExecutor(_gate, _github, failingGate, Drafts, _notifier, _time, NullLogger<GitHubActionExecutor>.Instance);

        await executor.RunOnceAsync(CancellationToken.None);
        await executor.RunOnceAsync(CancellationToken.None);

        await _github.ReceivedWithAnyArgs(1).SubmitReviewAsync(default!, default!, default, default!, default!, default);
        _gate.Records.ShouldHaveSingleItem().Outcome.ShouldBe("succeeded");
        (await Drafts.FindAsync("d1", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task An_Unexpected_Exception_Is_Logged_And_Never_Leaves_The_Run()
    {
        await QueuedReviewAsync();
        _gate.Approved.Enqueue(Review());
        _github.SubmitReviewAsync(default!, default!, default, default!, default!, default)
            .ReturnsForAnyArgs<PostedReview>(_ => throw new InvalidOperationException("unexpected"));

        await Should.NotThrowAsync(() => Executor().RunOnceAsync(CancellationToken.None));

        _gate.Records.ShouldBeEmpty();
    }

    /// <summary>A gate whose first record throws, as a control plane that went away for a moment would.</summary>
    private sealed class ThrowingOnceGate(RecordingWriteGate inner) : IWriteGate
    {
        private bool _thrown;

        public Task<WriteGateDecision> RequestAsync(string featureId, string scope, string target, string summary, CancellationToken cancellationToken) =>
            inner.RequestAsync(featureId, scope, target, summary, cancellationToken);

        public Task RecordAsync(string featureId, string scope, string target, string outcome, string? actionId, CancellationToken cancellationToken)
        {
            if (!_thrown)
            {
                _thrown = true;
                throw new InvalidOperationException("control plane away");
            }

            return inner.RecordAsync(featureId, scope, target, outcome, actionId, cancellationToken);
        }
    }

    [Fact]
    public async Task A_Rejection_By_GitHub_Is_Recorded_Failed_The_Draft_Deleted_And_The_Chat_Told_Why()
    {
        await QueuedReviewAsync();
        _gate.Approved.Enqueue(Review());
        _github.SubmitReviewAsync(default!, default!, default, default!, default!, default)
            .ReturnsForAnyArgs<PostedReview>(_ => throw new GitHubApiException("GitHub rejected the write: line must be part of the diff.", HttpStatusCode.UnprocessableEntity));

        await Executor().RunOnceAsync(CancellationToken.None);

        _gate.Records.ShouldHaveSingleItem().Outcome.ShouldBe("failed: GitHubApiException");
        (await Drafts.FindAsync("d1", CancellationToken.None)).ShouldBeNull();
        await _notifier.Received(1).PostAsync("discord:1/2", "The write on octocat/hello#42 the owner approved was not performed: GitHub rejected the write: line must be part of the diff.", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_Run_Purges_Week_Old_Drafts_And_Survives_A_Control_Plane_That_Does_Not_Answer()
    {
        await Drafts.SaveAsync(new GitHubDraft("stale", GitHubDraft.ReviewKind, "o", "r", 1, string.Empty, "b", [], [], null, _time.GetUtcNow() - TimeSpan.FromDays(8), null), CancellationToken.None);
        _gate.ClaimFailure = new InvalidOperationException("socket gone");

        await Executor().RunOnceAsync(CancellationToken.None);

        (await Drafts.FindAsync("stale", CancellationToken.None)).ShouldBeNull();
        _gate.Records.ShouldBeEmpty();
    }
}
