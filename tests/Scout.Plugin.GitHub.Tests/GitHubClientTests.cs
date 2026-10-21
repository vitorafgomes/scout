using System.Net;
using Scout.Plugin.GitHub;
using Scout.Plugin.GitHub.Abstractions;
using Scout.Plugin.GitHub.Tests.Fakes;

namespace Scout.Plugin.GitHub.Tests;

public sealed class GitHubClientTests
{
    private static (GitHubClient client, StubHttpMessageHandler handler) Build()
    {
        var handler = new StubHttpMessageHandler();
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.github.com/"),
        };
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-token");
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return (new GitHubClient(http), handler);
    }

    [Fact]
    public async Task ListRepositories_Sends_Expected_Headers()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, "[]");

        await client.ListRepositoriesAsync(CancellationToken.None);

        var req = handler.LastRequest.ShouldNotBeNull();
        req.Headers.Authorization!.Scheme.ShouldBe("Bearer");
        req.Headers.Authorization!.Parameter.ShouldBe("test-token");
        req.Headers.UserAgent.ToString().ShouldContain("Scout");
        req.Headers.Accept.ToString().ShouldContain("application/vnd.github+json");
        req.RequestUri!.ToString().ShouldContain("user/repos");
    }

    [Fact]
    public async Task ListRepositories_Parses_Items()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK,
            """[{"full_name":"octocat/hello","private":false},{"full_name":"acme/secret","private":true}]""");

        var repos = await client.ListRepositoriesAsync(CancellationToken.None);

        repos.Count.ShouldBe(2);
        repos[0].ShouldBe(new GitHubRepository("octocat/hello", false));
        repos[1].ShouldBe(new GitHubRepository("acme/secret", true));
    }

    [Fact]
    public async Task ListOpenPullRequests_Parses_Items()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK,
            """[{"number":7,"title":"Fix bug","user":{"login":"alice"}}]""");

        var prs = await client.ListOpenPullRequestsAsync("octocat", "hello", CancellationToken.None);

        prs.Count.ShouldBe(1);
        prs[0].ShouldBe(new GitHubPullRequest(7, "Fix bug", "alice"));
        handler.LastRequest!.RequestUri!.ToString()
            .ShouldContain("repos/octocat/hello/pulls");
    }

    [Fact]
    public async Task ListOpenIssues_Filters_Out_Pull_Requests()
    {
        var (client, handler) = Build();
        // GitHub's issues endpoint also returns PRs; PR-backed items carry a
        // "pull_request" field and must be excluded.
        handler.EnqueueJson(HttpStatusCode.OK,
            """
            [
              {"number":1,"title":"Real issue","user":{"login":"bob"}},
              {"number":2,"title":"A PR","user":{"login":"carol"},"pull_request":{"url":"x"}}
            ]
            """);

        var issues = await client.ListOpenIssuesAsync("octocat", "hello", CancellationToken.None);

        issues.Count.ShouldBe(1);
        issues[0].ShouldBe(new GitHubIssue(1, "Real issue", "bob"));
    }

    [Fact]
    public async Task Missing_Author_Defaults_To_Unknown()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK,
            """[{"number":3,"title":"No author"}]""");

        var issues = await client.ListOpenIssuesAsync("octocat", "hello", CancellationToken.None);

        issues[0].AuthorLogin.ShouldBe("unknown");
    }

    [Fact]
    public async Task Unauthorized_Throws_Auth_Message()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.Unauthorized, "{}");

        var ex = await Should.ThrowAsync<GitHubApiException>(
            () => client.ListRepositoriesAsync(CancellationToken.None));

        ex.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        ex.Message.ShouldContain("token");
    }

    [Fact]
    public async Task RateLimited_Forbidden_Throws_RateLimit_Message()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.Forbidden, "{}",
            r => r.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0"));

        var ex = await Should.ThrowAsync<GitHubApiException>(
            () => client.ListRepositoriesAsync(CancellationToken.None));

        ex.Message.ShouldContain("rate limit");
    }

    [Fact]
    public async Task Forbidden_Without_RateLimit_Throws_Permissions_Message()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.Forbidden, "{}");

        var ex = await Should.ThrowAsync<GitHubApiException>(
            () => client.ListRepositoriesAsync(CancellationToken.None));

        ex.Message.ShouldContain("permission");
    }

    [Fact]
    public async Task NotFound_Throws_NotFound_Message()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.NotFound, "{}");

        var ex = await Should.ThrowAsync<GitHubApiException>(
            () => client.ListOpenIssuesAsync("octocat", "ghost", CancellationToken.None));

        ex.Message.ShouldContain("not found");
    }

    [Fact]
    public async Task ServerError_Throws_Unavailable_Message()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.InternalServerError, "{}");

        var ex = await Should.ThrowAsync<GitHubApiException>(
            () => client.ListRepositoriesAsync(CancellationToken.None));

        ex.Message.ShouldContain("unavailable");
    }

    [Fact]
    public async Task Timeout_Propagates_As_OperationCanceled()
    {
        var (client, handler) = Build();
        handler.EnqueueThrow(new TaskCanceledException("timed out"));

        await Should.ThrowAsync<TaskCanceledException>(
            () => client.ListRepositoriesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Transport_Failure_Propagates_As_HttpRequestException()
    {
        var (client, handler) = Build();
        handler.EnqueueThrow(new HttpRequestException("connection reset"));

        await Should.ThrowAsync<HttpRequestException>(
            () => client.ListRepositoriesAsync(CancellationToken.None));
    }
}
