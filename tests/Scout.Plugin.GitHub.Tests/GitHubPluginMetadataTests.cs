using Microsoft.SemanticKernel;
using Scout.Plugin.GitHub;
using Scout.Plugin.GitHub.Abstractions;

namespace Scout.Plugin.GitHub.Tests;

/// <summary>
/// Verifies the plugin exposes Semantic Kernel function metadata the LLM relies
/// on for function-calling: stable function names plus non-empty descriptions
/// on the functions and their parameters.
/// </summary>
public sealed class GitHubPluginMetadataTests
{
    private static KernelPlugin BuildPlugin()
    {
        var client = Substitute.For<IGitHubClient>();
        return KernelPluginFactory.CreateFromObject(new GitHubPlugin(client), "GitHub");
    }

    [Theory]
    [InlineData("list_repositories")]
    [InlineData("list_pull_requests")]
    [InlineData("list_issues")]
    [InlineData("get_repository")]
    public void Exposes_Expected_Function(string functionName)
    {
        var plugin = BuildPlugin();

        plugin.TryGetFunction(functionName, out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("list_repositories")]
    [InlineData("list_pull_requests")]
    [InlineData("list_issues")]
    [InlineData("get_repository")]
    public void Function_Has_NonEmpty_Description(string functionName)
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction(functionName, out var function).ShouldBeTrue();

        function!.Metadata.Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("list_pull_requests")]
    [InlineData("list_issues")]
    [InlineData("get_repository")]
    public void RepoScoped_Function_Exposes_Owner_And_Repo_With_Descriptions(string functionName)
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction(functionName, out var function).ShouldBeTrue();

        var owner = function!.Metadata.Parameters.SingleOrDefault(p => p.Name == "owner");
        var repo = function.Metadata.Parameters.SingleOrDefault(p => p.Name == "repo");

        owner.ShouldNotBeNull();
        repo.ShouldNotBeNull();
        owner!.Description.ShouldNotBeNullOrWhiteSpace();
        repo!.Description.ShouldNotBeNullOrWhiteSpace();
    }
}
