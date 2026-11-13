using System.ComponentModel;
using System.Globalization;
using System.Text;
using Microsoft.SemanticKernel;
using Scout.Llm.Abstractions;
using Scout.Plugin.GitHub.Abstractions;

namespace Scout.Plugin.GitHub;

/// <summary>
/// The single-item reads a code review needs, and the two whole-project reads an audit needs. Diffs are
/// bounded: a pull request answer lists the files up to <see cref="FilesTextCap"/> characters, carries each file's diff
/// while the diffs fit <see cref="PatchBudget"/> characters and counts the diffs it left out, which <c>get_pull_request_file</c> reads one at a time up to
/// <see cref="PatchCap"/>. A tree listing carries at most <see cref="ListedPathsCap"/> paths after the filters.
/// </summary>
internal sealed partial class GitHubPlugin
{
    /// <summary>Characters of diff a pull request answer carries in total.</summary>
    internal const int PatchBudget = 12_000;

    /// <summary>Characters of one file's diff or text before it is cut.</summary>
    internal const int PatchCap = 20_000;

    /// <summary>Characters of a description before it is cut.</summary>
    internal const int BodyCap = 4_000;

    /// <summary>Files GitHub lists for one pull request at most, every page read; a pull request with more is read in part and says so.</summary>
    internal const int ListedFilesCap = GitHubClient.FilesPage * GitHubClient.FilePages;

    /// <summary>Characters of file lines a pull request answer lists; the files past it are counted, not named.</summary>
    internal const int FilesTextCap = 60_000;

    /// <summary>Paths an answer about a path the pull request does not change names; the rest are counted.</summary>
    internal const int NamedFilesCap = 100;
    internal const string DiffNotProvided = "(no diff from GitHub: binary or too large)";

