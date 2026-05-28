using System.Net;
using Microsoft.Extensions.Options;
using Octokit;
using Scout.Core.Features;
using Scout.Plugin.GitHub.Internal;
using Scout.Plugin.GitHub.Tests.Internal;

namespace Scout.Plugin.GitHub.Tests;

public class GitHubCredentialValidatorTests
{
    [Fact]
    public async Task Empty_Body_Returns_Invalid()
    {
        var sut = BuildSut(_ => Substitute.For<IGitHubClient>());

        var result = await sut.ValidateAsync(
            new Dictionary<string, string>(),
            CancellationToken.None);

        result.Outcome.ShouldBe(FeatureCredentialValidationOutcome.Invalid);
        result.Detail.ShouldNotBeNull();
        result.Detail!.ShouldContain("PAT is required");
    }

    [Fact]
    public async Task Valid_PAT_With_Repo_Scope_Returns_Valid()
    {
        var client = Substitute.For<IGitHubClient>();
        client.User.Current().Returns(OctokitFakes.User("vitor"));
        client.GetLastApiInfo().Returns(OctokitTestDoubles.BuildApiInfo(
            oauthScopes: new[] { "repo", "read:org" }));

        var sut = BuildSut(_ => client);

        var result = await sut.ValidateAsync(
            new Dictionary<string, string> { ["pat"] = "ghp_token" },
            CancellationToken.None);

        result.Outcome.ShouldBe(FeatureCredentialValidationOutcome.Valid);
        result.Detail.ShouldNotBeNull();
        result.Detail!.ShouldContain("vitor");
        result.Metadata.ShouldNotBeNull();
        result.Metadata!.ShouldContainKeyAndValue("login", "vitor");
    }

    [Fact]
    public async Task Missing_Repo_Scope_Returns_MissingScopes()
    {
        var client = Substitute.For<IGitHubClient>();
        client.User.Current().Returns(OctokitFakes.User("vitor"));
        client.GetLastApiInfo().Returns(OctokitTestDoubles.BuildApiInfo(
            oauthScopes: new[] { "read:org" }));

        var sut = BuildSut(_ => client);

        var result = await sut.ValidateAsync(
            new Dictionary<string, string> { ["pat"] = "ghp_token" },
            CancellationToken.None);

        result.Outcome.ShouldBe(FeatureCredentialValidationOutcome.MissingScopes);
    }

    [Fact]
    public async Task AuthorizationException_Returns_Invalid()
    {
        var client = Substitute.For<IGitHubClient>();
        client.User.Current().Returns<User>(_ => throw new AuthorizationException());

        var sut = BuildSut(_ => client);

        var result = await sut.ValidateAsync(
            new Dictionary<string, string> { ["pat"] = "bad" },
            CancellationToken.None);

        result.Outcome.ShouldBe(FeatureCredentialValidationOutcome.Invalid);
        result.Detail.ShouldNotBeNull();
        result.Detail!.ShouldContain("rejected");
    }

    [Fact]
    public async Task RateLimitExceededException_Returns_RateLimited()
    {
        var reset = new DateTimeOffset(2026, 5, 24, 13, 0, 0, TimeSpan.Zero);
        var client = Substitute.For<IGitHubClient>();
        client.User.Current().Returns<User>(_ => throw OctokitTestDoubles.RateLimitExceeded(reset));

        var sut = BuildSut(_ => client);

        var result = await sut.ValidateAsync(
            new Dictionary<string, string> { ["pat"] = "ghp_token" },
            CancellationToken.None);

        result.Outcome.ShouldBe(FeatureCredentialValidationOutcome.RateLimited);
        result.Detail.ShouldNotBeNull();
        result.Detail!.ShouldContain("Rate limit");
    }

    [Fact]
    public async Task ServiceUnavailable_Returns_TransientError()
    {
        var client = Substitute.For<IGitHubClient>();
        client.User.Current().Returns<User>(_ =>
            throw new ApiException("down", HttpStatusCode.ServiceUnavailable));

        var sut = BuildSut(_ => client);

        var result = await sut.ValidateAsync(
            new Dictionary<string, string> { ["pat"] = "ghp_token" },
            CancellationToken.None);

        result.Outcome.ShouldBe(FeatureCredentialValidationOutcome.TransientError);
    }

    [Fact]
    public void FeatureId_Is_github()
    {
        var sut = BuildSut(_ => Substitute.For<IGitHubClient>());
        sut.FeatureId.ShouldBe("github");
    }

    private static GitHubCredentialValidator BuildSut(Func<string, IGitHubClient> clientOverride)
    {
        return new GitHubCredentialValidator(
            Options.Create(new GitHubPluginOptions()),
            clientOverride);
    }
}
