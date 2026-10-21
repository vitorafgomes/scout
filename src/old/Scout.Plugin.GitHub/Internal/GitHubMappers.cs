using Octokit;
using Scout.Plugin.GitHub.Contracts;

namespace Scout.Plugin.GitHub.Internal;

/// <summary>
/// Octokit → narrow-DTO mappers. Centralises the slim projections used by
/// every kernel function so the function bodies stay focused on the API
/// call plus exception translation.
/// </summary>
internal static class GitHubMappers
{
    /// <summary>
    /// Maximum body length retained on issue / PR DTOs. Anything longer
    /// is truncated with <see cref="TruncationSuffix"/> appended.
    /// </summary>
    internal const int MaxBodyChars = 4000;

    /// <summary>Suffix added when a body was truncated.</summary>
    internal const string TruncationSuffix = "... [truncated]";

    internal static GitHubRepository Map(Repository r) => new(
        FullName: r.FullName,
        Owner: r.Owner?.Login ?? string.Empty,
        Name: r.Name,
        Private: r.Private,
        Description: r.Description,
        DefaultBranch: r.DefaultBranch ?? string.Empty,
        OpenIssuesCount: r.OpenIssuesCount,
        Stars: r.StargazersCount,
        UpdatedAt: r.UpdatedAt,
        Url: r.HtmlUrl ?? string.Empty);

    internal static GitHubPullRequestSummary MapSummary(PullRequest pr) => new(
        Number: pr.Number,
        Title: pr.Title ?? string.Empty,
        State: pr.State.StringValue ?? string.Empty,
        Author: pr.User?.Login ?? string.Empty,
        UpdatedAt: pr.UpdatedAt,
        Url: pr.HtmlUrl ?? string.Empty,
        CommentCount: pr.Comments,
        BaseRef: pr.Base?.Ref ?? string.Empty,
        HeadRef: pr.Head?.Ref ?? string.Empty,
        Draft: pr.Draft);

    internal static GitHubPullRequestDetails MapDetails(
        PullRequest pr,
        IReadOnlyList<GitHubChangedFile> files) => new(
        Number: pr.Number,
        Title: pr.Title ?? string.Empty,
        Body: Truncate(pr.Body),
        State: pr.State.StringValue ?? string.Empty,
        Author: pr.User?.Login ?? string.Empty,
        UpdatedAt: pr.UpdatedAt,
        Url: pr.HtmlUrl ?? string.Empty,
        CommentCount: pr.Comments,
        Mergeable: pr.Mergeable,
        MergeableState: pr.MergeableState?.StringValue,
        Additions: pr.Additions,
        Deletions: pr.Deletions,
        ChangedFiles: pr.ChangedFiles,
        BaseRef: pr.Base?.Ref ?? string.Empty,
        HeadRef: pr.Head?.Ref ?? string.Empty,
        Files: files);

    internal static GitHubChangedFile Map(PullRequestFile f) => new(
        Filename: f.FileName ?? string.Empty,
        Status: f.Status ?? string.Empty,
        Additions: f.Additions,
        Deletions: f.Deletions);

    /// <summary>
    /// IssuesClient.GetAllForRepository returns both issues and PRs.
    /// Filter PRs out: every PR has a non-null <c>PullRequest</c> field
    /// (it is GitHub's marker for "this is really a PR").
    /// </summary>
    internal static bool IsRealIssue(Issue issue) => issue.PullRequest is null;

    internal static GitHubIssueSummary MapSummary(Issue issue) => new(
        Number: issue.Number,
        Title: issue.Title ?? string.Empty,
        State: issue.State.StringValue ?? string.Empty,
        Author: issue.User?.Login ?? string.Empty,
        UpdatedAt: issue.UpdatedAt ?? issue.CreatedAt,
        Url: issue.HtmlUrl ?? string.Empty,
        CommentCount: issue.Comments,
        Labels: MapLabels(issue.Labels));

    internal static GitHubIssueDetails MapDetails(Issue issue) => new(
        Number: issue.Number,
        Title: issue.Title ?? string.Empty,
        Body: Truncate(issue.Body),
        State: issue.State.StringValue ?? string.Empty,
        Author: issue.User?.Login ?? string.Empty,
        CreatedAt: issue.CreatedAt,
        UpdatedAt: issue.UpdatedAt ?? issue.CreatedAt,
        Url: issue.HtmlUrl ?? string.Empty,
        CommentCount: issue.Comments,
        Labels: MapLabels(issue.Labels),
        Assignees: issue.Assignees?.Select(a => a.Login).ToArray() ?? Array.Empty<string>());

    private static IReadOnlyList<string> MapLabels(IReadOnlyList<Label>? labels)
    {
        if (labels is null || labels.Count == 0)
        {
            return Array.Empty<string>();
        }

        var output = new string[labels.Count];
        for (var i = 0; i < labels.Count; i++)
        {
            output[i] = labels[i].Name;
        }
        return output;
    }

    internal static string? Truncate(string? body)
    {
        if (body is null)
        {
            return null;
        }

        if (body.Length <= MaxBodyChars)
        {
            return body;
        }

        return body.Substring(0, MaxBodyChars) + TruncationSuffix;
    }
}
