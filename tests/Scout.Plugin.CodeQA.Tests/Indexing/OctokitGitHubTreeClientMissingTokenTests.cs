using Microsoft.Extensions.Options;
using Scout.Plugin.CodeQA;
using Scout.Plugin.CodeQA.Indexing;

namespace Scout.Plugin.CodeQA.Tests.Indexing;

/// <summary>
/// Covers the credential-loading boundary of <see cref="OctokitGitHubTreeClient"/>.
/// Each Scout dependency on an external service must have at least one test
/// that exercises the failure path; for GitHub the boundary is the PAT
/// discovered via environment variable, so we verify both flavours of
/// misconfiguration.
/// </summary>
[Collection("EnvVar")]
public class OctokitGitHubTreeClientMissingTokenTests
{
    private const string TokenEnvVar = "SCOUT_GITHUB_TOKEN";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Ctor_throws_InvalidOperationException_when_env_var_is_missing_or_empty(string? value)
    {
        var original = Environment.GetEnvironmentVariable(TokenEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, value);

            var options = Options.Create(new CodeQAOptions
            {
                Repository = new RepositoryOptions
                {
                    Owner = "scout-org",
                    Name = "scout",
                    Branch = "main",
                    TokenEnvVar = TokenEnvVar,
                },
            });

            Action act = () => _ = new OctokitGitHubTreeClient(options);

            var ex = act.ShouldThrow<InvalidOperationException>();
            ex.Message.ShouldContain(TokenEnvVar);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, original);
        }
    }

    [Fact]
    public void Ctor_throws_when_TokenEnvVar_itself_is_blank()
    {
        var original = Environment.GetEnvironmentVariable(TokenEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);

            var options = Options.Create(new CodeQAOptions
            {
                Repository = new RepositoryOptions
                {
                    Owner = "scout-org",
                    Name = "scout",
                    Branch = "main",
                    TokenEnvVar = "   ",
                },
            });

            Action act = () => _ = new OctokitGitHubTreeClient(options);

            act.ShouldThrow<ArgumentException>();
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, original);
        }
    }
}
