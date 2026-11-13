using System.Net;
using System.Text;
using System.Text.Json;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Scout.Llm.Abstractions;
using Scout.Plugin.GitHub;
using Shouldly;
using Xunit;

namespace Scout.Host.Console.Tests;

public sealed class GitHubActionExecutorTests : IDisposable
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private const string ReviewUrl = "https://github.com/octocat/hello/pull/42#pullrequestreview-88";
    private const string Patch = "@@ -1,3 +1,4 @@\n a\n-b\n+c\n+d\n e";

    private readonly RoutingHandler _github = new();
    private readonly FakeGate _gate = new();
    private readonly ServiceProvider _provider;

    public GitHubActionExecutorTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Scout:Plugins:GitHub:Token"] = "owner-token-not-real" })
            .Build();
        var services = new ServiceCollection().AddLogging();
        services.AddGitHubPlugin(configuration);
        services.AddHttpClient("IGitHubClient").ConfigurePrimaryHttpMessageHandler(() => _github);
        services.AddHttpClient("IGitHubWriteClient").ConfigurePrimaryHttpMessageHandler(() => _github);
        _provider = services.BuildServiceProvider();
        _gate.Steps = _github;
        _github.On("GET", "repos/octocat/hello/pulls/42", HttpStatusCode.OK, $$$"""{"number":42,"user":{"login":"alice"},"head":{"ref":"feat/x","sha":"{{{Sha}}}"}}""");
        _github.On("GET", "user", HttpStatusCode.OK, """{"login":"octocat"}""");
        _github.On("GET", "repos/octocat/hello/pulls/42/files?per_page=100&page=1", HttpStatusCode.OK,
            JsonSerializer.Serialize(new[] { new { filename = "src/Checkout.cs", status = "modified", additions = 2, deletions = 1, patch = Patch } }));
        _github.On("GET", "repos/octocat/hello/pulls/42/reviews?per_page=100&page=1", HttpStatusCode.OK, "[]");
        _github.On("POST", "repos/octocat/hello/pulls/42/reviews", HttpStatusCode.OK, $$"""{"id":88,"html_url":"{{ReviewUrl}}"}""");
        _github.On("POST", "repos/octocat/hello/issues/42/comments", HttpStatusCode.Created, """{"id":5,"html_url":"https://github.com/octocat/hello/pull/42#issuecomment-5"}""");
    }

    public void Dispose()
    {
        _provider.Dispose();
        _github.Dispose();
    }

    private GitHubActionExecutor Executor(ILogger<GitHubActionExecutor>? logger = null, TimeProvider? time = null) =>
        new(_gate, _gate, _provider.GetRequiredService<GitHubVerdictRunner>(), _provider.GetRequiredService<GitHubWriteRunner>(), time ?? TimeProvider.System, logger ?? NullLogger<GitHubActionExecutor>.Instance);

    private static string CardPayload(string commit = Sha, string verdict = "approve", string testsLine = "Tests: 3 checks passed.") =>
        JsonSerializer.Serialize(new
        {
            owner = "octocat",
            repo = "hello",
            number = 42,
            verdict,
            body = "Ready to merge.",
            comments = new[] { new { path = "src/Checkout.cs", line = 3, body = "c is new" } },
            commit,
            testsLine,
        });

    private static ApprovedAction Verdict(string? payload, string scope = "Pull request verdicts") =>
        new("act-1", "github", scope, "octocat/hello#42", "Approve octocat/hello#42") { Payload = payload };

    private static ApprovedAction Asked(string function, string scope, string? payload) =>
        new("act-1", "github", scope, "GitHubActions." + function, function + "(owner=octocat, repo=hello)") { Payload = payload };

    private static string IssuePayload() =>
        JsonSerializer.Serialize(new { body = "## Problem\n\nThe total is wrong.", labels = "bug", owner = "octocat", repo = "hello", title = "Cart loses an item" });

    private static string ReviewArguments() =>
        JsonSerializer.Serialize(new
        {
            body = "Two findings.",
            comments = """[{"path":"src/Checkout.cs","line":3,"body":"c is new"}]""",
            number = "42",
            owner = "octocat",
            repo = "hello",
        });

    private static ApprovedAction PostClicked() =>
        new("act-1", "github", "Pull request reviews", "octocat/hello#42", "Post a review on octocat/hello#42") { Payload = CardPayload(verdict: "comment", testsLine: "") };

    private static ApprovedAction ReviewAsked() => Asked("submit_pull_request_review", "Pull request reviews", ReviewArguments());

    private static string CommentPayload() =>
        JsonSerializer.Serialize(new { body = "Looks good once the lock is in.", comment_id = "99", number = "42", owner = "octocat", repo = "hello" });

    [Fact]
    public async Task Stopping_The_Worker_While_The_Executor_Waits_For_An_Approved_Action_Ends_Quietly_And_Not_As_A_Failed_Background_Service()
    {
        var service = new GrpcWriteGateTests.HangingWorkerGate();
        await using var server = await GrpcWriteGateTests.WorkerGateServer.StartAsync(service);
        using var gate = new GrpcWriteGate(server.SocketPath, "worker-session-token");
        using var executor = new GitHubActionExecutor(
            gate, gate, _provider.GetRequiredService<GitHubVerdictRunner>(), _provider.GetRequiredService<GitHubWriteRunner>(), TimeProvider.System, NullLogger<GitHubActionExecutor>.Instance);

        await executor.StartAsync(CancellationToken.None);
        await service.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await executor.StopAsync(CancellationToken.None);

        executor.ExecuteTask.ShouldNotBeNull().IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public async Task An_Approved_Verdict_Is_Sent_Pinned_To_The_Commit_Reviewed_Recorded_With_Its_Link_Then_Its_Tests_Line_Posted()
    {
        _gate.Claims.Enqueue(Verdict(CardPayload()));

        await Executor().RunOnceAsync(CancellationToken.None);

        _gate.Records.ShouldHaveSingleItem().ShouldBe(("Pull request verdicts", "octocat/hello#42", "succeeded", "act-1", ReviewUrl));
        var review = JsonDocument.Parse(_github.Sent("POST", "repos/octocat/hello/pulls/42/reviews").ShouldHaveSingleItem()).RootElement;
        review.GetProperty("event").GetString().ShouldBe("APPROVE");
        review.GetProperty("commit_id").GetString().ShouldBe(Sha);
        review.GetProperty("body").GetString().ShouldBe("Ready to merge.");
        var tests = JsonDocument.Parse(_github.Sent("POST", "repos/octocat/hello/issues/42/comments").ShouldHaveSingleItem()).RootElement;
        tests.GetProperty("body").GetString().ShouldBe("Tests: 3 checks passed.");
        _github.Order.IndexOf("POST repos/octocat/hello/issues/42/comments").ShouldBeGreaterThanOrEqualTo(_gate.RecordedAtStep, "the tests line is posted only after the verdict is recorded");
    }

    [Fact]
    public async Task An_Approved_Issue_Is_Opened_From_Its_Payload_And_Recorded_With_Its_Link_And_The_Login_That_Signed_It()
    {
        _github.On("POST", "repos/octocat/hello/issues", HttpStatusCode.Created, """{"number":12,"html_url":"https://github.com/octocat/hello/issues/12"}""");
        _gate.Claims.Enqueue(Asked("create_issue", "New issues", IssuePayload()));

        await Executor().RunOnceAsync(CancellationToken.None);

        _gate.Records.ShouldHaveSingleItem().ShouldBe(("New issues", "GitHubActions.create_issue", "succeeded", "act-1", "https://github.com/octocat/hello/issues/12"));
        _gate.Signers.ShouldBe(["octocat"]);
        _gate.GrantReads.ShouldBe(["act-1"]);
        var issue = JsonDocument.Parse(_github.Sent("POST", "repos/octocat/hello/issues").ShouldHaveSingleItem()).RootElement;
        issue.GetProperty("title").GetString().ShouldBe("Cart loses an item");
        issue.GetProperty("body").GetString().ShouldBe("## Problem\n\nThe total is wrong.");
        issue.GetProperty("labels").EnumerateArray().Select(l => l.GetString()).ShouldBe(["bug"]);
    }

    [Theory]
    [InlineData(true, "Ready to merge.")]
    [InlineData(false, "Two findings.")]
    public async Task A_Click_On_Post_Or_An_Approved_Review_Asked_In_Chat_Posts_The_Review_As_Comments_Only_And_Records_Its_Link_And_Signer(bool clicked, string body)
    {
        var action = clicked ? PostClicked() : ReviewAsked();
        _gate.Claims.Enqueue(action);

        await Executor().RunOnceAsync(CancellationToken.None);

        _gate.Records.ShouldHaveSingleItem().ShouldBe(("Pull request reviews", action.Target, "succeeded", "act-1", ReviewUrl));
        _gate.Signers.ShouldBe(["octocat"]);
        _gate.GrantReads.ShouldBe(["act-1"]);
        var review = JsonDocument.Parse(_github.Sent("POST", "repos/octocat/hello/pulls/42/reviews").ShouldHaveSingleItem()).RootElement;
        review.GetProperty("event").GetString().ShouldBe("COMMENT");
        review.GetProperty("body").GetString().ShouldBe(body);
        var comment = review.GetProperty("comments").EnumerateArray().ShouldHaveSingleItem();
        (comment.GetProperty("path").GetString(), comment.GetProperty("line").GetInt32(), comment.GetProperty("body").GetString())
            .ShouldBe(("src/Checkout.cs", 3, "c is new"));
        _github.Sent("POST", "repos/octocat/hello/issues/42/comments").ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_Review_Post_GitHub_Fails_With_A_502_Is_Recorded_As_Failed_With_The_Reason(bool clicked)
    {
        _github.On("POST", "repos/octocat/hello/pulls/42/reviews", HttpStatusCode.BadGateway, """{"message":"Server Error"}""");
        var action = clicked ? PostClicked() : ReviewAsked();
        _gate.Claims.Enqueue(action);

        await Executor().RunOnceAsync(CancellationToken.None);

        _github.Sent("POST", "repos/octocat/hello/pulls/42/reviews").Count.ShouldBe(1);
        _gate.Records.ShouldHaveSingleItem().ShouldBe(("Pull request reviews", action.Target, "failed: github 502", "act-1", "GitHub is currently unavailable. Try again later."));
    }

    [Fact]
    public async Task A_Review_Post_That_Times_Out_Is_Recorded_As_Failed_Saying_It_May_Already_Be_There()
    {
        _github.Throw("POST", "repos/octocat/hello/pulls/42/reviews", new TaskCanceledException("The request timed out."));
        _gate.Claims.Enqueue(PostClicked());

        await Executor().RunOnceAsync(CancellationToken.None);

        var (_, _, outcome, _, result) = _gate.Records.ShouldHaveSingleItem();
        outcome.ShouldBe("failed: TaskCanceledException");
        result.ShouldStartWith("GitHub request timed out. Try again later. GitHub did not answer after the request was sent, so it may already be there");
    }

    [Theory]
    [InlineData("comment_on_pull_request", "Pull request comments", "repos/octocat/hello/issues/42/comments", "https://github.com/octocat/hello/pull/42#issuecomment-5")]
    [InlineData("comment_on_issue", "Issue comments", "repos/octocat/hello/issues/42/comments", "https://github.com/octocat/hello/pull/42#issuecomment-5")]
    [InlineData("reply_to_review_comment", "Pull request comments", "repos/octocat/hello/pulls/42/comments/99/replies", "https://github.com/octocat/hello/pull/42#discussion_r7")]
    public async Task An_Approved_Comment_Is_Posted_Exactly_As_Asked_And_Recorded_With_Its_Link(string function, string scope, string route, string url)
    {
        _github.On("POST", "repos/octocat/hello/pulls/42/comments/99/replies", HttpStatusCode.Created, """{"id":7,"html_url":"https://github.com/octocat/hello/pull/42#discussion_r7"}""");
        _gate.Claims.Enqueue(Asked(function, scope, CommentPayload()));

        await Executor().RunOnceAsync(CancellationToken.None);

        _gate.Records.ShouldHaveSingleItem().ShouldBe((scope, "GitHubActions." + function, "succeeded", "act-1", url));
        _gate.Signers.ShouldBe(["octocat"]);
        JsonDocument.Parse(_github.Sent("POST", route).ShouldHaveSingleItem()).RootElement.GetProperty("body").GetString().ShouldBe("Looks good once the lock is in.");
    }

    [Theory]
    [InlineData("create_issue", "New issues", "repos/octocat/hello/issues")]
    [InlineData("comment_on_pull_request", "Pull request comments", "repos/octocat/hello/issues/42/comments")]
    public async Task An_Approved_Write_GitHub_Fails_With_A_502_Is_Recorded_As_Failed_With_The_Reason(string function, string scope, string route)
    {
        _github.On("POST", route, HttpStatusCode.BadGateway, """{"message":"Server Error"}""");
        _gate.Claims.Enqueue(Asked(function, scope, function == "create_issue" ? IssuePayload() : CommentPayload()));

        await Executor().RunOnceAsync(CancellationToken.None);

        _gate.Records.ShouldHaveSingleItem().ShouldBe((scope, "GitHubActions." + function, "failed: github 502", "act-1", "GitHub is currently unavailable. Try again later."));
        _github.Sent("POST", route).Count.ShouldBe(1);
    }

    [Fact]
    public async Task An_Approved_Issue_Whose_Request_Times_Out_Is_Recorded_As_Failed_Saying_It_May_Already_Be_There()
    {
        _github.Throw("POST", "repos/octocat/hello/issues", new TaskCanceledException("The request timed out."));
        _gate.Claims.Enqueue(Asked("create_issue", "New issues", IssuePayload()));

        await Executor().RunOnceAsync(CancellationToken.None);

        var (_, _, outcome, _, result) = _gate.Records.ShouldHaveSingleItem();
        outcome.ShouldBe("failed: TaskCanceledException");
        result.ShouldBe("GitHub request timed out. Try again later. GitHub did not answer after the request was sent, so it may already be there: check the repository before asking again.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("""{"owner":"octocat","repo":"hello"}""")]
    public async Task An_Approved_Issue_Whose_Payload_Cannot_Be_Read_Is_Recorded_As_Failed_And_Nothing_Is_Sent(string? payload)
    {
        _gate.Claims.Enqueue(Asked("create_issue", "New issues", payload));

        await Executor().RunOnceAsync(CancellationToken.None);

        _gate.Records.ShouldHaveSingleItem().ShouldBe(("New issues", "GitHubActions.create_issue", "failed: refused", "act-1", GitHubWriteRunner.Unreadable));
        _github.Order.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Verdict_Whose_Grant_Is_Gone_When_It_Is_About_To_Be_Sent_Is_Not_Sent_Nor_Recorded()
    {
        _gate.Claims.Enqueue(Verdict(CardPayload()));
        _gate.Granted = false;

        await Executor().RunOnceAsync(CancellationToken.None);

        _gate.GrantReads.ShouldBe(["act-1"]);
        _github.Sent("POST", "repos/octocat/hello/pulls/42/reviews").ShouldBeEmpty();
        _gate.Records.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("submit_pull_request_review", "Pull request reviews", true)]
    [InlineData("submit_pull_request_review", "Pull request reviews", false)]
    [InlineData("create_issue", "New issues", true)]
    [InlineData("comment_on_pull_request", "Pull request comments", true)]
    [InlineData("reply_to_review_comment", "Pull request comments", false)]
    [InlineData("comment_on_issue", "Issue comments", false)]
    public async Task An_Approved_Review_Issue_Or_Comment_Whose_Grant_Is_Gone_Or_Cannot_Be_Read_Just_Before_The_Send_Is_Not_Sent_Nor_Recorded(
        string function, string scope, bool revoked)
    {
        _gate.Claims.Enqueue(Asked(function, scope, function switch { "create_issue" => IssuePayload(), "submit_pull_request_review" => ReviewArguments(), _ => CommentPayload() }));
        _gate.Granted = !revoked;
        _gate.GrantFailure = revoked ? null : new RpcException(new Status(StatusCode.Unavailable, "control plane down"));

        await Executor().RunOnceAsync(CancellationToken.None);

        _gate.GrantReads.ShouldBe(["act-1"]);
        _github.Order.ShouldBeEmpty();
        _gate.Records.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Grant_That_Cannot_Be_Read_Just_Before_The_Send_Sends_Nothing_And_Records_Nothing()
    {
        _gate.Claims.Enqueue(Verdict(CardPayload()));
        _gate.GrantFailure = new RpcException(new Status(StatusCode.Unavailable, "control plane down"));
        var logger = new RecordingLogger();

        await Executor(logger).RunOnceAsync(CancellationToken.None);

        _github.Sent("POST", "repos/octocat/hello/pulls/42/reviews").ShouldBeEmpty();
        _gate.Records.ShouldBeEmpty();
        logger.Messages.ShouldBe(["Scout could not read GitHub's write permission before sending action act-1 (RpcException), so it sent nothing."]);
    }

    [Fact]
    public async Task A_Sent_Verdict_Is_Recorded_With_The_Login_That_Signed_It()
    {
        _gate.Claims.Enqueue(Verdict(CardPayload()));

        await Executor().RunOnceAsync(CancellationToken.None);

        _gate.Records.ShouldHaveSingleItem().Outcome.ShouldBe("succeeded");
        _gate.Signers.ShouldBe(["octocat"]);
    }

    [Fact]
    public async Task A_Verdict_The_Model_Asked_For_In_Chat_Runs_From_The_Function_s_Arguments()
    {
        _gate.Claims.Enqueue(Verdict("""{"body":"Fix the sum.","comments_json":"[]","commit":"0123456","number":"42","owner":"octocat","repo":"hello","verdict":"request_changes"}"""));

        await Executor().RunOnceAsync(CancellationToken.None);

        _gate.Records.ShouldHaveSingleItem().Outcome.ShouldBe("succeeded");
        JsonDocument.Parse(_github.Sent("POST", "repos/octocat/hello/pulls/42/reviews").ShouldHaveSingleItem()).RootElement
            .GetProperty("event").GetString().ShouldBe("REQUEST_CHANGES");
        _github.Sent("POST", "repos/octocat/hello/issues/42/comments").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Pull_Request_That_Moved_Past_The_Commit_Reviewed_Gets_No_Verdict()
    {
        _gate.Claims.Enqueue(Verdict(CardPayload(commit: "fedcba9876543210fedcba9876543210fedcba98")));

        await Executor().RunOnceAsync(CancellationToken.None);

        _gate.Records.ShouldHaveSingleItem().ShouldBe(("Pull request verdicts", "octocat/hello#42", "failed: refused", "act-1", "The pull request changed since the review; review it again."));
        _github.Sent("POST", "repos/octocat/hello/pulls/42/reviews").ShouldBeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "", "failed: github 403", "GitHub denied the request (insufficient token permissions).")]
    [InlineData(HttpStatusCode.UnprocessableEntity, """{"message":"Unprocessable Entity","errors":["Can not approve your own pull request"]}""", "failed: github 422", "GitHub rejected the write: Can not approve your own pull request.")]
    public async Task GitHub_Refusing_The_Verdict_Is_Recorded_As_Failed_With_The_Sentence_And_No_Tests_Line(
        HttpStatusCode status, string reply, string outcome, string sentence)
    {
        _github.On("POST", "repos/octocat/hello/pulls/42/reviews", status, reply);
        _gate.Claims.Enqueue(Verdict(CardPayload()));

        await Executor().RunOnceAsync(CancellationToken.None);

        _gate.Records.ShouldHaveSingleItem().ShouldBe(("Pull request verdicts", "octocat/hello#42", outcome, "act-1", sentence));
        _github.Sent("POST", "repos/octocat/hello/issues/42/comments").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Timeout_Before_The_Verdict_Left_Is_Recorded_As_Failed_Without_Saying_It_May_Be_There()
    {
        _github.Throw("GET", "repos/octocat/hello/pulls/42", new TaskCanceledException("timed out"));
        _gate.Claims.Enqueue(Verdict(CardPayload()));

        await Executor().RunOnceAsync(CancellationToken.None);

        _gate.Records.ShouldHaveSingleItem().ShouldBe(("Pull request verdicts", "octocat/hello#42", "failed: TaskCanceledException", "act-1", "GitHub request timed out. Try again later."));
        _github.Sent("POST", "repos/octocat/hello/pulls/42/reviews").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Timeout_After_The_Verdict_Left_Is_Recorded_As_Failed_Saying_It_May_Already_Be_There()
    {
        _github.Throw("POST", "repos/octocat/hello/pulls/42/reviews", new TaskCanceledException("timed out"));
        _gate.Claims.Enqueue(Verdict(CardPayload()));

        await Executor().RunOnceAsync(CancellationToken.None);

        var (_, _, outcome, _, result) = _gate.Records.ShouldHaveSingleItem();
        outcome.ShouldBe("failed: TaskCanceledException");
        result.ShouldBe("GitHub request timed out. Try again later. GitHub did not answer after the request was sent, so it may already be there: check the repository before asking again.");
    }

    [Fact]
    public async Task A_Verdict_Sent_Before_A_Crash_Is_Found_On_GitHub_And_Recorded_As_Given_Without_A_Second_Post()
    {
        _github.On("GET", "repos/octocat/hello/pulls/42/reviews?per_page=100&page=1", HttpStatusCode.OK,
            $$$"""[{"id":88,"user":{"login":"octocat"},"state":"APPROVED","commit_id":"{{{Sha}}}","html_url":"{{{ReviewUrl}}}"}]""");
        _gate.Claims.Enqueue(Verdict(CardPayload()));

        await Executor().RunOnceAsync(CancellationToken.None);

        _gate.Records.ShouldHaveSingleItem().ShouldBe(("Pull request verdicts", "octocat/hello#42", "succeeded", "act-1", ReviewUrl));
        _github.Sent("POST", "repos/octocat/hello/pulls/42/reviews").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Verdict_Given_Before_A_Crash_Is_Found_On_The_Second_Page_Of_Reviews_And_Not_Posted_Again()
    {
        const string Reviews = "repos/octocat/hello/pulls/42/reviews";
        _github.On("GET", Reviews + "?per_page=100&page=1", HttpStatusCode.OK,
            """[{"id":1,"user":{"login":"bob"},"state":"COMMENTED","commit_id":"x","html_url":"u1"}]""",
            $"<https://api.github.com/{Reviews}?per_page=100&page=2>; rel=\"next\", <https://api.github.com/{Reviews}?per_page=100&page=2>; rel=\"last\"");
        _github.On("GET", Reviews + "?per_page=100&page=2", HttpStatusCode.OK,
            $$$"""[{"id":88,"user":{"login":"octocat"},"state":"APPROVED","commit_id":"{{{Sha}}}","html_url":"{{{ReviewUrl}}}"}]""");
        _gate.Claims.Enqueue(Verdict(CardPayload()));

        await Executor().RunOnceAsync(CancellationToken.None);

        _gate.Records.ShouldHaveSingleItem().ShouldBe(("Pull request verdicts", "octocat/hello#42", "succeeded", "act-1", ReviewUrl));
        _github.Sent("POST", Reviews).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("octocat", false)]
    [InlineData("OCTOCAT", false)]
    [InlineData("alice", true)]
    public async Task A_Verdict_Found_Already_Given_Posts_Its_Tests_Line_Only_When_The_Signer_Has_Not_Posted_It(string commenter, bool posted)
    {
        _github.On("GET", "repos/octocat/hello/pulls/42/reviews?per_page=100&page=1", HttpStatusCode.OK,
            $$$"""[{"id":88,"user":{"login":"octocat"},"state":"APPROVED","commit_id":"{{{Sha}}}","html_url":"{{{ReviewUrl}}}"}]""");
        _github.On("GET", "repos/octocat/hello/issues/42/comments?per_page=100&page=1", HttpStatusCode.OK,
            $$$"""[{"user":{"login":"{{{commenter}}}"},"body":"Approved.\n\nTests: 3 checks passed.","created_at":"2026-11-07T10:00:00Z"}]""");
        _gate.Claims.Enqueue(Verdict(CardPayload()));

        await Executor().RunOnceAsync(CancellationToken.None);

        _github.Sent("POST", "repos/octocat/hello/issues/42/comments").Count.ShouldBe(posted ? 1 : 0);
        _gate.Records.ShouldHaveSingleItem().Outcome.ShouldBe("succeeded");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task A_Failed_Read_Of_The_Comments_Posts_No_Tests_Line_And_Logs_It(HttpStatusCode status)
    {
        _github.On("GET", "repos/octocat/hello/pulls/42/reviews?per_page=100&page=1", HttpStatusCode.OK,
            $$$"""[{"id":88,"user":{"login":"octocat"},"state":"APPROVED","commit_id":"{{{Sha}}}","html_url":"{{{ReviewUrl}}}"}]""");
        _github.On("GET", "repos/octocat/hello/issues/42/comments?per_page=100&page=1", status, "");
        _gate.Claims.Enqueue(Verdict(CardPayload()));
        var logger = new RecordingLogger();

        await Executor(logger).RunOnceAsync(CancellationToken.None);

        _github.Sent("POST", "repos/octocat/hello/issues/42/comments").ShouldBeEmpty();
        logger.Messages.ShouldContain("The verdict of GitHub action act-1 was given, but its tests line was not posted (GitHubApiException).");
    }

    [Fact]
    public async Task A_Read_Of_The_Comments_That_Times_Out_Posts_No_Tests_Line_And_Logs_It()
    {
        _github.On("GET", "repos/octocat/hello/pulls/42/reviews?per_page=100&page=1", HttpStatusCode.OK,
            $$$"""[{"id":88,"user":{"login":"octocat"},"state":"APPROVED","commit_id":"{{{Sha}}}","html_url":"{{{ReviewUrl}}}"}]""");
        _github.Throw("GET", "repos/octocat/hello/issues/42/comments?per_page=100&page=1", new TaskCanceledException("timed out"));
        _gate.Claims.Enqueue(Verdict(CardPayload()));
        var logger = new RecordingLogger();

        await Executor(logger).RunOnceAsync(CancellationToken.None);

        _github.Sent("POST", "repos/octocat/hello/issues/42/comments").ShouldBeEmpty();
        logger.Messages.ShouldContain("The verdict of GitHub action act-1 was given, but its tests line was not posted (TaskCanceledException).");
    }

    [Fact]
    public async Task A_Read_Of_The_Reviews_Given_That_Fails_Sends_No_Verdict_And_Records_Why()
    {
        _github.On("GET", "repos/octocat/hello/pulls/42/reviews?per_page=100&page=1", HttpStatusCode.Forbidden, "");
        _gate.Claims.Enqueue(Verdict(CardPayload()));

        await Executor().RunOnceAsync(CancellationToken.None);

        _gate.Records.ShouldHaveSingleItem().ShouldBe(("Pull request verdicts", "octocat/hello#42", "failed: github 403", "act-1", "GitHub denied the request (insufficient token permissions)."));
        _github.Sent("POST", "repos/octocat/hello/pulls/42/reviews").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Record_The_Control_Plane_Refuses_After_A_Revoke_Posts_No_Tests_Line()
    {
        _gate.RecordFailure = new RpcException(new Status(StatusCode.FailedPrecondition, "The owner revoked github's write permission before this action ran."));
        _gate.Claims.Enqueue(Verdict(CardPayload()));

        await Executor().RunOnceAsync(CancellationToken.None);

        _github.Sent("POST", "repos/octocat/hello/pulls/42/reviews").ShouldHaveSingleItem();
        _github.Sent("POST", "repos/octocat/hello/issues/42/comments").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Tests_Line_GitHub_Refuses_Never_Fails_The_Verdict_Given()
    {
        _github.On("POST", "repos/octocat/hello/issues/42/comments", HttpStatusCode.Forbidden, "");
        _gate.Claims.Enqueue(Verdict(CardPayload()));
        var logger = new RecordingLogger();

        await Executor(logger).RunOnceAsync(CancellationToken.None);

        _gate.Records.ShouldHaveSingleItem().Outcome.ShouldBe("succeeded");
        logger.Messages.ShouldContain("The verdict of GitHub action act-1 was given, but its tests line was not posted (GitHubApiException).");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("""{"owner":"octocat"}""")]
    public async Task A_Payload_That_Cannot_Be_Read_Is_Recorded_As_Failed_And_Nothing_Is_Sent(string? payload)
    {
        _gate.Claims.Enqueue(Verdict(payload));

        await Executor().RunOnceAsync(CancellationToken.None);

        _gate.Records.ShouldHaveSingleItem().ShouldBe(("Pull request verdicts", "octocat/hello#42", "failed: refused", "act-1", GitHubVerdictRunner.Unreadable));
        _github.Order.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_Action_On_Another_Row_Is_Recorded_As_Failed_Without_A_Call()
    {
        _gate.Claims.Enqueue(Asked("run_pull_request_tests", "Local container", CardPayload()));

        await Executor().RunOnceAsync(CancellationToken.None);

        _gate.Records.ShouldHaveSingleItem().ShouldBe(("Local container", "GitHubActions.run_pull_request_tests", "failed: refused", "act-1", "Scout does not run Local container on GitHub."));
        _github.Order.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Control_Plane_That_Cannot_Be_Reached_Leaves_Nothing_Run_Or_Recorded()
    {
        _gate.ClaimFailure = new RpcException(new Status(StatusCode.Unavailable, "down"));

        await Executor().RunOnceAsync(CancellationToken.None);

        _gate.Records.ShouldBeEmpty();
        _github.Order.ShouldBeEmpty();
    }

    [Fact]
    public async Task Three_Approved_Actions_Are_Sent_One_After_The_Other_Within_One_Tick_With_At_Least_A_Second_Between_Two_Sends()
    {
        var clock = new SignallingClock();
        var started = clock.GetUtcNow();
        var second = TimeSpan.FromSeconds(1);
        _gate.Clock = clock;
        _github.On("POST", "repos/octocat/hello/issues", HttpStatusCode.Created, """{"number":12,"html_url":"https://github.com/octocat/hello/issues/12"}""");
        _github.On("POST", "repos/octocat/hello/pulls/42/comments/99/replies", HttpStatusCode.BadGateway, """{"message":"Server Error"}""");
        _gate.Claims.Enqueue(Asked("create_issue", "New issues", IssuePayload()));
        _gate.Claims.Enqueue(Asked("reply_to_review_comment", "Pull request comments", CommentPayload()) with { Id = "act-2" });
        _gate.Claims.Enqueue(Asked("comment_on_issue", "Issue comments", CommentPayload()) with { Id = "act-3" });
        using var executor = Executor(time: clock);
        int Posts() => _github.Order.Count(request => request.StartsWith("POST ", StringComparison.Ordinal));

        await executor.StartAsync(CancellationToken.None);
        Should.CompleteIn(clock.Timer(2), TimeSpan.FromSeconds(5), "the executor did not pause after the first send");
        clock.Advance(second - TimeSpan.FromMilliseconds(1));
        var sentBeforeTheSecondHasPassed = (Posts(), _gate.ClaimTimes.Count);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Should.CompleteIn(clock.Timer(3), TimeSpan.FromSeconds(5), "the executor did not pause after the second send");
        var sentAfterOneSecond = Posts();
        clock.Advance(second);
        Should.CompleteIn(clock.Timer(4), TimeSpan.FromSeconds(5), "the executor did not pause after the third send");
        clock.Advance(second);
        Should.CompleteIn(_gate.Claim(4), TimeSpan.FromSeconds(5), "the executor did not look for a fourth action");
        await executor.StopAsync(CancellationToken.None);

        sentBeforeTheSecondHasPassed.ShouldBe((1, 1), "the second action is not taken until a second has passed since the first send");
        sentAfterOneSecond.ShouldBe(2);
        _gate.Records.Select(r => (r.ActionId, r.Outcome)).ShouldBe([("act-1", "succeeded"), ("act-2", "failed: github 502"), ("act-3", "succeeded")]);
        _gate.ClaimTimes.ShouldBe([started, started + second, started + (2 * second), started + (3 * second)]);
        (3 * second).ShouldBeLessThan(GitHubActionExecutor.Interval, "all three are sent within one tick");
    }

    [Fact]
    public async Task A_Stop_During_The_Pause_Between_Two_Sends_Ends_The_Loop_At_Once_Without_Another_Send()
    {
        var clock = new SignallingClock();
        _gate.Clock = clock;
        _github.On("POST", "repos/octocat/hello/issues", HttpStatusCode.Created, """{"number":12,"html_url":"https://github.com/octocat/hello/issues/12"}""");
        _gate.Claims.Enqueue(Asked("create_issue", "New issues", IssuePayload()));
        _gate.Claims.Enqueue(Asked("create_issue", "New issues", IssuePayload()) with { Id = "act-2" });
        using var executor = Executor(time: clock);

        await executor.StartAsync(CancellationToken.None);
        Should.CompleteIn(clock.Timer(2), TimeSpan.FromSeconds(5), "the executor did not pause after the first send");
        Should.CompleteIn(executor.StopAsync(CancellationToken.None), TimeSpan.FromSeconds(5), "the stop did not end the pause at once");

        executor.ExecuteTask.ShouldNotBeNull().IsCompletedSuccessfully.ShouldBeTrue();
        _github.Sent("POST", "repos/octocat/hello/issues").Count.ShouldBe(1);
        _gate.Claims.ShouldHaveSingleItem().ShouldNotBeNull().Id.ShouldBe("act-2");
    }

    [Fact]
    public async Task An_Action_Refused_Before_Anything_Is_Sent_Needs_No_Pause_Before_The_Next_One()
    {
        var clock = new SignallingClock();
        var started = clock.GetUtcNow();
        _gate.Clock = clock;
        _github.On("POST", "repos/octocat/hello/issues", HttpStatusCode.Created, """{"number":12,"html_url":"https://github.com/octocat/hello/issues/12"}""");
        _gate.Claims.Enqueue(Asked("create_issue", "New issues", payload: null));
        _gate.Claims.Enqueue(Asked("run_pull_request_tests", "Local container", CardPayload()) with { Id = "act-2" });
        _gate.Claims.Enqueue(Asked("create_issue", "New issues", IssuePayload()) with { Id = "act-3" });
        using var executor = Executor(time: clock);

        await executor.StartAsync(CancellationToken.None);
        Should.CompleteIn(clock.Timer(2), TimeSpan.FromSeconds(5), "the executor did not pause after the one send");
        await executor.StopAsync(CancellationToken.None);

        _gate.Records.Select(r => (r.ActionId, r.Outcome)).ShouldBe([("act-1", "failed: refused"), ("act-2", "failed: refused"), ("act-3", "succeeded")]);
        _gate.ClaimTimes.ShouldBe([started, started, started]);
    }

    [Fact]
    public async Task An_Action_Whose_Record_Cannot_Be_Written_Ends_The_Run_Until_The_Next_Tick_So_The_Loop_Never_Spins()
    {
        var clock = new FakeTimeProvider();
        var started = clock.GetUtcNow();
        _gate.Clock = clock;
        _github.On("POST", "repos/octocat/hello/issues", HttpStatusCode.Created, """{"number":12,"html_url":"https://github.com/octocat/hello/issues/12"}""");
        _gate.Claims.Enqueue(Asked("create_issue", "New issues", IssuePayload()));
        _gate.Claims.Enqueue(Asked("comment_on_issue", "Issue comments", CommentPayload()) with { Id = "act-2" });
        _gate.RecordFailure = new RpcException(new Status(StatusCode.Unavailable, "down"));
        using var executor = Executor(time: clock);

        await executor.StartAsync(CancellationToken.None);
        Should.CompleteIn(_gate.Record(1), TimeSpan.FromSeconds(5), "the claimed action was never recorded");
        clock.Advance(GitHubActionExecutor.Interval);
        Should.CompleteIn(_gate.Record(2), TimeSpan.FromSeconds(5), "the next tick did not run the next action");
        await executor.StopAsync(CancellationToken.None);

        _gate.ClaimTimes.Take(2).ShouldBe([started, started + GitHubActionExecutor.Interval]);
        _gate.Records.Take(2).Select(r => r.ActionId).ShouldBe(["act-1", "act-2"]);
    }

    private sealed class SignallingClock : FakeTimeProvider
    {
        private readonly List<TaskCompletionSource> _timers = [];
        private int _created;

        public Task Timer(int number) => Nth(number).Task;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Nth(Interlocked.Increment(ref _created)).TrySetResult();
            return timer;
        }

        private TaskCompletionSource Nth(int number)
        {
            lock (_timers)
            {
                while (_timers.Count < number)
                {
                    _timers.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                }

                return _timers[number - 1];
            }
        }
    }

    private sealed class FakeGate : IWriteGate, IApprovedActions
    {
        private readonly List<TaskCompletionSource> _claims = [];
        private readonly List<TaskCompletionSource> _records = [];
        private int _recorded;

        public TimeProvider Clock { get; set; } = TimeProvider.System;

        public List<DateTimeOffset> ClaimTimes { get; } = [];

        public Task Claim(int number) => Nth(_claims, number).Task;

        public Task Record(int number) => Nth(_records, number).Task;

        private static TaskCompletionSource Nth(List<TaskCompletionSource> signals, int number)
        {
            lock (signals)
            {
                while (signals.Count < number)
                {
                    signals.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                }

                return signals[number - 1];
            }
        }

        public Queue<ApprovedAction?> Claims { get; } = new();

        public Exception? ClaimFailure { get; set; }

        public Exception? RecordFailure { get; set; }

        public List<(string Scope, string Target, string Outcome, string? ActionId, string? Result)> Records { get; } = [];

        public List<string?> Signers { get; } = [];

        public bool Granted { get; set; } = true;

        public Exception? GrantFailure { get; set; }

        public List<string> GrantReads { get; } = [];

        public int RecordedAtStep { get; private set; } = -1;

        public RoutingHandler? Steps { get; set; }

        public Task<ApprovedAction?> ClaimNextAsync(string featureId, CancellationToken cancellationToken)
        {
            featureId.ShouldBe("github");
            cancellationToken.ThrowIfCancellationRequested();
            int taken;
            lock (ClaimTimes)
            {
                ClaimTimes.Add(Clock.GetUtcNow());
                taken = ClaimTimes.Count;
            }

            Nth(_claims, taken).TrySetResult();
            return ClaimFailure is not null ? throw ClaimFailure : Task.FromResult(Claims.Count == 0 ? null : Claims.Dequeue());
        }

        public Task<bool> CheckGrantAsync(ApprovedAction action, CancellationToken cancellationToken)
        {
            GrantReads.Add(action.Id);
            return GrantFailure is not null ? throw GrantFailure : Task.FromResult(Granted);
        }

        public Task<WriteGateDecision> RequestAsync(string featureId, string scope, string target, string summary, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The executor never asks the gate for a write.");

        public Task RecordAsync(string featureId, string scope, string target, string outcome, string? actionId, CancellationToken cancellationToken) =>
            RecordAsync(featureId, scope, target, outcome, actionId, null, cancellationToken);

        public Task RecordAsync(string featureId, string scope, string target, string outcome, string? actionId, string? result, CancellationToken cancellationToken) =>
            RecordAsync(featureId, scope, target, outcome, actionId, result, null, cancellationToken);

        public Task RecordAsync(string featureId, string scope, string target, string outcome, string? actionId, string? result, string? signedAs, CancellationToken cancellationToken)
        {
            Signers.Add(signedAs);
            featureId.ShouldBe("github");
            Records.Add((scope, target, outcome, actionId, result));
            RecordedAtStep = Records.Count == 1 ? Steps?.Order.Count ?? -1 : RecordedAtStep;
            Nth(_records, Interlocked.Increment(ref _recorded)).TrySetResult();
            return RecordFailure is null ? Task.CompletedTask : throw RecordFailure;
        }
    }

    private sealed class RecordingLogger : ILogger<GitHubActionExecutor>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    internal sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);
        private readonly List<(string Route, string Body)> _sent = [];

        public List<string> Order { get; } = [];

        public void On(string method, string path, HttpStatusCode status, string json, string? link = null) =>
            _routes[$"{method} {path}"] = () =>
            {
                var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                if (link is not null)
                {
                    response.Headers.Add("Link", link);
                }

                return response;
            };

        public void Throw(string method, string path, Exception exception) =>
            _routes[$"{method} {path}"] = () => throw exception;

        public IReadOnlyList<string> Sent(string method, string path) =>
            _sent.Where(s => s.Route == $"{method} {path}").Select(s => s.Body).ToList();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var route = $"{request.Method.Method} {request.RequestUri!.PathAndQuery.TrimStart('/')}";
            Order.Add(route);
            _sent.Add((route, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return _routes.TryGetValue(route, out var reply) ? reply() : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