    /// <summary>Read one pull request with its changed files and their diffs.</summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="number">Pull request number.</param>
    /// <param name="ct">Cancels the underlying GitHub requests.</param>
    /// <returns>The pull request's metadata, file list and bounded diffs, or a clean message when not found or on failure.</returns>
    [KernelFunction("get_pull_request")]
    [Description("Read one pull request for a code review: title, description, author, branches, the extensions of the files it touches, every changed file with its status and line counts (all of them are read, past the first hundred, up to the 3,000 GitHub lists; a very long list names the first files and counts the rest), and each file's unified diff while the diffs fit a budget. The answer says how many diffs were left out, and how many files GitHub sent no diff for (binary or too large), naming up to 100; neither gets a section under Diffs; read each with get_pull_request_file. Use before reviewing or summarising a pull request.")]
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
    [Description("Read the full unified diff of one file in a pull request, any file get_pull_request listed or counted, for a file listed without its diff or when one file needs a closer look.")]
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
                    ? $"{path} is not among the {ListedFilesCap} files GitHub lists for pull request #{number}, the most it lists for one pull request; the pull request may change more files than GitHub can list."
                    : $"Pull request #{number} does not change {path}. Files: {string.Join(", ", files.Take(NamedFilesCap).Select(f => f.Path))}"
                        + (files.Count > NamedFilesCap ? $", and {files.Count - NamedFilesCap} more" : string.Empty);
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
    /// <returns>
    /// The file text cut at the cap, the directory entries, or a clean message when not found or on failure; every answer
    /// that is not the file's text is marked on <see cref="ReadOutcome"/> for a caller that counts the files read.
    /// </returns>
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
            return NoContent(GitHubNames.InvalidRepository);
        }

        if (!GitHubNames.IsValidPath(path))
        {
            return NoContent(GitHubNames.InvalidPath);
        }

        if (!GitHubNames.IsValidReference(reference))
        {
            return NoContent(GitHubNames.InvalidReference);
        }

        if (_snapshots?.Find(owner, repo, reference) is { } snapshot)
        {
            return ReadFromSnapshot(snapshot, path);
        }

        try
        {
            var file = await _client.GetFileContentAsync(owner, repo, path, reference, ct).ConfigureAwait(false);
            if (file is null)
            {
                return NoContent($"{path} not found in {owner}/{repo}{At(reference)}.");
            }

            if (file.IsDirectory)
            {
                return NoContent(file.Entries.Count == 0
                    ? $"{path} is an empty directory."
                    : $"{path} is a directory:\n" + string.Join("\n", file.Entries.Select(e => $"  - {e}")));
            }

            if (file.Kind is "symlink" or "submodule")
            {
                return NoContent($"{path} is a {file.Kind}, not a file; GitHub returns no text for it.");
            }

            return file.Text is null
                ? NoContent($"{path} ({file.Size} bytes) has no text GitHub returns inline; it is binary or over 1 MB.")
                : $"{path} ({file.Size} bytes){At(reference)}:\n{Cut(file.Text, PatchCap)}";
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return NoContent(ErrorLine(ex));
        }
    }

    private static string NoContent(string answer)
    {
        ReadOutcome.MarkNoContent();
        return answer;
    }

    /// <summary>Paths a tree listing prints after the filters; the rest is counted and the model is told to narrow.</summary>
    internal const int ListedPathsCap = 300;

    internal const int FragmentCap = 300;

    /// <summary>List the files of a repository, with counts by folder and by extension.</summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="reference">Branch, tag or commit; empty for the default branch.</param>
    /// <param name="prefix">Only paths under this folder; empty for the whole repository.</param>
    /// <param name="extensions">Comma-separated extensions to keep, e.g. ".cs,.ts"; empty for all.</param>
    /// <param name="ct">Cancels the underlying GitHub requests.</param>
    /// <returns>The counts and the matching paths, cut at a cap, or a clean message when not found or on failure.</returns>
    [KernelFunction("list_repository_files")]
    [Description("List the files of a repository in one call: how many per top-level folder and per extension, then the paths that match an optional folder prefix and extension list, up to 300. Use it first when judging a whole project, to see its shape and pick what to read; narrow with prefix or extensions when it says the listing was cut.")]
    public async Task<string> ListRepositoryFilesAsync(
        [Description("Repository owner or organization login.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Branch, tag or commit to list at; empty for the default branch.")] string reference = "",
        [Description("Only paths under this folder, e.g. src/; empty for the whole repository.")] string prefix = "",
        [Description("Comma-separated extensions to keep, e.g. .cs,.ts; empty for all.")] string extensions = "",
        CancellationToken ct = default)
    {
        if (!GitHubNames.IsValidRepository(owner, repo))
        {
            return GitHubNames.InvalidRepository;
        }

        if (!GitHubNames.IsValidReference(reference))
        {
            return GitHubNames.InvalidReference;
        }

        if (prefix.Length > 0 && !GitHubNames.IsValidPath(prefix.TrimEnd('/')))
        {
            return GitHubNames.InvalidPath;
        }

        if (_snapshots?.Find(owner, repo, reference) is { } snapshot)
        {
            return FormatTree(owner, repo, new RepositoryTree(snapshot.Reference, false, RepositorySnapshotStore.Files(snapshot)), prefix, extensions)
                + $"\n(from the local snapshot at {snapshot.ShortSha})";
        }

        try
        {
            var tree = await _client.ListRepositoryFilesAsync(owner, repo, reference, ct).ConfigureAwait(false);
            return tree is null
                ? $"Repository {owner}/{repo}{At(reference)} not found or not accessible."
                : FormatTree(owner, repo, tree, prefix, extensions);
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>Search a repository's code on its default branch.</summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="query">What to look for: words, an identifier, a string.</param>
    /// <param name="path">Only files under this folder; empty for all.</param>
    /// <param name="extension">Only files with this extension, e.g. cs; empty for all.</param>
    /// <param name="ct">Cancels the underlying GitHub request.</param>
    /// <returns>The matching files with the highlighted fragments, or a clean message when none or on failure.</returns>
    [KernelFunction("search_code")]
    [Description("Search a repository's code on its default branch for words, an identifier or a string, optionally under one folder or one extension: the matching files with the fragments GitHub highlights, first 30. Use it to find where something is done or called across the project. GitHub allows about ten searches a minute.")]
    public async Task<string> SearchCodeAsync(
        [Description("Repository owner or organization login.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("What to look for, 1 to 200 characters, without repo:, org: or user: qualifiers.")] string query,
        [Description("Only files under this folder, e.g. src/Orders; empty for all.")] string path = "",
        [Description("Only files with this extension, e.g. cs or ts; empty for all.")] string extension = "",
        CancellationToken ct = default)
    {
        if (!GitHubNames.IsValidRepository(owner, repo))
        {
            return GitHubNames.InvalidRepository;
        }

        if (!GitHubNames.IsValidSearchQuery(query))
        {
            return GitHubNames.InvalidQuery;
        }

        if (path.Length > 0 && !GitHubNames.IsValidPath(path.TrimEnd('/')))
        {
            return GitHubNames.InvalidPath;
        }

        if (!GitHubNames.IsValidExtension(extension))
        {
            return GitHubNames.InvalidExtension;
        }

        if (_snapshots?.FindNewest(owner, repo) is { } snapshot)
        {
            return FormatSearch(owner, repo, query, RepositorySnapshotStore.Search(snapshot, query.Trim(), path, extension))
                + $"\n(from the local snapshot of {snapshot.Reference} at {snapshot.ShortSha}, every file searched)";
        }

        try
        {
            var result = await _client.SearchCodeAsync(owner, repo, query, path, extension, ct).ConfigureAwait(false);
            return FormatSearch(owner, repo, query, result);
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    private static string ReadFromSnapshot(RepositorySnapshot snapshot, string path)
    {
        var entries = RepositorySnapshotStore.ListDirectory(snapshot, path);
        if (entries is not null)
        {
            return NoContent(entries.Count == 0
                ? $"{path} is an empty directory."
                : $"{path} is a directory (local snapshot at {snapshot.ShortSha}):\n" + string.Join("\n", entries.Select(e => $"  - {e}")));
        }

        var text = RepositorySnapshotStore.ReadText(snapshot, path, out var size);
        if (text is null)
        {
            return NoContent(size == 0
                ? $"{path} not found in {snapshot.Label}."
                : $"{path} ({size} bytes) is binary or over 1 MB; no text to show.");
        }

        return $"{path} ({size} bytes) at {snapshot.Reference} (local snapshot at {snapshot.ShortSha}):\n{Cut(text, PatchCap)}";
    }

    internal static string FormatTree(string owner, string repo, RepositoryTree tree, string prefix, string extensions)
    {
        var wanted = extensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.StartsWith('.') ? e : "." + e)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var folder = prefix.Length == 0 ? string.Empty : prefix.TrimEnd('/') + "/";
        var selected = tree.Entries
            .Where(e => folder.Length == 0 || e.Path.StartsWith(folder, StringComparison.Ordinal))
            .Where(e => wanted.Count == 0 || wanted.Contains(Path.GetExtension(e.Path)))
            .ToList();

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{owner}/{repo} at {tree.Reference}: {tree.Entries.Count} file(s)");
        if (tree.Truncated)
        {
            text.Append("; GitHub cut the listing, so this is a prefix of the repository");
        }

        text.Append("\nBy top-level folder: ").Append(Counts(tree.Entries.Select(e => e.Path.Contains('/') ? e.Path[..e.Path.IndexOf('/')] + "/" : "(root)")));
        text.Append("\nBy extension: ").Append(Counts(tree.Entries.Select(e => Path.GetExtension(e.Path) is { Length: > 0 } ext ? ext : "(none)")));

        var parts = new List<string>();
        if (folder.Length > 0)
        {
            parts.Add($"prefix \"{folder}\"");
        }

        if (wanted.Count > 0)
        {
            parts.Add($"extensions \"{string.Join(",", wanted.OrderBy(w => w, StringComparer.OrdinalIgnoreCase))}\"");
        }

        var filter = parts.Count == 0 ? string.Empty : " matching " + string.Join(" and ", parts);
        text.Append(CultureInfo.InvariantCulture, $"\nFiles{filter}: {selected.Count}");
        foreach (var entry in selected.OrderBy(e => e.Path, StringComparer.Ordinal).Take(ListedPathsCap))
        {
            text.Append(CultureInfo.InvariantCulture, $"\n  {entry.Path} ({entry.Size} bytes)");
        }

        if (selected.Count > ListedPathsCap)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n  ... {selected.Count - ListedPathsCap} more; narrow with prefix or extensions");
        }

        return text.ToString();
    }

    internal static string FormatSearch(string owner, string repo, string query, CodeSearchResult result)
    {
        if (result.TotalCount == 0 || result.Hits.Count == 0)
        {
            return $"No file in {owner}/{repo} matches \"{query.Trim()}\" (default branch).";
        }

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{result.TotalCount} file(s) in {owner}/{repo} match \"{query.Trim()}\" (default branch; first {result.Hits.Count} shown)");
        foreach (var hit in result.Hits)
        {
            text.Append("\n--- ").Append(hit.Path);
            foreach (var fragment in hit.Fragments.Take(2))
            {
                text.Append('\n').Append(Cut(fragment.Trim(), FragmentCap));
            }
        }

        return text.ToString();
    }

    private static string Counts(IEnumerable<string> keys) =>
        string.Join(", ", keys
            .GroupBy(k => k, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(30)
            .Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Key} {g.Count()}")));

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
        var listed = 0;
        var listedText = 0;
        foreach (var file in pr.Files)
        {
            var line = string.Create(CultureInfo.InvariantCulture, $"\n  {file.Path} ({file.Status}, +{file.Additions} -{file.Deletions})");
            listedText += line.Length;
            if (listedText > FilesTextCap)
            {
                break;
            }

            text.Append(line);
            listed++;
        }

        if (listed < pr.Files.Count)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n{pr.Files.Count - listed} more files not listed here; read one with get_pull_request_file by its path");
        }

        var diffs = new StringBuilder("\nDiffs:");
        var budget = PatchBudget;
        var leftOut = 0;
        var noDiff = new List<string>();
        foreach (var file in pr.Files)
        {
            if (file.Patch is null)
            {
                noDiff.Add(file.Path);
                continue;
            }

            if (file.Patch.Length > budget)
            {
                leftOut++;
                continue;
            }

            diffs.Append("\n--- ").Append(file.Path).Append('\n').Append(file.Patch);
            budget -= file.Patch.Length;
        }

        if (leftOut > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n{leftOut} of {pr.Files.Count} file(s) had their diff left out to fit the budget and have no section under Diffs; read each with get_pull_request_file.");
        }

        if (noDiff.Count > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n{noDiff.Count} file(s) GitHub sent no diff for (binary or too large), no section under Diffs: {string.Join(", ", noDiff.Take(NamedFilesCap))}")
                .Append(noDiff.Count > NamedFilesCap ? $", and {noDiff.Count - NamedFilesCap} more" : string.Empty);
        }

        return text.Append(diffs).ToString();
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
