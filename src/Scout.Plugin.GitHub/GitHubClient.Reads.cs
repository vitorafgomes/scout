using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Scout.Plugin.GitHub.Abstractions;

namespace Scout.Plugin.GitHub;

/// <summary>The single-item reads: one pull request with its files, its files alone, its comments, one issue, one file, the whole tree, a code search.</summary>
internal sealed partial class GitHubClient
{
    /// <summary>Items GitHub returns per page of a listing, the most it allows.</summary>
    internal const int FilesPage = 100;

    /// <summary>Pages of a pull request's files read at most: GitHub lists no more than 3,000 files for one pull request.</summary>
    internal const int FilePages = 30;
    private const int SearchPage = 30;
    private const int ReviewPages = 10;

    [GeneratedRegex(@"[?&]page=(\d+)[^>]*>;\s*rel=""last""")]
    private static partial Regex LastPage();

    public async Task<PullRequestDetails?> GetPullRequestAsync(
        string owner, string repo, int number, CancellationToken ct)
    {
        var pr = await TryGetAsync<PullDetailsDto>(
            $"repos/{owner}/{repo}/pulls/{number}", ct).ConfigureAwait(false);
        if (pr is null)
        {
            return null;
        }

        // A pull request deleted between the two calls answers 404 here; it is then a pull request with no files to show.
        var files = await ListPullRequestFilesAsync(owner, repo, number, ct).ConfigureAwait(false) ?? [];

        return new PullRequestDetails(
            Number: pr.Number,
            Title: pr.Title ?? string.Empty,
            Body: NullIfBlank(pr.Body),
            AuthorLogin: Login(pr.User),
            State: pr.State ?? "open",
            Draft: pr.Draft,
            BaseRef: pr.Base?.Ref ?? string.Empty,
            HeadRef: pr.Head?.Ref ?? string.Empty,
            HeadSha: pr.Head?.Sha ?? string.Empty,
            Additions: pr.Additions,
            Deletions: pr.Deletions,
            HtmlUrl: pr.HtmlUrl ?? string.Empty,
            ChangedFileCount: Math.Max(pr.ChangedFiles, files.Count),
            Files: files);
    }

    public async Task<PullRequestHead?> GetPullRequestHeadAsync(string owner, string repo, int number, CancellationToken ct)
    {
        var pr = await TryGetAsync<PullDetailsDto>($"repos/{owner}/{repo}/pulls/{number}", ct).ConfigureAwait(false);
        return pr is null ? null : new PullRequestHead(pr.Head?.Sha ?? string.Empty, Login(pr.User));
    }

    public async Task<CheckRuns> ListCheckRunsAsync(string owner, string repo, string sha, CancellationToken ct)
    {
        var runs = await GetAsync<CheckRunsDto>($"repos/{owner}/{repo}/commits/{sha}/check-runs?per_page={FilesPage}", ct).ConfigureAwait(false);
        var checks = (runs.CheckRuns ?? [])
            .Select(r => new PullRequestCheck(r.Name ?? string.Empty, r.Status ?? "queued", r.Conclusion, r.HtmlUrl ?? string.Empty))
            .ToList();
        return new CheckRuns(checks, Math.Max(runs.TotalCount, checks.Count));
    }

    public async Task<IReadOnlyList<PullRequestCheck>> ListStatusesAsync(string owner, string repo, string sha, CancellationToken ct)
    {
        var combined = await GetAsync<CombinedStatusDto>($"repos/{owner}/{repo}/commits/{sha}/status?per_page={FilesPage}", ct).ConfigureAwait(false);
        return (combined.Statuses ?? [])
            .Select(s => s.State switch
            {
                "success" => new PullRequestCheck(s.Context ?? string.Empty, "completed", "success", s.TargetUrl ?? string.Empty),
                "failure" or "error" => new PullRequestCheck(s.Context ?? string.Empty, "completed", "failure", s.TargetUrl ?? string.Empty),
                _ => new PullRequestCheck(s.Context ?? string.Empty, "in_progress", null, s.TargetUrl ?? string.Empty),
            })
            .ToList();
    }

    public async Task<string> GetSignedInLoginAsync(CancellationToken ct) =>
        Login(await GetAsync<UserDto>("user", ct).ConfigureAwait(false));

