using System.ComponentModel;
using Microsoft.SemanticKernel;
using Scout.Plugin.GitHub.Abstractions;

namespace Scout.Plugin.GitHub;

/// <summary>
/// Read-only GitHub plugin exposed to the LLM as Semantic Kernel functions. Each
/// method is a <see cref="KernelFunctionAttribute"/> the model can invoke by
/// name through function-calling. Listing only: no create, merge, or push
/// operations exist by design. Every call returns a non-empty, user-safe string;
/// transport and API failures are caught and surfaced as a clean error line so a
/// tool call never throws back into the kernel.
/// </summary>
public sealed class GitHubPlugin
{
    private readonly IGitHubClient _client;

    /// <summary>Creates the plugin over a read-only GitHub client.</summary>
    /// <param name="client">The GitHub client. Must be non-null.</param>
    public GitHubPlugin(IGitHubClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <summary>List repositories the authenticated user can access.</summary>
    /// <param name="ct">Cancels the underlying GitHub request.</param>
    /// <returns>One repository per line, or a clean message when none or on failure.</returns>
    [KernelFunction("list_repositories")]
    [Description("List repositories the authenticated user can access.")]
    public async Task<string> ListRepositoriesAsync(CancellationToken ct)
    {
        try
        {
            var repos = await _client.ListRepositoriesAsync(ct).ConfigureAwait(false);
            return repos.Count == 0
                ? "No repositories."
                : string.Join("\n", repos.Select(FormatRepo));
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>List open pull requests for a repository.</summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="ct">Cancels the underlying GitHub request.</param>
    /// <returns>One pull request per line, or a clean message when none or on failure.</returns>
    [KernelFunction("list_pull_requests")]
    [Description("List open pull requests for a repository.")]
    public async Task<string> ListPullRequestsAsync(
        [Description("Repository owner or organization login, e.g. octocat.")] string owner,
        [Description("Repository name, e.g. hello-world.")] string repo,
        CancellationToken ct)
    {
        try
        {
            var prs = await _client.ListOpenPullRequestsAsync(owner, repo, ct).ConfigureAwait(false);
            return prs.Count == 0
                ? $"No open pull requests in {owner}/{repo}."
                : string.Join("\n", prs.Select(p => $"#{p.Number} {p.Title} (by {p.AuthorLogin})"));
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>List open issues for a repository.</summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="ct">Cancels the underlying GitHub request.</param>
    /// <returns>One issue per line, or a clean message when none or on failure.</returns>
    [KernelFunction("list_issues")]
    [Description("List open issues for a repository.")]
    public async Task<string> ListIssuesAsync(
        [Description("Repository owner or organization login.")] string owner,
        [Description("Repository name.")] string repo,
        CancellationToken ct)
    {
        try
        {
            var issues = await _client.ListOpenIssuesAsync(owner, repo, ct).ConfigureAwait(false);
            return issues.Count == 0
                ? $"No open issues in {owner}/{repo}."
                : string.Join("\n", issues.Select(i => $"#{i.Number} {i.Title} (by {i.AuthorLogin})"));
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>Get an overview of a single repository for describing a project.</summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="ct">Cancels the underlying GitHub request.</param>
    /// <returns>A readable multi-section summary, or a clean message when not found or on failure.</returns>
    [KernelFunction("get_repository")]
    [Description("Get an overview of a GitHub repository: description, language(s), topics, license, README excerpt, and top-level structure. Use this to describe or summarize a project.")]
    public async Task<string> GetRepositoryAsync(
        [Description("Repository owner or organization login, e.g. octocat.")] string owner,
        [Description("Repository name, e.g. hello-world.")] string repo,
        CancellationToken ct)
    {
        try
        {
            var info = await _client.GetRepositoryAsync(owner, repo, ct).ConfigureAwait(false);
            return info is null
                ? $"Repository {owner}/{repo} not found or not accessible."
                : FormatRepositoryInfo(info);
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    private static string FormatRepositoryInfo(RepositoryInfo info)
    {
        var lines = new List<string>
        {
            info.FullName,
            $"Description: {info.Description ?? "(none)"}",
            $"Primary language: {info.Language ?? "(unknown)"}",
        };

        if (info.Languages.Count > 0)
        {
            lines.Add("Languages: " + string.Join(", ",
                info.Languages.Select(l => $"{l.Name} ({l.Bytes} bytes)")));
        }

        if (info.Topics.Count > 0)
        {
            lines.Add("Topics: " + string.Join(", ", info.Topics));
        }

        lines.Add($"License: {info.License ?? "(none)"}");
        lines.Add($"Default branch: {info.DefaultBranch}");
        lines.Add($"Stars: {info.StarCount}, Forks: {info.ForkCount}");
        lines.Add($"URL: {info.HtmlUrl}");

        lines.Add(info.TopLevelEntries.Count > 0
            ? "Top-level structure:\n" + string.Join("\n", info.TopLevelEntries.Select(e => $"  - {e}"))
            : "Top-level structure: (unavailable)");

        lines.Add(info.Readme is null
            ? "README: (none)"
            : "README excerpt:\n" + info.Readme);

        return string.Join("\n", lines);
    }

    private static string FormatRepo(GitHubRepository r)
        => r.Private ? $"{r.FullName} (private)" : r.FullName;

    private static bool IsHandledFailure(Exception ex)
        => ex is GitHubApiException or HttpRequestException or OperationCanceledException;

    private static string ErrorLine(Exception ex) => ex switch
    {
        GitHubApiException api => api.Message,
        OperationCanceledException => "GitHub request timed out. Try again later.",
        _ => "Could not reach GitHub. Try again later.",
    };
}
