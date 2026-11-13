using Microsoft.SemanticKernel;
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
    [InlineData("get_pull_request")]
    [InlineData("get_pull_request_file")]
    [InlineData("list_pull_request_comments")]
    [InlineData("get_issue")]
    [InlineData("get_file_contents")]
    [InlineData("list_repository_files")]
    [InlineData("search_code")]
    [InlineData("present_review_findings")]
    [InlineData("present_audit_findings")]
    public void Exposes_Expected_Function(string functionName)
    {
        var plugin = BuildPlugin();

        plugin.TryGetFunction(functionName, out _).ShouldBeTrue();
    }

    [Fact]
    public void Exposes_Exactly_The_Fourteen_Read_Functions() =>
        BuildPlugin().Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal).ShouldBe([
            "get_file_contents", "get_issue", "get_pull_request", "get_pull_request_checks", "get_pull_request_file", "get_repository",
            "list_issues", "list_pull_request_comments", "list_pull_requests", "list_repositories", "list_repository_files",
            "present_audit_findings", "present_review_findings", "search_code",
        ]);

    [Theory]
    [InlineData("list_repositories")]
    [InlineData("list_pull_requests")]
    [InlineData("list_issues")]
    [InlineData("get_repository")]
    [InlineData("get_pull_request")]
    [InlineData("get_pull_request_file")]
    [InlineData("list_pull_request_comments")]
    [InlineData("get_issue")]
    [InlineData("get_file_contents")]
    [InlineData("list_repository_files")]
    [InlineData("search_code")]
    [InlineData("present_review_findings")]
    [InlineData("present_audit_findings")]
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
    [InlineData("get_pull_request")]
    [InlineData("get_pull_request_file")]
    [InlineData("list_pull_request_comments")]
    [InlineData("get_issue")]
    [InlineData("get_file_contents")]
    [InlineData("list_repository_files")]
    [InlineData("search_code")]
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

    [Theory]
    [InlineData("get_pull_request", "number")]
    [InlineData("get_pull_request_file", "number")]
    [InlineData("get_pull_request_file", "path")]
    [InlineData("list_pull_request_comments", "number")]
    [InlineData("get_issue", "number")]
    [InlineData("get_file_contents", "path")]
    [InlineData("get_file_contents", "reference")]
    [InlineData("list_repository_files", "prefix")]
    [InlineData("list_repository_files", "extensions")]
    [InlineData("search_code", "query")]
    [InlineData("search_code", "extension")]
    [InlineData("present_review_findings", "repository")]
    [InlineData("present_review_findings", "number")]
    [InlineData("present_review_findings", "verdict")]
    [InlineData("present_review_findings", "findings_json")]
    [InlineData("present_review_findings", "commit")]
    [InlineData("present_review_findings", "read")]
    [InlineData("present_audit_findings", "repository")]
    [InlineData("present_audit_findings", "verdict")]
    [InlineData("present_audit_findings", "findings_json")]
    [InlineData("present_audit_findings", "commit")]
    [InlineData("present_audit_findings", "read")]
    [InlineData("present_audit_findings", "known_json")]
    public void Single_Item_Function_Describes_Its_Selector(string functionName, string parameter)
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction(functionName, out var function).ShouldBeTrue();

        function!.Metadata.Parameters.Single(p => p.Name == parameter).Description.ShouldNotBeNullOrWhiteSpace();
    }
}
