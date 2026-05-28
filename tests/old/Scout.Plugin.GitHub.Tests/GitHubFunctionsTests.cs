using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Octokit;
using Scout.Core.Features;
using Scout.Plugin.GitHub.Functions;
using Scout.Plugin.GitHub.Internal;
using Scout.Plugin.GitHub.Tests.Internal;

namespace Scout.Plugin.GitHub.Tests;

public class GitHubFunctionsTests
{
    [Fact]
    public async Task ListRepositories_Maps_Octokit_Response()
    {
        var client = Substitute.For<IGitHubClient>();
        var repo = OctokitFakes.Repository(defaultBranch: "main", stars: 12, openIssues: 3);
        client.Repository
            .GetAllForUser("octocat")
            .Returns(Task.FromResult<IReadOnlyList<Repository>>(new[] { repo }));

        var sut = BuildSut(client);

        var result = await sut.ListRepositoriesAsync("octocat");

        result.Success.ShouldBeTrue();
        result.Value.ShouldNotBeNull();
        result.Value!.Count.ShouldBe(1);
        result.Value[0].FullName.ShouldBe("octocat/Hello-World");
        result.Value[0].DefaultBranch.ShouldBe("main");
        result.Value[0].Stars.ShouldBe(12);
    }

    [Fact]
    public async Task ListRepositories_Uses_Org_Endpoint_When_Type_Org()
    {
        var client = Substitute.For<IGitHubClient>();
        client.Repository
            .GetAllForOrg("dotnet")
            .Returns(Task.FromResult<IReadOnlyList<Repository>>(Array.Empty<Repository>()));

        var sut = BuildSut(client);

        var result = await sut.ListRepositoriesAsync("dotnet", type: "org");

        result.Success.ShouldBeTrue();
        await client.Repository.Received(1).GetAllForOrg("dotnet");
    }

    [Fact]
    public async Task ListRepositories_Maps_NotFoundException_To_NotFound()
    {
        var client = Substitute.For<IGitHubClient>();
        client.Repository
            .GetAllForUser(Arg.Any<string>())
            .Returns<IReadOnlyList<Repository>>(_ =>
                throw new NotFoundException("nope", HttpStatusCode.NotFound));

        var sut = BuildSut(client);

        var result = await sut.ListRepositoriesAsync("ghost");

        result.Success.ShouldBeFalse();
        result.Error.ShouldBe(GitHubFunctions.ErrorNotFound);
    }

    [Fact]
    public async Task ListRepositories_Maps_AuthorizationException_To_CredentialsInvalid()
    {
        var client = Substitute.For<IGitHubClient>();
        client.Repository
            .GetAllForUser(Arg.Any<string>())
            .Returns<IReadOnlyList<Repository>>(_ => throw new AuthorizationException());

        var sut = BuildSut(client);

        var result = await sut.ListRepositoriesAsync("anyone");

        result.Success.ShouldBeFalse();
        result.Error.ShouldBe(GitHubFunctions.ErrorCredentialsInvalid);
    }

    [Fact]
    public async Task ListPullRequests_Defaults_To_Open_State()
    {
        var client = Substitute.For<IGitHubClient>();
        client.PullRequest
            .GetAllForRepository("o", "r", Arg.Any<PullRequestRequest>())
            .Returns(Task.FromResult<IReadOnlyList<PullRequest>>(Array.Empty<PullRequest>()));

        var sut = BuildSut(client);

        var result = await sut.ListPullRequestsAsync("o", "r");

        result.Success.ShouldBeTrue();
        await client.PullRequest.Received(1).GetAllForRepository(
            "o",
            "r",
            Arg.Is<PullRequestRequest>(r => r.State == ItemStateFilter.Open));
    }

    [Fact]
    public async Task GetPullRequestDetails_Maps_PR_And_Files()
    {
        var client = Substitute.For<IGitHubClient>();
        var pr = OctokitFakes.PullRequest(number: 7);
        var file = OctokitFakes.PullRequestFile(fileName: "src/foo.cs");
        client.PullRequest.Get("o", "r", 7).Returns(Task.FromResult(pr));
        client.PullRequest.Files("o", "r", 7)
            .Returns(Task.FromResult<IReadOnlyList<PullRequestFile>>(new[] { file }));

        var sut = BuildSut(client);

        var result = await sut.GetPullRequestDetailsAsync("o", "r", 7);

        result.Success.ShouldBeTrue();
        result.Value.ShouldNotBeNull();
        result.Value!.Number.ShouldBe(7);
        result.Value.Files.Count.ShouldBe(1);
        result.Value.Files[0].Filename.ShouldBe("src/foo.cs");
    }

