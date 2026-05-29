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
}
