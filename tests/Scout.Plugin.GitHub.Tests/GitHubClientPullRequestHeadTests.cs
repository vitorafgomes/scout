using System.Net;
using Scout.Plugin.GitHub.Abstractions;
using Scout.Plugin.GitHub.Tests.Fakes;

namespace Scout.Plugin.GitHub.Tests;

public sealed class GitHubClientPullRequestHeadTests : IDisposable
{
    private readonly StubHttpMessageHandler _handler = new();

    public void Dispose() => _handler.Dispose();

    private GitHubClient Client() => new(new HttpClient(_handler) { BaseAddress = new Uri("https://api.github.com/") });

    [Fact]
    public async Task The_Head_Of_A_Pull_Request_Is_Its_Head_Commit_And_Its_Author_In_One_Read()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, """{"number":42,"user":{"login":"alice"},"head":{"ref":"feat/x","sha":"0123456789abcdef"}}""");

        var head = await Client().GetPullRequestHeadAsync("octocat", "hello", 42, CancellationToken.None);

        head.ShouldBe(new PullRequestHead("0123456789abcdef", "alice"));
        _handler.LastRequest!.Method.ShouldBe(HttpMethod.Get);
        _handler.LastRequest.RequestUri!.ToString().ShouldBe("https://api.github.com/repos/octocat/hello/pulls/42");
    }

    [Fact]
    public async Task A_Pull_Request_That_Does_Not_Exist_Has_No_Head()
    {
        _handler.EnqueueJson(HttpStatusCode.NotFound, """{"message":"Not Found"}""");

        (await Client().GetPullRequestHeadAsync("octocat", "hello", 42, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task The_Signed_In_Login_Is_Read_From_The_User_Endpoint()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, """{"login":"octocat","id":1}""");

        (await Client().GetSignedInLoginAsync(CancellationToken.None)).ShouldBe("octocat");
        _handler.LastRequest!.RequestUri!.ToString().ShouldBe("https://api.github.com/user");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "GitHub denied the request (insufficient token permissions).")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "GitHub request failed (422).")]
    [InlineData(HttpStatusCode.BadGateway, "GitHub is currently unavailable. Try again later.")]
    public async Task A_Failed_Head_Or_Login_Read_Names_What_GitHub_Answered(HttpStatusCode status, string message)
    {
        _handler.EnqueueJson(status, "");
        _handler.EnqueueJson(status, "");

        (await Should.ThrowAsync<GitHubApiException>(() => Client().GetPullRequestHeadAsync("octocat", "hello", 42, CancellationToken.None))).Message.ShouldBe(message);
        (await Should.ThrowAsync<GitHubApiException>(() => Client().GetSignedInLoginAsync(CancellationToken.None))).Message.ShouldBe(message);
    }

    [Fact]
    public async Task A_Head_Or_Login_Read_That_Times_Out_Bubbles_For_The_Caller()
    {
        _handler.EnqueueThrow(new TaskCanceledException("timed out"));
        _handler.EnqueueThrow(new TaskCanceledException("timed out"));

        await Should.ThrowAsync<TaskCanceledException>(() => Client().GetPullRequestHeadAsync("octocat", "hello", 42, CancellationToken.None));
        await Should.ThrowAsync<TaskCanceledException>(() => Client().GetSignedInLoginAsync(CancellationToken.None));
    }
}
