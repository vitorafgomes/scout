using System.ComponentModel;
using System.Globalization;
using System.Text;
using Microsoft.SemanticKernel;
using Scout.Plugin.GitHub.Abstractions;

namespace Scout.Plugin.GitHub;

/// <summary>
/// The single-item reads a code review needs. Diffs are bounded: a pull request answer carries each
/// file's diff while the diffs fit <see cref="PatchBudget"/> characters and names the files it left out,
/// which <c>get_pull_request_file</c> reads one at a time up to <see cref="PatchCap"/>.
/// </summary>
public sealed partial class GitHubPlugin
{
    /// <summary>Characters of diff a pull request answer carries in total.</summary>
    internal const int PatchBudget = 12_000;

    /// <summary>Characters of one file's diff or text before it is cut.</summary>
    internal const int PatchCap = 20_000;

    /// <summary>Characters of a description before it is cut.</summary>
    internal const int BodyCap = 4_000;

    /// <summary>Files GitHub lists per request; a pull request with more files than this is read in part and says so.</summary>
    internal const int ListedFilesCap = 100;

    internal const string DiffLeftOut = "(diff left out to fit the budget; read it with get_pull_request_file)";
    internal const string DiffNotProvided = "(no diff from GitHub: binary or too large)";

    /// <summary>Read one pull request with its changed files and their diffs.</summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="number">Pull request number.</param>
    /// <param name="ct">Cancels the underlying GitHub requests.</param>
    /// <returns>The pull request's metadata, file list and bounded diffs, or a clean message when not found or on failure.</returns>
    [KernelFunction("get_pull_request")]
    [Description("Read one pull request for a code review: title, description, author, branches, the extensions of the files it touches, and every changed file with its unified diff while the diffs fit a budget. A file whose diff was left out is marked; read it with get_pull_request_file. Use before reviewing or summarising a pull request.")]
    public async Task<string> GetPullRequestAsync(
        [Description("Repository owner or organization login, e.g. octocat.")] string owner,
        [Description("Repository name, e.g. hello-world.")] string repo,
        [Description("Pull request number, e.g. 42.")] int number,
        CancellationToken ct)
    {
        if (!GitHubNames.IsValidRepository(owner, repo))
        {
            return GitHubNames.InvalidRepository;
        }

        if (number <= 0)
        {
            return "The pull request number must be positive.";
        }

        try
        {
            var pr = await _client.GetPullRequestAsync(owner, repo, number, ct).ConfigureAwait(false);
            return pr is null
                ? $"Pull request #{number} not found in {owner}/{repo} or not accessible."
                : FormatPullRequest(pr);
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>Read the full diff of one file in a pull request.</summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="number">Pull request number.</param>
    /// <param name="path">The changed file's path as get_pull_request listed it.</param>
    /// <param name="ct">Cancels the underlying GitHub requests.</param>
    /// <returns>The file's unified diff, cut at the cap, or a clean message when absent or on failure.</returns>
    [KernelFunction("get_pull_request_file")]
    [Description("Read the full unified diff of one file in a pull request, for a file get_pull_request listed without its diff or when one file needs a closer look.")]
    public async Task<string> GetPullRequestFileAsync(
        [Description("Repository owner or organization login.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Pull request number.")] int number,
        [Description("The changed file's path exactly as get_pull_request listed it.")] string path,
        CancellationToken ct)
    {
        if (!GitHubNames.IsValidRepository(owner, repo))
        {
            return GitHubNames.InvalidRepository;
        }

        if (number <= 0)
        {
            return "The pull request number must be positive.";
        }

        if (!GitHubNames.IsValidPath(path))
        {
            return GitHubNames.InvalidPath;
        }

        try
        {
            var files = await _client.ListPullRequestFilesAsync(owner, repo, number, ct).ConfigureAwait(false);
            if (files is null)
            {
                return $"Pull request #{number} not found in {owner}/{repo} or not accessible.";
            }

            var file = files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.Ordinal));
            if (file is null)
            {
                return files.Count >= ListedFilesCap
                    ? $"{path} is not among the first {ListedFilesCap} files GitHub lists for pull request #{number}; the pull request may change more files than this plugin can list."
                    : $"Pull request #{number} does not change {path}. Files: {string.Join(", ", files.Select(f => f.Path))}";
            }

            return file.Patch is null
                ? $"{file.Path} ({file.Status}, +{file.Additions} -{file.Deletions}) {DiffNotProvided}"
                : $"{file.Path} ({file.Status}, +{file.Additions} -{file.Deletions})\n{Cut(file.Patch, PatchCap)}";
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>List the comments already on a pull request, so a review does not repeat them.</summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="number">Pull request number.</param>
    /// <param name="ct">Cancels the underlying GitHub requests.</param>
    /// <returns>One comment per line, oldest first, or a clean message when none or on failure.</returns>
    [KernelFunction("list_pull_request_comments")]
    [Description("List the review comments (anchored to a file and line) and the conversation comments already on a pull request, oldest first. Read them before reviewing so the review does not repeat what was already said.")]
    public async Task<string> ListPullRequestCommentsAsync(
        [Description("Repository owner or organization login.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Pull request number.")] int number,
        CancellationToken ct)
    {
        if (!GitHubNames.IsValidRepository(owner, repo))
        {
            return GitHubNames.InvalidRepository;
        }

        if (number <= 0)
        {
            return "The pull request number must be positive.";
        }

        try
        {
            var comments = await _client.ListPullRequestCommentsAsync(owner, repo, number, ct).ConfigureAwait(false);
            if (comments is null)
            {
                return $"Pull request #{number} not found in {owner}/{repo} or not accessible.";
            }

            return comments.Count == 0
                ? $"No comments on pull request #{number} in {owner}/{repo}."
                : string.Join("\n", comments.Select(FormatComment));
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>Read one issue in full.</summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="number">Issue number.</param>
    /// <param name="ct">Cancels the underlying GitHub request.</param>
    /// <returns>The issue's title, state, labels and description, or a clean message when not found or on failure.</returns>
    [KernelFunction("get_issue")]
    [Description("Read one issue in full: title, state, author, labels, comment count and description. Use it before proposing work on an issue or to check whether an improvement is already tracked.")]
    public async Task<string> GetIssueAsync(
        [Description("Repository owner or organization login.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Issue number.")] int number,
        CancellationToken ct)
    {
        if (!GitHubNames.IsValidRepository(owner, repo))
        {
            return GitHubNames.InvalidRepository;
        }

        if (number <= 0)
        {
            return "The issue number must be positive.";
        }

        try
        {
            var issue = await _client.GetIssueAsync(owner, repo, number, ct).ConfigureAwait(false);
            if (issue is null)
            {
                return $"Issue #{number} not found in {owner}/{repo} or not accessible.";
            }

            if (issue.IsPullRequest)
            {
                return $"#{number} in {owner}/{repo} is a pull request, not an issue; read it with get_pull_request.";
            }

            return FormatIssue(issue);
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>Read one file, or list one directory, of a repository.</summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="path">Repository-relative path of the file or directory.</param>
    /// <param name="reference">Branch, tag or commit; empty for the default branch.</param>
    /// <param name="ct">Cancels the underlying GitHub request.</param>
    /// <returns>The file text cut at the cap, the directory entries, or a clean message when not found or on failure.</returns>
    [KernelFunction("get_file_contents")]
    [Description("Read one file of a repository as text (long files are cut, and the answer says so), or list one directory, at a branch, tag or commit or at the default branch. Use it to read the code around a diff, a configuration file, or the structure of a folder when judging a project.")]
    public async Task<string> GetFileContentsAsync(
        [Description("Repository owner or organization login.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Repository-relative path, e.g. src/Program.cs or src for a directory.")] string path,
        [Description("Branch, tag or commit to read at; empty for the default branch.")] string reference = "",
        CancellationToken ct = default)
    {
        if (!GitHubNames.IsValidRepository(owner, repo))
        {
            return GitHubNames.InvalidRepository;
        }

        if (!GitHubNames.IsValidPath(path))
        {
            return GitHubNames.InvalidPath;
        }

        if (!GitHubNames.IsValidReference(reference))
        {
            return GitHubNames.InvalidReference;
        }

        try
        {
            var file = await _client.GetFileContentAsync(owner, repo, path, reference, ct).ConfigureAwait(false);
            if (file is null)
            {
                return $"{path} not found in {owner}/{repo}{At(reference)}.";
            }

            if (file.IsDirectory)
            {
                return file.Entries.Count == 0
                    ? $"{path} is an empty directory."
                    : $"{path} is a directory:\n" + string.Join("\n", file.Entries.Select(e => $"  - {e}"));
            }

            if (file.Kind is "symlink" or "submodule")
            {
                return $"{path} is a {file.Kind}, not a file; GitHub returns no text for it.";
            }

            return file.Text is null
                ? $"{path} ({file.Size} bytes) has no text GitHub returns inline; it is binary or over 1 MB."
                : $"{path} ({file.Size} bytes){At(reference)}:\n{Cut(file.Text, PatchCap)}";
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    internal static string FormatPullRequest(PullRequestDetails pr)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"#{pr.Number} {pr.Title} ({pr.State}{(pr.Draft ? ", draft" : string.Empty)}, by {pr.AuthorLogin})");
        text.Append(CultureInfo.InvariantCulture, $"\nBranches: {pr.HeadRef} -> {pr.BaseRef} at {Short(pr.HeadSha)}");
        text.Append(CultureInfo.InvariantCulture, $"\nURL: {pr.HtmlUrl}");
        text.Append(CultureInfo.InvariantCulture, $"\nChanges: +{pr.Additions} -{pr.Deletions} in {pr.ChangedFileCount} file(s)");
        if (pr.FilesTruncated)
        {
            text.Append(CultureInfo.InvariantCulture, $"; only the first {pr.Files.Count} are listed below, so this review covers part of the pull request");
        }


        var extensions = pr.Files
            .Select(f => Path.GetExtension(f.Path))
            .Where(e => e.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
            .ToList();
        text.Append("\nExtensions: ").Append(extensions.Count == 0 ? "(none)" : string.Join(", ", extensions));

        text.Append("\nDescription:\n").Append(pr.Body is null ? "(none)" : Cut(pr.Body, BodyCap));

        text.Append("\nFiles:");
        foreach (var file in pr.Files)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n  {file.Path} ({file.Status}, +{file.Additions} -{file.Deletions})");
        }

        text.Append("\nDiffs:");
        var budget = PatchBudget;
        foreach (var file in pr.Files)
        {
            text.Append("\n--- ").Append(file.Path);
            if (file.Patch is null)
            {
                text.Append('\n').Append(DiffNotProvided);
                continue;
            }

            if (file.Patch.Length > budget)
            {
                text.Append('\n').Append(DiffLeftOut);
                continue;
            }

            text.Append('\n').Append(file.Patch);
            budget -= file.Patch.Length;
        }

        return text.ToString();
    }

    internal static string FormatIssue(IssueDetails issue)
    {
        var labels = issue.Labels.Count == 0 ? "(none)" : string.Join(", ", issue.Labels);
        return string.Join("\n",
            $"#{issue.Number} {issue.Title} ({issue.State}, by {issue.AuthorLogin})",
            $"Labels: {labels}",
            $"Comments: {issue.CommentCount}",
            $"URL: {issue.HtmlUrl}",
            "Description:",
            issue.Body is null ? "(none)" : Cut(issue.Body, BodyCap));
    }

    private static string FormatComment(PullRequestComment comment)
    {
        var anchor = comment.Path is null
            ? "[general]"
            : comment.Line is null ? $"[{comment.Path}]" : $"[{comment.Path}:{comment.Line}]";
        return $"{anchor} {comment.AuthorLogin}: {Cut(comment.Body, BodyCap)}";
    }

    private static string At(string reference) => string.IsNullOrEmpty(reference) ? string.Empty : $" at {reference}";

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    private static string Cut(string text, int cap) =>
        text.Length <= cap ? text : string.Create(CultureInfo.InvariantCulture, $"{text[..cap]}\n... (cut at {cap} characters)");
}
