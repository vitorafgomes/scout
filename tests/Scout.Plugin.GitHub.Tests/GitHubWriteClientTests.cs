using System.Net;
using System.Text.Json;
using Scout.Plugin.GitHub;
using Scout.Plugin.GitHub.Abstractions;
using Scout.Plugin.GitHub.Tests.Fakes;

namespace Scout.Plugin.GitHub.Tests;

public sealed class GitHubWriteClientTests
{
    private static (GitHubWriteClient client, StubHttpMessageHandler handler) Build()
    {
        var handler = new StubHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        return (new GitHubWriteClient(http), handler);
    }

    private static async Task<JsonElement> SentJson(StubHttpMessageHandler handler) =>
        JsonDocument.Parse(await handler.LastRequest!.Content!.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task SubmitReview_Posts_A_Commenting_Review_With_Every_Comment_On_The_Right_Side()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, """{"id":77,"html_url":"https://github.com/octocat/hello/pull/42#pullrequestreview-77"}""");

        var posted = await client.SubmitReviewAsync("octocat", "hello", 42, "Ready after one fix.",
            [new ReviewComment("src/Checkout.cs", 88, "Summed before the discount.")], CancellationToken.None);

        posted.ShouldBe(new PostedReview(77, "https://github.com/octocat/hello/pull/42#pullrequestreview-77"));
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Post);
        handler.LastRequest.RequestUri!.ToString().ShouldEndWith("repos/octocat/hello/pulls/42/reviews");
        var json = await SentJson(handler);
        json.GetProperty("event").GetString().ShouldBe("COMMENT");
        json.GetProperty("body").GetString().ShouldBe("Ready after one fix.");
        var comment = json.GetProperty("comments").EnumerateArray().Single();
        comment.GetProperty("path").GetString().ShouldBe("src/Checkout.cs");
        comment.GetProperty("line").GetInt32().ShouldBe(88);
        comment.GetProperty("side").GetString().ShouldBe("RIGHT");
        comment.GetProperty("body").GetString().ShouldBe("Summed before the discount.");
    }

    [Fact]
    public async Task CreateIssue_Posts_Title_Body_And_Labels_Only_When_Given()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.Created, """{"number":13,"html_url":"https://github.com/octocat/hello/issues/13"}""");
        handler.EnqueueJson(HttpStatusCode.Created, """{"number":14,"html_url":"u"}""");

        var created = await client.CreateIssueAsync("octocat", "hello", "CI never runs the tests", "What, why, how, where.", ["ci", "tests"], CancellationToken.None);
        var first = await SentJson(handler);
        await client.CreateIssueAsync("octocat", "hello", "Second", "Body", [], CancellationToken.None);
        var second = await SentJson(handler);

        created.ShouldBe(new CreatedIssue(13, "https://github.com/octocat/hello/issues/13"));
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Post);
        handler.LastRequest.RequestUri!.ToString().ShouldEndWith("repos/octocat/hello/issues");
        first.GetProperty("title").GetString().ShouldBe("CI never runs the tests");
        first.GetProperty("labels").EnumerateArray().Select(l => l.GetString()).ShouldBe(["ci", "tests"]);
        second.TryGetProperty("labels", out _).ShouldBeFalse("no labels means the property is left out, so GitHub keeps its default");
    }

    [Fact]
    public async Task A_422_Names_What_GitHub_Rejected()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.UnprocessableEntity,
            """{"message":"Validation Failed","errors":[{"message":"Pull request review thread line must be part of the diff"}]}""");

        var ex = await Should.ThrowAsync<GitHubApiException>(() => client.SubmitReviewAsync("octocat", "hello", 42, "b",
            [new ReviewComment("a.cs", 999, "x")], CancellationToken.None));

        ex.Message.ShouldBe("GitHub rejected the write: Pull request review thread line must be part of the diff.");
        ex.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_Server_Error_Maps_Like_The_Reads_And_A_Timeout_Bubbles()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.ServiceUnavailable, "");
        handler.EnqueueThrow(new TaskCanceledException("timed out"));

        var ex = await Should.ThrowAsync<GitHubApiException>(() => client.CreateIssueAsync("octocat", "hello", "t", "b", [], CancellationToken.None));
        ex.Message.ShouldBe("GitHub is currently unavailable. Try again later.");
        await Should.ThrowAsync<TaskCanceledException>(() => client.CreateIssueAsync("octocat", "hello", "t", "b", [], CancellationToken.None));
    }
}