    public async Task<IReadOnlyList<PullRequestReview>?> ListReviewsAsync(string owner, string repo, int number, CancellationToken ct)
    {
        var reviews = await NewestFirstAsync<ReviewDto>($"repos/{owner}/{repo}/pulls/{number}/reviews", ReviewPages, ct).ConfigureAwait(false);
        return reviews?
            .Select(r => new PullRequestReview(r.Id, Login(r.User), r.State ?? string.Empty, r.CommitId ?? string.Empty, r.HtmlUrl ?? string.Empty))
            .ToList();
    }

    public async Task<IReadOnlyList<PullRequestComment>?> ListLatestConversationCommentsAsync(string owner, string repo, int number, CancellationToken ct)
    {
        var comments = await NewestFirstAsync<ConversationCommentDto>($"repos/{owner}/{repo}/issues/{number}/comments", 1, ct).ConfigureAwait(false);
        return comments?
            .Select(c => new PullRequestComment(Login(c.User), c.Body ?? string.Empty, null, null, c.CreatedAt))
            .ToList();
    }

    /// <summary>
    /// The items of a listing newest first: the first page's <c>Link</c> header names the last page, which is read first,
    /// then the pages before it, each reversed, at most <paramref name="maxPages"/> pages; null on a 404.
    /// </summary>
    private async Task<List<T>?> NewestFirstAsync<T>(string path, int maxPages, CancellationToken ct)
    {
        var first = await PageAsync<T>(path, 1, ct).ConfigureAwait(false);
        if (first is null)
        {
            return null;
        }

        var items = new List<T>();
        for (var page = first.Value.Last; page >= 1 && first.Value.Last - page < maxPages; page--)
        {
            var current = page == 1 ? first.Value.Items : (await PageAsync<T>(path, page, ct).ConfigureAwait(false))?.Items ?? [];
            items.AddRange(Enumerable.Reverse(current));
        }

        return items;
    }

    private async Task<(List<T> Items, int Last)?> PageAsync<T>(string path, int page, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"{path}?per_page={FilesPage}&page={page.ToString(CultureInfo.InvariantCulture)}", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw await ToApiExceptionAsync(response, ct).ConfigureAwait(false);
        }

