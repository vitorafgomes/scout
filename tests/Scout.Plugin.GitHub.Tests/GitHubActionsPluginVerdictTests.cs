using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using NSubstitute.ExceptionExtensions;
using Scout.Llm.Abstractions;
using Scout.Plugin.GitHub.Abstractions;
using Scout.Plugin.GitHub.Tests.Fakes;

namespace Scout.Plugin.GitHub.Tests;

public sealed class GitHubActionsPluginVerdictTests : IDisposable
{
    private const string Patch = "@@ -1,3 +1,4 @@\n a\n-b\n+c\n+d\n e";
    private const string Comments = """[{"path":"src/Checkout.cs","line":3,"body":"c is new"},{"path":"src/Other.cs","line":1,"body":"not in the PR"}]""";

    private readonly string _dir = Directory.CreateTempSubdirectory("scout-github-verdict-").FullName;
    private readonly IGitHubClient _reads = Substitute.For<IGitHubClient>();
    private readonly StubHttpMessageHandler _handler = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.Zero));

    public GitHubActionsPluginVerdictTests()
    {
        _reads.ListPullRequestFilesAsync("octocat", "hello", 42, Arg.Any<CancellationToken>())
            .Returns(new List<ChangedFile> { new("src/Checkout.cs", "modified", 2, 1, Patch) });
        _reads.GetPullRequestHeadAsync("octocat", "hello", 42, Arg.Any<CancellationToken>())
            .Returns(new PullRequestHead("abc1234", "alice"));
        _reads.GetSignedInLoginAsync(Arg.Any<CancellationToken>()).Returns("octocat");
        _reads.ListReviewsAsync("octocat", "hello", 42, Arg.Any<CancellationToken>()).Returns(new List<PullRequestReview>());
    }

    public void Dispose()
    {
        _handler.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private GitHubActionsPlugin Plugin() => new(
        _reads,
        new GitHubWriteClient(new HttpClient(_handler) { BaseAddress = new Uri("https://api.github.com/") }),
        new GitHubProfileStore(Path.Combine(_dir, "profiles")),
        new RepositorySnapshotStore(Path.Combine(_dir, "snapshots"), 0, _time),
        Substitute.For<IRepositoryProjects>(),
        _time);

    private async Task<JsonElement> Sent() =>
        JsonDocument.Parse(await _handler.LastRequest!.Content!.ReadAsStringAsync()).RootElement;

    private static async Task<string> Refused(Func<Task<string>> call)
    {
        var ex = await Should.ThrowAsync<WriteFailedException>(call);
        ex.Outcome.ShouldBe("refused");
        return ex.Message;
    }

    [Fact]
    public async Task Approve_Posts_An_Approving_Review_With_The_Body_And_The_Comments_On_The_Diff()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, """{"id":88,"html_url":"https://github.com/octocat/hello/pull/42#pullrequestreview-88"}""");

        var result = await Plugin().GivePullRequestVerdictAsync("octocat", "hello", 42, "approve", "Ready to merge.", Comments, "abc1234", CancellationToken.None);

        result.ShouldBe("Approved octocat/hello#42 with 1 comment(s): https://github.com/octocat/hello/pull/42#pullrequestreview-88 Dropped, not on a line of the diff: src/Other.cs:1.");
        _handler.LastRequest!.RequestUri!.ToString().ShouldBe("https://api.github.com/repos/octocat/hello/pulls/42/reviews");
        var sent = await Sent();
        sent.GetProperty("event").GetString().ShouldBe("APPROVE");
        sent.GetProperty("body").GetString().ShouldBe("Ready to merge.");
        var comment = sent.GetProperty("comments").EnumerateArray().ShouldHaveSingleItem();
        (comment.GetProperty("path").GetString(), comment.GetProperty("line").GetInt32(), comment.GetProperty("body").GetString())
            .ShouldBe(("src/Checkout.cs", 3, "c is new"));
    }

    [Fact]
    public async Task Request_Changes_Posts_A_Review_That_Requests_Changes()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, """{"id":89,"html_url":"https://github.com/octocat/hello/pull/42#pullrequestreview-89"}""");

        var result = await Plugin().GivePullRequestVerdictAsync("octocat", "hello", 42, "request_changes", "Fix the sum first.", "[]", "abc1234", CancellationToken.None);

        result.ShouldBe("Requested changes on octocat/hello#42 with 0 comment(s): https://github.com/octocat/hello/pull/42#pullrequestreview-89");
        (await Sent()).GetProperty("event").GetString().ShouldBe("REQUEST_CHANGES");
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("ALICE")]
    public async Task Approving_A_Pull_Request_The_Signing_Identity_Authored_Is_Refused_Before_Any_Call(string signer)
    {
        _reads.GetSignedInLoginAsync(Arg.Any<CancellationToken>()).Returns(signer);

        (await Refused(() => Plugin().GivePullRequestVerdictAsync("octocat", "hello", 42, "approve", "Ready.", "[]", "abc1234", CancellationToken.None)))
            .ShouldBe("GitHub does not let the author approve their own pull request; request changes or comment instead.");
        _handler.LastRequest.ShouldBeNull();
    }

    [Fact]
    public async Task Requesting_Changes_On_The_Signing_Identity_s_Own_Pull_Request_Is_Allowed()
    {
        _reads.GetSignedInLoginAsync(Arg.Any<CancellationToken>()).Returns("alice");
        _handler.EnqueueJson(HttpStatusCode.OK, """{"id":90,"html_url":"u"}""");

        var result = await Plugin().GivePullRequestVerdictAsync("octocat", "hello", 42, "request_changes", "Fix the sum first.", "[]", "abc1234", CancellationToken.None);

        result.ShouldStartWith("Requested changes on octocat/hello#42");
    }

    [Theory]
    [InlineData("comment")]
    [InlineData("")]
    [InlineData("merge")]
    public async Task A_Verdict_Other_Than_Approve_Or_Request_Changes_Is_Refused_Before_Any_Call(string verdict)
    {
        (await Refused(() => Plugin().GivePullRequestVerdictAsync("octocat", "hello", 42, verdict, "Ready.", "[]", "abc1234", CancellationToken.None)))
            .ShouldBe(GitHubActionsPlugin.InvalidVerdict);
        _handler.LastRequest.ShouldBeNull();
        await _reads.DidNotReceiveWithAnyArgs().ListPullRequestFilesAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task A_Verdict_On_A_Pull_Request_That_Does_Not_Exist_Is_Refused()
    {
        _reads.GetPullRequestHeadAsync("octocat", "hello", 42, Arg.Any<CancellationToken>()).Returns((PullRequestHead?)null);

        (await Refused(() => Plugin().GivePullRequestVerdictAsync("octocat", "hello", 42, "approve", "Ready.", "[]", "abc1234", CancellationToken.None)))
            .ShouldBe("Pull request #42 not found in octocat/hello or not accessible.");
        _handler.LastRequest.ShouldBeNull();
    }

    [Fact]
    public async Task GitHub_Unreachable_While_Reading_Who_Signs_Never_Says_The_Verdict_May_Be_There()
    {
        _reads.GetSignedInLoginAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new TaskCanceledException("timed out"));

        var ex = await Should.ThrowAsync<WriteFailedException>(() => Plugin().GivePullRequestVerdictAsync("octocat", "hello", 42, "approve", "Ready.", "[]", "abc1234", CancellationToken.None));

        ex.Message.ShouldBe("GitHub request timed out. Try again later.");
        _handler.LastRequest.ShouldBeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "", "GitHub denied the request (insufficient token permissions).", "github 403")]
    [InlineData(HttpStatusCode.NotFound, "", "Repository not found or the token has no access.", "github 404")]
    [InlineData(HttpStatusCode.UnprocessableEntity, """{"message":"Unprocessable Entity","errors":["Can not approve your own pull request"]}""", "GitHub rejected the write: Can not approve your own pull request.", "github 422")]
    public async Task GitHub_Refusing_A_Verdict_Is_A_Sentence_For_The_Model_And_The_Status_For_The_Log(
        HttpStatusCode status, string reply, string sentence, string outcome)
    {
        _handler.EnqueueJson(status, reply);

        var ex = await Should.ThrowAsync<WriteFailedException>(() => Plugin().GivePullRequestVerdictAsync("octocat", "hello", 42, "request_changes", "Fix it.", "[]", "abc1234", CancellationToken.None));

        ex.Message.ShouldBe(sentence);
        ex.Outcome.ShouldBe(outcome);
    }

    [Fact]
    public async Task A_Verdict_That_Times_Out_Says_It_May_Already_Be_Given()
    {
        _handler.EnqueueThrow(new TaskCanceledException("timed out"));

        var ex = await Should.ThrowAsync<WriteFailedException>(() => Plugin().GivePullRequestVerdictAsync("octocat", "hello", 42, "request_changes", "Fix it.", "[]", "abc1234", CancellationToken.None));

        ex.Message.ShouldBe("GitHub request timed out. Try again later." + GitHubActionsPlugin.MayAlreadyBePosted);
    }

    [Fact]
    public async Task The_Verdict_Is_Pinned_To_The_Head_Commit_The_Review_Read()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, """{"id":91,"html_url":"u"}""");

        await Plugin().GivePullRequestVerdictAsync("octocat", "hello", 42, "request_changes", "Fix it.", "[]", "abc1234", CancellationToken.None);

        (await Sent()).GetProperty("commit_id").GetString().ShouldBe("abc1234");
    }

    [Fact]
    public async Task A_Pull_Request_That_Moved_Past_The_Commit_Reviewed_Gets_No_Verdict()
    {
        (await Refused(() => Plugin().GivePullRequestVerdictAsync("octocat", "hello", 42, "approve", "Ready.", "[]", "fedcba9", CancellationToken.None)))
            .ShouldBe("The pull request changed since the review; review it again.");
        _handler.LastRequest.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc12")]
    public async Task A_Verdict_Without_The_Commit_Reviewed_Is_Refused_Before_Any_Call(string commit)
    {
        (await Refused(() => Plugin().GivePullRequestVerdictAsync("octocat", "hello", 42, "approve", "Ready.", "[]", commit, CancellationToken.None)))
            .ShouldBe(GitHubActionsPlugin.NoCommit);
        _handler.LastRequest.ShouldBeNull();
        await _reads.DidNotReceiveWithAnyArgs().GetPullRequestHeadAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task A_Verdict_The_Signer_Already_Gave_On_This_Commit_Is_Answered_With_Its_Link_And_Never_Sent_Again()
    {
        _reads.ListReviewsAsync("octocat", "hello", 42, Arg.Any<CancellationToken>()).Returns(new List<PullRequestReview>
        {
            new(4, "octocat", "APPROVED", "0000000", "https://github.com/octocat/hello/pull/42#pullrequestreview-4"),
            new(5, "octocat", "APPROVED", "abc1234", "https://github.com/octocat/hello/pull/42#pullrequestreview-5"),
        });

        var answer = await Plugin().GivePullRequestVerdictAsync("octocat", "hello", 42, "approve", "Ready.", "[]", "abc1234", CancellationToken.None);

        answer.ShouldBe("Approved octocat/hello#42 already, at this commit: https://github.com/octocat/hello/pull/42#pullrequestreview-5");
        _handler.LastRequest.ShouldBeNull();
    }

    [Theory]
    [InlineData("alice", "APPROVED", "abc1234")]
    [InlineData("octocat", "CHANGES_REQUESTED", "abc1234")]
    [InlineData("octocat", "APPROVED", "0000000")]
    [InlineData("octocat", "DISMISSED", "abc1234")]
    public async Task Another_Reviewer_Another_Event_Or_Another_Commit_Does_Not_Stop_The_Verdict(string login, string state, string commit)
    {
        _reads.ListReviewsAsync("octocat", "hello", 42, Arg.Any<CancellationToken>())
            .Returns(new List<PullRequestReview> { new(5, login, state, commit, "u") });
        _handler.EnqueueJson(HttpStatusCode.OK, """{"id":92,"html_url":"https://github.com/octocat/hello/pull/42#pullrequestreview-92"}""");

        var answer = await Plugin().GivePullRequestVerdictAsync("octocat", "hello", 42, "approve", "Ready.", "[]", "abc1234", CancellationToken.None);

        answer.ShouldStartWith("Approved octocat/hello#42 with 0 comment(s): https://github.com/octocat/hello/pull/42#pullrequestreview-92");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "GitHub denied the request (insufficient token permissions).", "github 403")]
    [InlineData(HttpStatusCode.BadGateway, "GitHub is currently unavailable. Try again later.", "github 502")]
    public async Task A_Failed_Read_Of_The_Reviews_Already_Given_Sends_No_Verdict(HttpStatusCode status, string sentence, string outcome)
    {
        _reads.ListReviewsAsync("octocat", "hello", 42, Arg.Any<CancellationToken>()).ThrowsAsync(new GitHubApiException(sentence, status));

        var ex = await Should.ThrowAsync<WriteFailedException>(() => Plugin().GivePullRequestVerdictAsync("octocat", "hello", 42, "approve", "Ready.", "[]", "abc1234", CancellationToken.None));

        (ex.Message, ex.Outcome).ShouldBe((sentence, outcome));
        _handler.LastRequest.ShouldBeNull();
    }

    [Fact]
    public async Task A_Read_Of_The_Reviews_Already_Given_That_Times_Out_Sends_No_Verdict_And_Never_Says_It_May_Be_There()
    {
        _reads.ListReviewsAsync("octocat", "hello", 42, Arg.Any<CancellationToken>()).ThrowsAsync(new TaskCanceledException("timed out"));

        var ex = await Should.ThrowAsync<WriteFailedException>(() => Plugin().GivePullRequestVerdictAsync("octocat", "hello", 42, "approve", "Ready.", "[]", "abc1234", CancellationToken.None));

        ex.Message.ShouldBe("GitHub request timed out. Try again later.");
        _handler.LastRequest.ShouldBeNull();
    }

    [Fact]
    public void The_Verdict_Function_No_Longer_Tells_The_Model_To_Send_The_Owner_To_Another_Menu_To_Approve()
    {
        var description = typeof(GitHubActionsPlugin)
            .GetMethod(nameof(GitHubActionsPlugin.GivePullRequestVerdictAsync))!
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
            .Cast<System.ComponentModel.DescriptionAttribute>()
            .Single()
            .Description;

        description.ShouldContain("an Approve card waiting in the chat and in Approvals of Scout Desktop");
        description.ShouldNotContain("Review screen");
    }

    [Theory]
    [InlineData(nameof(GitHubActionsPlugin.SubmitPullRequestReviewAsync))]
    [InlineData(nameof(GitHubActionsPlugin.CreateIssueAsync))]
    [InlineData(nameof(GitHubActionsPlugin.CommentOnPullRequestAsync))]
    [InlineData(nameof(GitHubActionsPlugin.CommentOnIssueAsync))]
    [InlineData(nameof(GitHubActionsPlugin.ReplyToReviewCommentAsync))]
    public void A_Review_Issue_Or_Comment_Function_Tells_The_Model_The_Call_Is_Queued_For_The_Owner_And_Promises_No_Url(string method)
    {
        var description = typeof(GitHubActionsPlugin)
            .GetMethod(method)!
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
            .Cast<System.ComponentModel.DescriptionAttribute>()
            .Single()
            .Description;

        description.ShouldContain("queued for the owner's approval, an Approve card waiting in the chat and in Approvals of Scout Desktop, and sent only after the owner approves it");
        description.ShouldContain("Never call it again for the same");
        description.ShouldNotContain("URL");
    }
}
