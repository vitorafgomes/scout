using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Scout.Llm.Abstractions;
using Scout.Plugin.GitHub.Abstractions;
using Scout.Plugin.GitHub.Tests.Fakes;

namespace Scout.Plugin.GitHub.Tests;

public sealed class GitHubActionsPluginCommentsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("scout-github-comments-").FullName;
    private readonly StubHttpMessageHandler _handler = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        _handler.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private GitHubActionsPlugin Plugin() => new(
        Substitute.For<IGitHubClient>(),
        new GitHubWriteClient(new HttpClient(_handler) { BaseAddress = new Uri("https://api.github.com/") }),
        new GitHubProfileStore(Path.Combine(_dir, "profiles")),
        new RepositorySnapshotStore(Path.Combine(_dir, "snapshots"), 0, _time),
        Substitute.For<IRepositoryProjects>(),
        _time);

    private async Task<string> SentBody() =>
        JsonDocument.Parse(await _handler.LastRequest!.Content!.ReadAsStringAsync()).RootElement.GetProperty("body").GetString()!;

    private static async Task<string> Refused(Func<Task<string>> call)
    {
        var ex = await Should.ThrowAsync<WriteFailedException>(call);
        ex.Outcome.ShouldBe("refused");
        return ex.Message;
    }

    public static TheoryData<string> Functions => new() { "comment_on_pull_request", "comment_on_issue", "reply_to_review_comment" };

    private Task<string> Call(string function, string body) => function switch
    {
        "comment_on_pull_request" => Plugin().CommentOnPullRequestAsync("octocat", "hello", 42, body, CancellationToken.None),
        "comment_on_issue" => Plugin().CommentOnIssueAsync("octocat", "hello", 42, body, CancellationToken.None),
        _ => Plugin().ReplyToReviewCommentAsync("octocat", "hello", 42, 9001, body, CancellationToken.None),
    };

    [Fact]
    public async Task A_Comment_On_A_Pull_Request_Is_Posted_To_Its_Conversation_With_Exactly_The_Body_Given()
    {
        _handler.EnqueueJson(HttpStatusCode.Created, """{"id":501,"html_url":"https://github.com/octocat/hello/pull/42#issuecomment-501"}""");
        const string body = "  Tests: 3 checks passed.\n\n- build\n";

        var result = await Plugin().CommentOnPullRequestAsync("octocat", "hello", 42, body, CancellationToken.None);

        result.ShouldBe("Commented on pull request octocat/hello#42: https://github.com/octocat/hello/pull/42#issuecomment-501");
        _handler.LastRequest!.Method.ShouldBe(HttpMethod.Post);
        _handler.LastRequest.RequestUri!.ToString().ShouldBe("https://api.github.com/repos/octocat/hello/issues/42/comments");
        (await SentBody()).ShouldBe(body);
    }

    [Fact]
    public async Task A_Comment_On_An_Issue_Is_Posted_To_The_Issue_With_Exactly_The_Body_Given()
    {
        _handler.EnqueueJson(HttpStatusCode.Created, """{"id":502,"html_url":"https://github.com/octocat/hello/issues/7#issuecomment-502"}""");

        var result = await Plugin().CommentOnIssueAsync("octocat", "hello", 7, "Fixed by #42.", CancellationToken.None);

        result.ShouldBe("Commented on issue octocat/hello#7: https://github.com/octocat/hello/issues/7#issuecomment-502");
        _handler.LastRequest!.RequestUri!.ToString().ShouldBe("https://api.github.com/repos/octocat/hello/issues/7/comments");
        (await SentBody()).ShouldBe("Fixed by #42.");
    }

    [Fact]
    public async Task A_Reply_To_A_Review_Comment_Is_Posted_In_Its_Thread_With_Exactly_The_Body_Given()
    {
        _handler.EnqueueJson(HttpStatusCode.Created, """{"id":503,"html_url":"https://github.com/octocat/hello/pull/42#discussion_r503"}""");

        var result = await Plugin().ReplyToReviewCommentAsync("octocat", "hello", 42, 9001, "Done in ec7f951.", CancellationToken.None);

        result.ShouldBe("Replied to review comment 9001 on octocat/hello#42: https://github.com/octocat/hello/pull/42#discussion_r503");
        _handler.LastRequest!.Method.ShouldBe(HttpMethod.Post);
        _handler.LastRequest.RequestUri!.ToString().ShouldBe("https://api.github.com/repos/octocat/hello/pulls/42/comments/9001/replies");
        (await SentBody()).ShouldBe("Done in ec7f951.");
    }

    [Theory]
    [MemberData(nameof(Functions))]
    public async Task An_Empty_Comment_Is_Refused_Before_Any_Call(string function)
    {
        (await Refused(() => Call(function, ""))).ShouldBe("The comment is empty.");
        (await Refused(() => Call(function, " \n\t "))).ShouldBe("The comment is empty.");
        _handler.LastRequest.ShouldBeNull();
    }

    [Theory]
    [MemberData(nameof(Functions))]
    public async Task A_Comment_Over_65000_Characters_Is_Refused_Before_Any_Call(string function)
    {
        (await Refused(() => Call(function, new string('x', 65_001)))).ShouldBe("The comment is over 65,000 characters.");
        _handler.LastRequest.ShouldBeNull();
    }

    [Theory]
    [MemberData(nameof(Functions))]
    public async Task A_Comment_Of_Exactly_65000_Characters_Is_Posted(string function)
    {
        _handler.EnqueueJson(HttpStatusCode.Created, """{"id":1,"html_url":"u"}""");

        await Call(function, new string('x', 65_000));

        (await SentBody()).Length.ShouldBe(65_000);
    }

    [Fact]
    public async Task A_Bad_Repository_A_Non_Positive_Number_Or_Comment_Id_Is_Refused_Before_Any_Call()
    {
        var plugin = Plugin();

        (await Refused(() => plugin.CommentOnIssueAsync("octo cat", "hello", 7, "b", CancellationToken.None))).ShouldBe(GitHubNames.InvalidRepository);
        (await Refused(() => plugin.CommentOnPullRequestAsync("octocat", "hello", 0, "b", CancellationToken.None))).ShouldBe("The pull request number must be positive.");
        (await Refused(() => plugin.CommentOnIssueAsync("octocat", "hello", -1, "b", CancellationToken.None))).ShouldBe("The issue number must be positive.");
        (await Refused(() => plugin.ReplyToReviewCommentAsync("octocat", "hello", 42, 0, "b", CancellationToken.None))).ShouldBe("The review comment id must be positive.");
        _handler.LastRequest.ShouldBeNull();
    }

    public static TheoryData<string, HttpStatusCode, string, string, string> Failures
    {
        get
        {
            var data = new TheoryData<string, HttpStatusCode, string, string, string>();
            foreach (var function in new[] { "comment_on_pull_request", "comment_on_issue", "reply_to_review_comment" })
            {
                data.Add(function, HttpStatusCode.Forbidden, "", "GitHub denied the request (insufficient token permissions).", "github 403");
                data.Add(function, HttpStatusCode.NotFound, "", "Repository not found or the token has no access.", "github 404");
                data.Add(function, HttpStatusCode.UnprocessableEntity, """{"message":"Validation Failed","errors":[{"message":"Body is too long"}]}""", "GitHub rejected the write: Body is too long.", "github 422");
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task GitHub_Refusing_A_Comment_Is_A_Sentence_For_The_Model_And_The_Status_For_The_Log(
        string function, HttpStatusCode status, string reply, string sentence, string outcome)
    {
        _handler.EnqueueJson(status, reply);

        var ex = await Should.ThrowAsync<WriteFailedException>(() => Call(function, "LGTM"));

        ex.Message.ShouldBe(sentence);
        ex.Outcome.ShouldBe(outcome);
    }

    [Theory]
    [MemberData(nameof(Functions))]
    public async Task A_Comment_That_Times_Out_Says_It_May_Already_Be_Posted(string function)
    {
        _handler.EnqueueThrow(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 30 seconds elapsing."));

        var ex = await Should.ThrowAsync<WriteFailedException>(() => Call(function, "LGTM"));

        ex.Message.ShouldBe("GitHub request timed out. Try again later." + GitHubActionsPlugin.MayAlreadyBePosted);
        ex.Outcome.ShouldBe("TaskCanceledException");
    }
}