        var items = await response.Content.ReadFromJsonAsync<List<T>>(cancellationToken: ct).ConfigureAwait(false) ?? [];
        var last = response.Headers.TryGetValues("Link", out var links)
            && LastPage().Match(string.Join(",", links)) is { Success: true } match
            && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? number
                : page;
        return (items, Math.Max(last, page));
    }

    public async Task<IReadOnlyList<ChangedFile>?> ListPullRequestFilesAsync(
        string owner, string repo, int number, CancellationToken ct)
    {
        var files = new List<ChangedFile>();
        for (var page = 1; page <= FilePages; page++)
        {
            var current = await PageAsync<PullFileDto>($"repos/{owner}/{repo}/pulls/{number}/files", page, ct).ConfigureAwait(false);
            if (current is null)
            {
                return page == 1
                    ? null
                    : throw new GitHubApiException($"GitHub stopped listing the files of pull request #{number} after {files.Count}; try again later.", HttpStatusCode.NotFound);
            }

            files.AddRange(current.Value.Items
                .Select(f => new ChangedFile(f.Filename ?? string.Empty, f.Status ?? "modified", f.Additions, f.Deletions, f.Patch)));
            if (current.Value.Items.Count < FilesPage)
            {
                break;
            }
        }

        return files;
    }

    public async Task<IReadOnlyList<PullRequestComment>?> ListPullRequestCommentsAsync(
        string owner, string repo, int number, CancellationToken ct)
    {
        // The two lists are independent; both requests are in flight together.
        var reviewTask = TryGetAsync<List<ReviewCommentDto>>(
            $"repos/{owner}/{repo}/pulls/{number}/comments?per_page=100", ct);
        var conversationTask = TryGetAsync<List<ConversationCommentDto>>(
            $"repos/{owner}/{repo}/issues/{number}/comments?per_page=100", ct);
        await Task.WhenAll(reviewTask, conversationTask).ConfigureAwait(false);

        var review = await reviewTask.ConfigureAwait(false);
        if (review is null)
        {
            return null;
        }

        var conversation = await conversationTask.ConfigureAwait(false) ?? [];
        return review
            .Select(c => new PullRequestComment(Login(c.User), c.Body ?? string.Empty, c.Path, c.Line ?? c.OriginalLine, c.CreatedAt))
            .Concat(conversation.Select(c => new PullRequestComment(Login(c.User), c.Body ?? string.Empty, null, null, c.CreatedAt)))
            .OrderBy(c => c.CreatedAt)
            .ToList();
    }

    public async Task<IssueDetails?> GetIssueAsync(
        string owner, string repo, int number, CancellationToken ct)
    {
        var issue = await TryGetAsync<IssueDetailsDto>(
            $"repos/{owner}/{repo}/issues/{number}", ct).ConfigureAwait(false);
        if (issue is null)
        {
            return null;
        }

        return new IssueDetails(
            Number: issue.Number,
            Title: issue.Title ?? string.Empty,
            Body: NullIfBlank(issue.Body),
            AuthorLogin: Login(issue.User),
            State: issue.State ?? "open",
            Labels: (issue.Labels ?? []).Select(l => l.Name).OfType<string>().ToList(),
            CommentCount: issue.Comments,
            HtmlUrl: issue.HtmlUrl ?? string.Empty,
            IsPullRequest: issue.PullRequest is not null);
    }

    public async Task<RepositoryFile?> GetFileContentAsync(
        string owner, string repo, string path, string? reference, CancellationToken ct)
    {
        var url = $"repos/{owner}/{repo}/contents/{EscapePath(path)}";
        if (!string.IsNullOrWhiteSpace(reference))
        {
            url += "?ref=" + Uri.EscapeDataString(reference);
        }

        // The endpoint answers an array for a directory and an object for anything else.
        using var json = await TryGetAsync<JsonDocument>(url, ct).ConfigureAwait(false);
        if (json is null)
        {
            return null;
        }

        var root = json.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            var entries = root.Deserialize<List<ContentEntryDto>>();
            return new RepositoryFile(path, Kind: "dir", Text: null, Size: 0, Entries: MapEntries(entries));
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new GitHubApiException("GitHub returned an unexpected empty response.");
        }

        var file = root.Deserialize<ContentFileDto>()
            ?? throw new GitHubApiException("GitHub returned an unexpected empty response.");
        var text = file.Encoding == "base64" && file.Content is not null
            ? (file.Content.Length == 0 ? string.Empty : DecodeBase64Content(file.Content))
            : null;
        return new RepositoryFile(path, Kind: file.Type ?? "file", Text: text, Size: file.Size, Entries: []);
    }

    public async Task<RepositoryCommit?> ResolveCommitAsync(string owner, string repo, string? reference, CancellationToken ct)
    {
        var at = reference;
        var isDefault = false;
        if (string.IsNullOrWhiteSpace(at))
        {
            var repoDto = await TryGetAsync<RepoInfoDto>($"repos/{owner}/{repo}", ct).ConfigureAwait(false);
            if (repoDto is null)
            {
                return null;
            }

            at = repoDto.DefaultBranch ?? "main";
            isDefault = true;
        }

        var commit = await TryGetAsync<CommitDto>(
            $"repos/{owner}/{repo}/commits/{Uri.EscapeDataString(at)}", ct).ConfigureAwait(false);
        return commit?.Sha is { Length: > 0 } sha ? new RepositoryCommit(at, sha, isDefault) : null;
    }

    public async Task<bool> DownloadTarballAsync(
        string owner, string repo, string sha, Func<Stream, CancellationToken, Task> consume, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(consume);
        using var response = await _http.GetAsync(
            $"repos/{owner}/{repo}/tarball/{Uri.EscapeDataString(sha)}", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw await ToApiExceptionAsync(response, ct).ConfigureAwait(false);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await consume(stream, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<RepositoryTree?> ListRepositoryFilesAsync(
        string owner, string repo, string? reference, CancellationToken ct)
    {
        var at = reference;
        if (string.IsNullOrWhiteSpace(at))
        {
            var repoDto = await TryGetAsync<RepoInfoDto>($"repos/{owner}/{repo}", ct).ConfigureAwait(false);
            if (repoDto is null)
            {
                return null;
            }

            at = repoDto.DefaultBranch ?? "main";
        }

        var tree = await TryGetAsync<TreeDto>(
            $"repos/{owner}/{repo}/git/trees/{Uri.EscapeDataString(at)}?recursive=1", ct).ConfigureAwait(false);
        if (tree is null)
        {
            return null;
        }

        var entries = (tree.Tree ?? [])
            .Where(e => e.Type == "blob" && !string.IsNullOrEmpty(e.Path))
            .Select(e => new RepositoryEntry(e.Path!, e.Size))
            .ToList();
        return new RepositoryTree(at, tree.Truncated, entries);
    }

    public async Task<CodeSearchResult> SearchCodeAsync(
        string owner, string repo, string query, string? path, string? extension, CancellationToken ct)
    {
        var terms = new List<string> { query.Trim(), $"repo:{owner}/{repo}" };
        if (!string.IsNullOrWhiteSpace(path))
        {
            var folder = path.Trim();
            terms.Add(folder.Any(char.IsWhiteSpace) ? $"path:\"{folder}\"" : $"path:{folder}");
        }

        if (!string.IsNullOrWhiteSpace(extension))
        {
            terms.Add($"extension:{extension.Trim().TrimStart('.')}");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"search/code?q={Uri.EscapeDataString(string.Join(' ', terms))}&per_page={SearchPage}");
        request.Headers.Accept.ParseAdd("application/vnd.github.text-match+json");
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await ToApiExceptionAsync(response, ct).ConfigureAwait(false);
        }

        var result = await response.Content.ReadFromJsonAsync<SearchDto>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new GitHubApiException("GitHub returned an unexpected empty response.");
        var hits = (result.Items ?? [])
            .Where(i => !string.IsNullOrEmpty(i.Path))
            .Select(i => new CodeSearchHit(
                i.Path!,
                (i.TextMatches ?? []).Select(m => m.Fragment).OfType<string>().ToList()))
            .ToList();
        return new CodeSearchResult(result.TotalCount, hits);
    }

    private static string EscapePath(string path) =>
        string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private sealed record PullDetailsDto(
        [property: JsonPropertyName("number")] int Number,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("user")] UserDto? User,
        [property: JsonPropertyName("base")] RefDto? Base,
        [property: JsonPropertyName("head")] RefDto? Head,
        [property: JsonPropertyName("additions")] int Additions,
        [property: JsonPropertyName("deletions")] int Deletions,
        [property: JsonPropertyName("changed_files")] int ChangedFiles,
        [property: JsonPropertyName("html_url")] string? HtmlUrl);

    private sealed record CheckRunsDto(
        [property: JsonPropertyName("total_count")] int TotalCount,
        [property: JsonPropertyName("check_runs")] List<CheckRunDto>? CheckRuns);

    private sealed record CheckRunDto(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("conclusion")] string? Conclusion,
        [property: JsonPropertyName("html_url")] string? HtmlUrl);

    private sealed record CombinedStatusDto([property: JsonPropertyName("statuses")] List<StatusDto>? Statuses);

    private sealed record StatusDto(
        [property: JsonPropertyName("context")] string? Context,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("target_url")] string? TargetUrl);

    private sealed record ReviewDto(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("user")] UserDto? User,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("commit_id")] string? CommitId,
        [property: JsonPropertyName("html_url")] string? HtmlUrl);

    private sealed record RefDto(
        [property: JsonPropertyName("ref")] string? Ref,
        [property: JsonPropertyName("sha")] string? Sha);

    private sealed record PullFileDto(
        [property: JsonPropertyName("filename")] string? Filename,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("additions")] int Additions,
        [property: JsonPropertyName("deletions")] int Deletions,
        [property: JsonPropertyName("patch")] string? Patch);

    private sealed record ReviewCommentDto(
        [property: JsonPropertyName("user")] UserDto? User,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("line")] int? Line,
        [property: JsonPropertyName("original_line")] int? OriginalLine,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

    private sealed record ConversationCommentDto(
        [property: JsonPropertyName("user")] UserDto? User,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

    private sealed record IssueDetailsDto(
        [property: JsonPropertyName("number")] int Number,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("user")] UserDto? User,
        [property: JsonPropertyName("labels")] List<LabelDto>? Labels,
        [property: JsonPropertyName("comments")] int Comments,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("pull_request")] PullRequestRefDto? PullRequest);

    private sealed record LabelDto(
        [property: JsonPropertyName("name")] string? Name);

    private sealed record CommitDto([property: JsonPropertyName("sha")] string? Sha);

    private sealed record TreeDto(
        [property: JsonPropertyName("truncated")] bool Truncated,
        [property: JsonPropertyName("tree")] List<TreeEntryDto>? Tree);

    private sealed record TreeEntryDto(
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("size")] long Size);

    private sealed record SearchDto(
        [property: JsonPropertyName("total_count")] int TotalCount,
        [property: JsonPropertyName("items")] List<SearchItemDto>? Items);

    private sealed record SearchItemDto(
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("text_matches")] List<TextMatchDto>? TextMatches);

    private sealed record TextMatchDto(
        [property: JsonPropertyName("fragment")] string? Fragment);

    private sealed record ContentFileDto(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("encoding")] string? Encoding,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("content")] string? Content);
}
