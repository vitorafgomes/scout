using Scout.Plugin.GitHub;
using Scout.Plugin.GitHub.Abstractions;

namespace Scout.Plugin.GitHub.Tests;

public sealed class GitHubPluginTests
{
    private static IGitHubClient Client() => Substitute.For<IGitHubClient>();

    private static GitHubPlugin Plugin(IGitHubClient client) => new(client);

    [Fact]
    public async Task ListRepositories_Formats_Repositories()
    {
        var client = Client();
        client.ListRepositoriesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<GitHubRepository>
            {
                new("octocat/hello-world", Private: false),
                new("octocat/secret", Private: true),
            });

        var result = await Plugin(client).ListRepositoriesAsync(CancellationToken.None);

        result.ShouldContain("octocat/hello-world");
        result.ShouldContain("octocat/secret (private)");
    }

    [Fact]
    public async Task ListRepositories_Empty_Returns_Message()
    {
        var client = Client();
        client.ListRepositoriesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<GitHubRepository>());

        var result = await Plugin(client).ListRepositoriesAsync(CancellationToken.None);

        result.ShouldBe("No repositories.");
    }

    [Fact]
    public async Task ListPullRequests_Formats_PullRequests()
    {
        var client = Client();
        client.ListOpenPullRequestsAsync("octocat", "hello-world", Arg.Any<CancellationToken>())
            .Returns(new List<GitHubPullRequest>
            {
                new(42, "Add feature", "alice"),
                new(7, "Fix bug", "bob"),
            });

        var result = await Plugin(client)
            .ListPullRequestsAsync("octocat", "hello-world", CancellationToken.None);

        result.ShouldContain("#42 Add feature (by alice)");
        result.ShouldContain("#7 Fix bug (by bob)");
    }

    [Fact]
    public async Task ListPullRequests_Empty_Returns_Message()
    {
        var client = Client();
        client.ListOpenPullRequestsAsync("octocat", "hello-world", Arg.Any<CancellationToken>())
            .Returns(new List<GitHubPullRequest>());

        var result = await Plugin(client)
            .ListPullRequestsAsync("octocat", "hello-world", CancellationToken.None);

        result.ShouldBe("No open pull requests in octocat/hello-world.");
    }

    [Fact]
    public async Task ListIssues_Formats_Issues()
    {
        var client = Client();
        client.ListOpenIssuesAsync("octocat", "hello-world", Arg.Any<CancellationToken>())
            .Returns(new List<GitHubIssue>
            {
                new(13, "Crash on start", "carol"),
            });

        var result = await Plugin(client)
            .ListIssuesAsync("octocat", "hello-world", CancellationToken.None);

        result.ShouldContain("#13 Crash on start (by carol)");
    }

    [Fact]
    public async Task ListIssues_Empty_Returns_Message()
    {
        var client = Client();
        client.ListOpenIssuesAsync("octocat", "hello-world", Arg.Any<CancellationToken>())
            .Returns(new List<GitHubIssue>());

        var result = await Plugin(client)
            .ListIssuesAsync("octocat", "hello-world", CancellationToken.None);

        result.ShouldBe("No open issues in octocat/hello-world.");
    }

    [Fact]
    public async Task ListRepositories_ApiException_Returns_Clean_Error_Without_Throwing()
    {
        var client = Client();
        client.ListRepositoriesAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<GitHubRepository>>(
                _ => throw new GitHubApiException("GitHub authentication failed."));

        var plugin = Plugin(client);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.ListRepositoriesAsync(CancellationToken.None));

        result.ShouldBe("GitHub authentication failed.");
    }

    [Fact]
    public async Task ListPullRequests_Timeout_Returns_Clean_Error_Without_Throwing()
    {
        var client = Client();
        client.ListOpenPullRequestsAsync("octocat", "hello-world", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<GitHubPullRequest>>(
                _ => throw new TaskCanceledException("timeout"));

        var plugin = Plugin(client);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.ListPullRequestsAsync(
                "octocat", "hello-world", CancellationToken.None));

        result.ShouldBe("GitHub request timed out. Try again later.");
    }

    [Fact]
    public async Task ListIssues_HttpRequestException_Returns_Clean_Error_Without_Throwing()
    {
        var client = Client();
        client.ListOpenIssuesAsync("octocat", "hello-world", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<GitHubIssue>>(
                _ => throw new HttpRequestException("connection reset"));

        var plugin = Plugin(client);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.ListIssuesAsync(
                "octocat", "hello-world", CancellationToken.None));

        result.ShouldBe("Could not reach GitHub. Try again later.");
    }

    [Fact]
    public void Ctor_Throws_When_Client_Null()
    {
        Should.Throw<ArgumentNullException>(() => new GitHubPlugin(null!));
    }
}
