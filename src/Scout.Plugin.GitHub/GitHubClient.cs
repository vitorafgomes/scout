using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Scout.Plugin.GitHub.Abstractions;

namespace Scout.Plugin.GitHub;

/// <summary>
/// Thin read-only GitHub REST client over an injected, pre-configured
/// <see cref="HttpClient"/> (base address, auth, and headers are set by DI).
/// Non-success responses become a user-safe <see cref="GitHubApiException"/>;
/// timeouts and transport faults bubble unchanged.
/// </summary>
internal sealed class GitHubClient(HttpClient http) : IGitHubClient
{
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    public async Task<IReadOnlyList<GitHubRepository>> ListRepositoriesAsync(CancellationToken ct)
    {
        var items = await GetAsync<List<RepoDto>>(
            "user/repos?per_page=100&sort=full_name", ct).ConfigureAwait(false);

        return items
            .Select(r => new GitHubRepository(r.FullName ?? string.Empty, r.Private))
            .ToList();
    }

    public async Task<IReadOnlyList<GitHubPullRequest>> ListOpenPullRequestsAsync(
        string owner, string repo, CancellationToken ct)
    {
        var items = await GetAsync<List<PullDto>>(
            $"repos/{owner}/{repo}/pulls?state=open&per_page=100", ct).ConfigureAwait(false);

        return items
            .Select(p => new GitHubPullRequest(p.Number, p.Title ?? string.Empty, Login(p.User)))
            .ToList();
    }

    public async Task<IReadOnlyList<GitHubIssue>> ListOpenIssuesAsync(
        string owner, string repo, CancellationToken ct)
    {
        var items = await GetAsync<List<IssueDto>>(
            $"repos/{owner}/{repo}/issues?state=open&per_page=100", ct).ConfigureAwait(false);

        // The issues endpoint also returns pull requests; PR-backed items carry a
        // "pull_request" object. Exclude them so this lists issues only.
        return items
            .Where(i => i.PullRequest is null)
            .Select(i => new GitHubIssue(i.Number, i.Title ?? string.Empty, Login(i.User)))
            .ToList();
    }

    public async Task<RepositoryInfo?> GetRepositoryAsync(
        string owner, string repo, CancellationToken ct)
    {
        // The repo endpoint is required: a 404 means not found / no access and
        // yields null. Other non-success statuses surface as GitHubApiException.
        var repoDto = await TryGetAsync<RepoInfoDto>(
            $"repos/{owner}/{repo}", ct).ConfigureAwait(false);
        if (repoDto is null)
        {
            return null;
        }

        // Sub-calls are best-effort: a 404 leaves that part empty rather than
        // failing the whole overview. Transport faults and 5xx still bubble.
        var languages = await TryGetAsync<Dictionary<string, long>>(
            $"repos/{owner}/{repo}/languages", ct).ConfigureAwait(false);
        var readme = await TryGetAsync<ReadmeDto>(
            $"repos/{owner}/{repo}/readme", ct).ConfigureAwait(false);
        var contents = await TryGetAsync<List<ContentEntryDto>>(
            $"repos/{owner}/{repo}/contents", ct).ConfigureAwait(false);

        return new RepositoryInfo(
            FullName: repoDto.FullName ?? $"{owner}/{repo}",
            Description: NullIfBlank(repoDto.Description),
            Language: NullIfBlank(repoDto.Language),
            Languages: MapLanguages(languages),
            Topics: repoDto.Topics ?? [],
            License: NullIfBlank(repoDto.License?.SpdxId),
            DefaultBranch: repoDto.DefaultBranch ?? "main",
            StarCount: repoDto.StargazersCount,
            ForkCount: repoDto.ForksCount,
            HtmlUrl: repoDto.HtmlUrl ?? string.Empty,
            Readme: DecodeReadme(readme),
            TopLevelEntries: MapEntries(contents));
    }

    private static IReadOnlyList<LanguageUsage> MapLanguages(Dictionary<string, long>? languages)
        => languages is null
            ? []
            : languages
                .OrderByDescending(kvp => kvp.Value)
                .Select(kvp => new LanguageUsage(kvp.Key, kvp.Value))
                .ToList();