    [Fact]
    public async Task GetPullRequestDetails_Throws_ArgumentOutOfRange_When_Number_NonPositive()
    {
        var sut = BuildSut(Substitute.For<IGitHubClient>());

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            async () => await sut.GetPullRequestDetailsAsync("o", "r", 0));
    }

    [Fact]
    public async Task ListIssues_Filters_Out_PullRequests()
    {
        var client = Substitute.For<IGitHubClient>();
        var issue = OctokitFakes.Issue(number: 10, isPullRequest: false);
        var asPr = OctokitFakes.Issue(number: 11, isPullRequest: true);
        client.Issue
            .GetAllForRepository("o", "r", Arg.Any<RepositoryIssueRequest>())
            .Returns(Task.FromResult<IReadOnlyList<Issue>>(new[] { issue, asPr }));

        var sut = BuildSut(client);

        var result = await sut.ListIssuesAsync("o", "r");

        result.Success.ShouldBeTrue();
        result.Value.ShouldNotBeNull();
        result.Value!.Count.ShouldBe(1);
        result.Value[0].Number.ShouldBe(10);
    }

    [Fact]
    public async Task GetIssue_Maps_Body_And_Labels()
    {
        var client = Substitute.For<IGitHubClient>();
        var issue = OctokitFakes.Issue(number: 42, body: "Issue body", labels: new[] { "bug", "good first issue" });
        client.Issue.Get("o", "r", 42).Returns(Task.FromResult(issue));

        var sut = BuildSut(client);

        var result = await sut.GetIssueAsync("o", "r", 42);

        result.Success.ShouldBeTrue();
        result.Value!.Number.ShouldBe(42);
        result.Value.Body.ShouldBe("Issue body");
        result.Value.Labels.ShouldBe(new[] { "bug", "good first issue" });
    }

    [Fact]
    public async Task GetIssue_Truncates_Long_Body()
    {
        var longBody = new string('x', GitHubMappers.MaxBodyChars + 100);
        var client = Substitute.For<IGitHubClient>();
        var issue = OctokitFakes.Issue(number: 1, body: longBody);
        client.Issue.Get("o", "r", 1).Returns(Task.FromResult(issue));

        var sut = BuildSut(client);

        var result = await sut.GetIssueAsync("o", "r", 1);

        result.Success.ShouldBeTrue();
        result.Value!.Body.ShouldNotBeNull();
        result.Value.Body!.Length.ShouldBe(GitHubMappers.MaxBodyChars + GitHubMappers.TruncationSuffix.Length);
        result.Value.Body.ShouldEndWith(GitHubMappers.TruncationSuffix);
    }

    [Fact]
    public async Task GetIssue_Maps_RateLimit_To_RateLimited_Error_With_RetryAfter()
    {
        var reset = new DateTimeOffset(2026, 5, 24, 12, 0, 0, TimeSpan.Zero);
        var client = Substitute.For<IGitHubClient>();
        client.Issue.Get("o", "r", 1)
            .Returns<Issue>(_ => throw OctokitTestDoubles.RateLimitExceeded(reset));

        var sut = BuildSut(client);

        var result = await sut.GetIssueAsync("o", "r", 1);

        result.Success.ShouldBeFalse();
        result.Error.ShouldBe(GitHubFunctions.ErrorRateLimited);
        result.RetryAfter.ShouldBe(reset);
    }

    [Fact]
    public async Task GetIssue_Maps_503_To_GithubApiError()
    {
        var client = Substitute.For<IGitHubClient>();
        client.Issue.Get("o", "r", 1)
            .Returns<Issue>(_ => throw new ApiException("down", HttpStatusCode.ServiceUnavailable));

        var sut = BuildSut(client);

        var result = await sut.GetIssueAsync("o", "r", 1);

        result.Success.ShouldBeFalse();
        result.Error.ShouldBe(GitHubFunctions.ErrorGitHubApi);
    }

    [Fact]
    public async Task GetIssue_Maps_NonRateLimit_Forbidden_To_MissingScopes()
    {
        var client = Substitute.For<IGitHubClient>();
        client.Issue.Get("o", "r", 1)
            .Returns<Issue>(_ => throw OctokitTestDoubles.Forbidden());

        var sut = BuildSut(client);

        var result = await sut.GetIssueAsync("o", "r", 1);

        result.Success.ShouldBeFalse();
        result.Error.ShouldBe(GitHubFunctions.ErrorMissingScopes);
    }

    [Fact]
    public async Task CreateIssue_Posts_With_Title_Body_Labels()
    {
        var client = Substitute.For<IGitHubClient>();
        var capturedTitle = string.Empty;
        client.Issue.Create("o", "r", Arg.Any<NewIssue>())
            .Returns(callInfo =>
            {
                var ni = callInfo.Arg<NewIssue>();
                capturedTitle = ni.Title;
                ni.Body.ShouldBe("repro steps");
                ni.Labels.ShouldContain("bug");
                return Task.FromResult(OctokitFakes.Issue(number: 123, body: ni.Body));
            });

        var sut = BuildSut(client);

        var result = await sut.CreateIssueAsync("o", "r", "bug", "repro steps", new[] { "bug" });

        result.Success.ShouldBeTrue();
        result.Value!.Number.ShouldBe(123);
        capturedTitle.ShouldBe("bug");
    }

    private static GitHubFunctions BuildSut(
        IGitHubClient client,
        IFeatureSettingsReader? settings = null)
    {
        var factory = Substitute.For<IGitHubClientFactory>();
        factory.CreateAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(client));
        return new GitHubFunctions(
            factory,
            NullLogger<GitHubFunctions>.Instance,
            Options.Create(new GitHubPluginOptions()),
            settings ?? NoSettings());
    }

    private static IFeatureSettingsReader NoSettings()
    {
        var reader = Substitute.For<IFeatureSettingsReader>();
        reader.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        return reader;
    }

    [Fact]
    public async Task ListPullRequests_Falls_Back_To_Default_Repo_Url_When_Owner_Repo_Blank()
    {
        var client = Substitute.For<IGitHubClient>();
        client.PullRequest
            .GetAllForRepository("octocat", "Hello-World", Arg.Any<PullRequestRequest>())
            .Returns(Task.FromResult<IReadOnlyList<PullRequest>>(Array.Empty<PullRequest>()));

        var settings = Substitute.For<IFeatureSettingsReader>();
        settings.GetAsync("github", "default_repo_url", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("https://github.com/octocat/Hello-World"));

        var sut = BuildSut(client, settings);

        var result = await sut.ListPullRequestsAsync(owner: "", repo: "");

        result.Success.ShouldBeTrue();
        await client.PullRequest.Received(1).GetAllForRepository(
            "octocat",
            "Hello-World",
            Arg.Any<PullRequestRequest>());
    }

    [Fact]
    public async Task CreateIssue_Falls_Back_To_Default_Repo_Url_When_Owner_Repo_Blank()
    {
        var client = Substitute.For<IGitHubClient>();
        client.Issue.Create("octocat", "Hello-World", Arg.Any<NewIssue>())
            .Returns(Task.FromResult(OctokitFakes.Issue(number: 9)));

        var settings = Substitute.For<IFeatureSettingsReader>();
        settings.GetAsync("github", "default_repo_url", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("octocat/Hello-World"));

        var sut = BuildSut(client, settings);

        var result = await sut.CreateIssueAsync(owner: "", repo: "", "bug", "body");

        result.Success.ShouldBeTrue();
        await client.Issue.Received(1).Create("octocat", "Hello-World", Arg.Any<NewIssue>());
    }

    [Fact]
    public async Task ListIssues_Fails_With_MissingRepo_When_No_Owner_Repo_And_No_Default()
    {
        var sut = BuildSut(Substitute.For<IGitHubClient>());

        var result = await sut.ListIssuesAsync(owner: "", repo: "");

        result.Success.ShouldBeFalse();
        result.Error.ShouldBe(GitHubFunctions.ErrorMissingRepo);
    }
}
