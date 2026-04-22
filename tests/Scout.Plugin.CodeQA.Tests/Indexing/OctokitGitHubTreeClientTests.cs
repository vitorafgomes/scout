using System.Net;
using Microsoft.Extensions.Options;
using Octokit;
using Scout.Plugin.CodeQA;
using Scout.Plugin.CodeQA.Indexing;

namespace Scout.Plugin.CodeQA.Tests.Indexing;

[Collection("EnvVar")]
public class OctokitGitHubTreeClientTests
{
    private const string TestEnvVar = "SCOUT_GITHUB_TOKEN_UNIT_TEST";

    private static IOptions<CodeQAOptions> BuildOptions()
    {
        return Options.Create(new CodeQAOptions
        {
            Repository = new RepositoryOptions
            {
                Owner = "scout-org",
                Name = "scout",
                Branch = "main",
                TokenEnvVar = TestEnvVar,
            },
        });
    }

    private static RepositoryOptions BuildRepository() => new()
    {
        Owner = "scout-org",
        Name = "scout",
        Branch = "main",
        TokenEnvVar = TestEnvVar,
    };

    private static IResponse StubResponse(HttpStatusCode code)
    {
        var response = Substitute.For<IResponse>();
        response.StatusCode.Returns(code);
        response.ApiInfo.Returns(BuildApiInfo());
        response.Headers.Returns(new Dictionary<string, string>(StringComparer.Ordinal));
        return response;
    }

    private static ApiInfo BuildApiInfo()
    {
        return new ApiInfo(
            links: new Dictionary<string, Uri>(StringComparer.Ordinal),
            oauthScopes: new List<string>(),
            acceptedOauthScopes: new List<string>(),
            etag: string.Empty,
            rateLimit: new RateLimit(5000, 0, 0L),
            serverTimeDifference: TimeSpan.Zero);
    }

    [Fact]
    public void Ctor_Missing_Env_Var_Throws_InvalidOperation()
    {
        var original = Environment.GetEnvironmentVariable(TestEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(TestEnvVar, null);

            Action act = () => _ = new OctokitGitHubTreeClient(BuildOptions());

            var ex = act.ShouldThrow<InvalidOperationException>();
            ex.Message.ShouldContain(TestEnvVar);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestEnvVar, original);
        }
    }

    [Fact]
    public void Ctor_Empty_Env_Var_Throws_InvalidOperation()
    {
        var original = Environment.GetEnvironmentVariable(TestEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(TestEnvVar, string.Empty);

            Action act = () => _ = new OctokitGitHubTreeClient(BuildOptions());

            act.ShouldThrow<InvalidOperationException>();
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestEnvVar, original);
        }
    }

    [Fact]
    public void Ctor_Present_Env_Var_Builds_Client()
    {
        var original = Environment.GetEnvironmentVariable(TestEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(TestEnvVar, "ghp_dummy-token-not-real");

            var client = new OctokitGitHubTreeClient(BuildOptions());

            client.ShouldNotBeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestEnvVar, original);
        }
    }

    [Fact]
    public void Ctor_Null_Options_Throws()
    {
        Action act = () => _ = new OctokitGitHubTreeClient((IOptions<CodeQAOptions>)null!);
        act.ShouldThrow<ArgumentNullException>();
    }

    [Fact]
    public async Task TranslateAsync_Maps_AuthorizationException()
    {
        var repo = BuildRepository();
        var authEx = new AuthorizationException(StubResponse(HttpStatusCode.Unauthorized));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            OctokitGitHubTreeClient.TranslateAsync<int>(() => throw authEx, repo));

        ex.InnerException.ShouldBe(authEx);
        ex.Message.ShouldContain(repo.TokenEnvVar);
    }

    [Fact]
    public async Task TranslateAsync_Maps_NotFoundException()
    {
        var repo = BuildRepository();
        var nfEx = new NotFoundException("nope", HttpStatusCode.NotFound);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            OctokitGitHubTreeClient.TranslateAsync<int>(() => throw nfEx, repo));

        ex.InnerException.ShouldBe(nfEx);
        ex.Message.ShouldContain(repo.Owner);
        ex.Message.ShouldContain(repo.Name);
        ex.Message.ShouldContain(repo.Branch);
    }

    [Fact]
    public async Task TranslateAsync_Maps_RateLimitExceededException()
    {
        var repo = BuildRepository();
        var rlEx = new RateLimitExceededException(StubResponse(HttpStatusCode.Forbidden));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            OctokitGitHubTreeClient.TranslateAsync<int>(() => throw rlEx, repo));

        ex.InnerException.ShouldBe(rlEx);
        ex.Message.ShouldContain("rate limit", Case.Insensitive);
    }

    [Fact]
    public async Task TranslateAsync_Lets_Unrelated_Exceptions_Propagate()
    {
        var repo = BuildRepository();
        var raw = new HttpRequestException("connection reset");

        var ex = await Should.ThrowAsync<HttpRequestException>(() =>
            OctokitGitHubTreeClient.TranslateAsync<int>(() => throw raw, repo));

        ex.Message.ShouldBe("connection reset");
    }

    [Fact]
    public async Task TranslateAsync_Lets_Cancellation_Propagate()
    {
        var repo = BuildRepository();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            OctokitGitHubTreeClient.TranslateAsync<int>(
                () => throw new OperationCanceledException(),
                repo));
    }

    [Fact]
    public async Task TranslateAsync_Happy_Path_Returns_Result()
    {
        var repo = BuildRepository();

        var result = await OctokitGitHubTreeClient.TranslateAsync(
            () => Task.FromResult(42),
            repo);

        result.ShouldBe(42);
    }

    [Fact]
    public async Task TranslateAsync_Null_Call_Throws()
    {
        await Should.ThrowAsync<ArgumentNullException>(() =>
            OctokitGitHubTreeClient.TranslateAsync<int>(null!, BuildRepository()));
    }

    [Fact]
    public async Task TranslateAsync_Null_Repo_Throws()
    {
        await Should.ThrowAsync<ArgumentNullException>(() =>
            OctokitGitHubTreeClient.TranslateAsync<int>(() => Task.FromResult(1), null!));
    }
}

/// <summary>
/// Serializes every test that mutates a process-level environment variable.
/// xUnit runs collections sequentially when <c>DisableParallelization</c>
/// is set, which prevents cross-test leaks of the PAT fixture.
/// </summary>
[CollectionDefinition("EnvVar", DisableParallelization = true)]
public sealed class EnvVarCollection
{
}