    private static IReadOnlyList<string> MapEntries(List<ContentEntryDto>? contents)
        => contents is null
            ? []
            : contents
                .Where(e => !string.IsNullOrWhiteSpace(e.Name))
                .Select(e => $"{e.Name} ({(string.IsNullOrWhiteSpace(e.Type) ? "file" : e.Type)})")
                .ToList();

    private static string? DecodeReadme(ReadmeDto? readme)
    {
        if (string.IsNullOrWhiteSpace(readme?.Content))
        {
            return null;
        }

        // GitHub returns base64 content with embedded newlines; strip them
        // before decoding to UTF-8, then truncate to keep the LLM context small.
        var raw = readme.Content.Replace("\n", string.Empty).Replace("\r", string.Empty);
        string text;
        try
        {
            text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(raw));
        }
        catch (FormatException)
        {
            return null;
        }

        const int maxLength = 2000;
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// GET that returns null on 404 (resource absent / no access) and maps other
    /// non-success responses to <see cref="GitHubApiException"/>. Transport faults
    /// and timeouts bubble unchanged.
    /// </summary>
    private async Task<T?> TryGetAsync<T>(string path, CancellationToken ct)
        where T : class
    {
        using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw ToApiException(response);
        }

        return await response.Content
            .ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw ToApiException(response);
        }

        var value = await response.Content
            .ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
        return value ?? throw new GitHubApiException(
            "GitHub returned an unexpected empty response.");
    }

    private static GitHubApiException ToApiException(HttpResponseMessage response)
    {
        var status = response.StatusCode;
        switch (status)
        {
            case HttpStatusCode.Unauthorized:
                return new GitHubApiException(
                    "GitHub authentication failed. Check the configured token.", status);
            case HttpStatusCode.Forbidden:
                var remaining = response.Headers.TryGetValues("X-RateLimit-Remaining", out var values)
                    ? values.FirstOrDefault()
                    : null;
                return remaining == "0"
                    ? new GitHubApiException(
                        "GitHub rate limit reached. Try again later.", status)
                    : new GitHubApiException(
                        "GitHub denied the request (insufficient token permissions).", status);
            case HttpStatusCode.NotFound:
                return new GitHubApiException(
                    "Repository not found or the token has no access.", status);
            default:
                if ((int)status >= 500)
                {
                    return new GitHubApiException(
                        "GitHub is currently unavailable. Try again later.", status);
                }
                return new GitHubApiException(
                    $"GitHub request failed ({(int)status}).", status);
        }
    }

    private static string Login(UserDto? user)
        => string.IsNullOrWhiteSpace(user?.Login) ? "unknown" : user.Login;

    private sealed record RepoDto(
        [property: JsonPropertyName("full_name")] string? FullName,
        [property: JsonPropertyName("private")] bool Private);

    private sealed record PullDto(
        [property: JsonPropertyName("number")] int Number,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("user")] UserDto? User);

    private sealed record IssueDto(
        [property: JsonPropertyName("number")] int Number,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("user")] UserDto? User,
        [property: JsonPropertyName("pull_request")] PullRequestRefDto? PullRequest);

    private sealed record UserDto(
        [property: JsonPropertyName("login")] string? Login);

    private sealed record PullRequestRefDto(
        [property: JsonPropertyName("url")] string? Url);

    private sealed record RepoInfoDto(
        [property: JsonPropertyName("full_name")] string? FullName,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("language")] string? Language,
        [property: JsonPropertyName("topics")] List<string>? Topics,
        [property: JsonPropertyName("license")] LicenseDto? License,
        [property: JsonPropertyName("default_branch")] string? DefaultBranch,
        [property: JsonPropertyName("stargazers_count")] int StargazersCount,
        [property: JsonPropertyName("forks_count")] int ForksCount,
        [property: JsonPropertyName("html_url")] string? HtmlUrl);

    private sealed record LicenseDto(
        [property: JsonPropertyName("spdx_id")] string? SpdxId);

    private sealed record ReadmeDto(
        [property: JsonPropertyName("content")] string? Content);

    private sealed record ContentEntryDto(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("type")] string? Type);
}
