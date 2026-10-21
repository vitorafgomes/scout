using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Scout.Plugin.CodeReview.Abstractions;

namespace Scout.Plugin.CodeReview;

/// <summary>
/// Raw read-only GitHub REST fetcher over an injected, pre-configured
/// <see cref="HttpClient"/> (base address, auth, and headers are set by DI).
/// GET-only: resolves the repository's default branch, lists its recursive tree,
/// selects key files via <see cref="KeyFileSelector"/>, and decodes each as
/// strict UTF-8 within size caps. Binary, oversize, undecodable, or individually
/// missing files are skipped best-effort; a truncated tree and non-success
/// statuses surface as <see cref="CodeReviewFetchException"/>; timeouts and
/// transport faults bubble unchanged.
/// </summary>
internal sealed class GitHubRepositoryFetcher : IRepositoryFetcher
{
    private readonly HttpClient _http;
    private readonly int _maxFileBytes;
    private readonly int _maxFiles;

    /// <summary>Creates the fetcher over a configured GitHub client and size caps.</summary>
    /// <param name="http">The pre-configured GitHub HTTP client. Must be non-null.</param>
    /// <param name="maxFileBytes">Per-file decoded-size cap; larger files are skipped.</param>
    /// <param name="maxFiles">Maximum number of files selected for review.</param>
    public GitHubRepositoryFetcher(HttpClient http, int maxFileBytes, int maxFiles)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _maxFileBytes = maxFileBytes;
        _maxFiles = maxFiles;
    }

    public async Task<RepositorySnapshot> FetchAsync(
        RepositoryCoordinates coords, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(coords);

        var repo = await GetAsync<RepoDto>(
            $"repos/{coords.Owner}/{coords.Repo}", ct).ConfigureAwait(false);

        // Never hardcode "main": resolve the repository's declared default branch.
        var branch = string.IsNullOrWhiteSpace(repo.DefaultBranch) ? "main" : repo.DefaultBranch;
        var fullName = string.IsNullOrWhiteSpace(repo.FullName)
            ? $"{coords.Owner}/{coords.Repo}"
            : repo.FullName;

        // Encode the branch wherever it enters a URL: branch names may contain
        // slashes and other characters that would otherwise alter the path.
        var encodedBranch = Uri.EscapeDataString(branch);

        var treeDto = await GetAsync<TreeDto>(
            $"repos/{coords.Owner}/{coords.Repo}/git/trees/{encodedBranch}?recursive=1",
            ct).ConfigureAwait(false);

        if (treeDto.Truncated)
        {
            throw new CodeReviewFetchException(
                "The repository is too large to analyze in full. Try a smaller repository or a specific snippet.");
        }

        var entries = (treeDto.Tree ?? [])
            .Where(e => !string.IsNullOrWhiteSpace(e.Path) && !string.IsNullOrWhiteSpace(e.Type))
            .Select(e => new TreeEntry(e.Path!, e.Type!, e.Size, e.Sha ?? string.Empty))
            .ToList();

        var selected = KeyFileSelector.Select(entries, _maxFiles);

        var files = new List<RepositoryFile>(selected.Count);
        foreach (var path in selected)
        {
            ct.ThrowIfCancellationRequested();
            var content = await TryFetchFileAsync(coords, encodedBranch, path, ct)
                .ConfigureAwait(false);
            if (content is not null)
            {
                files.Add(new RepositoryFile(path, content));
            }
        }

        return new RepositorySnapshot(fullName, branch, files);
    }

    private async Task<string?> TryFetchFileAsync(
        RepositoryCoordinates coords, string encodedBranch, string path, CancellationToken ct)
    {
        ContentDto? dto;
        try
        {
            dto = await GetContentAsync(coords, encodedBranch, path, ct).ConfigureAwait(false);
        }
        catch (CodeReviewFetchException ex) when (IsSkippablePerFile(ex))
        {
            // A single file failing (vanished, forbidden, gone, or a transient
            // per-blob 5xx) is non-fatal: skip it and keep reviewing the rest.
            // Repo-wide rate-limit, transport faults, and cancellation still bubble.
            return null;
        }

        if (dto is null || string.IsNullOrWhiteSpace(dto.Content))
        {
            return null;
        }

        // Directories or submodules return without base64 file content.
        if (!string.Equals(dto.Encoding, "base64", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        byte[] bytes;
        try
        {
            var raw = dto.Content.Replace("\n", string.Empty).Replace("\r", string.Empty);
            bytes = Convert.FromBase64String(raw);
        }
        catch (FormatException)
        {
            return null;
        }

        if (bytes.Length > _maxFileBytes)
        {
            return null;
        }

        // NUL byte is a strong binary signal; skip rather than feed garbage.
        if (Array.IndexOf(bytes, (byte)0) >= 0)
        {
            return null;
        }

        try
        {
            // Strict decoder: invalid UTF-8 throws, so non-text content is skipped.
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private async Task<ContentDto?> GetContentAsync(
        RepositoryCoordinates coords, string encodedBranch, string path, CancellationToken ct)
    {
        var encodedPath = Uri.EscapeDataString(path).Replace("%2F", "/", StringComparison.Ordinal);
        using var response = await _http.GetAsync(
            $"repos/{coords.Owner}/{coords.Repo}/contents/{encodedPath}?ref={encodedBranch}",
            ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw ToFetchException(response);
        }

        return await response.Content
            .ReadFromJsonAsync<ContentDto>(cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
        where T : class
    {
        using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw ToFetchException(response);
        }

        var value = await response.Content
            .ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
        return value ?? throw new CodeReviewFetchException(
            "GitHub returned an unexpected empty response.");
    }

    /// <summary>
    /// True when a single-blob failure should be skipped rather than aborting the
    /// whole fetch: 404 (vanished), 403 that is not a rate-limit (per-file
    /// permission), 410 (gone), and any 5xx (transient per-blob server error). A
    /// repo-wide rate-limit is never skippable.
    /// </summary>
    private static bool IsSkippablePerFile(CodeReviewFetchException ex)
    {
        if (ex.RateLimited)
        {
            return false;
        }

        return ex.StatusCode switch
        {
            HttpStatusCode.NotFound => true,
            HttpStatusCode.Forbidden => true,
            HttpStatusCode.Gone => true,
            { } status when (int)status >= 500 => true,
            _ => false,
        };
    }

    private static CodeReviewFetchException ToFetchException(HttpResponseMessage response)
    {
        var status = response.StatusCode;
        switch (status)
        {
            case HttpStatusCode.Unauthorized:
                return new CodeReviewFetchException(
                    "GitHub authentication failed. Check the configured token.", status);
            case HttpStatusCode.Forbidden:
                var remaining = response.Headers.TryGetValues("X-RateLimit-Remaining", out var values)
                    ? values.FirstOrDefault()
                    : null;
                return remaining == "0"
                    ? new CodeReviewFetchException(
                        "GitHub rate limit reached. Try again later.", status, rateLimited: true)
                    : new CodeReviewFetchException(
                        "GitHub denied the request (insufficient token permissions).", status);
            case HttpStatusCode.NotFound:
                return new CodeReviewFetchException(
                    "Repository not found or the token has no access.", status);
            default:
                if ((int)status >= 500)
                {
                    return new CodeReviewFetchException(
                        "GitHub is currently unavailable. Try again later.", status);
                }
                return new CodeReviewFetchException(
                    $"GitHub request failed ({(int)status}).", status);
        }
    }

    private sealed record RepoDto(
        [property: JsonPropertyName("full_name")] string? FullName,
        [property: JsonPropertyName("default_branch")] string? DefaultBranch);

    private sealed record TreeDto(
        [property: JsonPropertyName("tree")] List<TreeItemDto>? Tree,
        [property: JsonPropertyName("truncated")] bool Truncated);

    private sealed record TreeItemDto(
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("sha")] string? Sha);

    private sealed record ContentDto(
        [property: JsonPropertyName("content")] string? Content,
        [property: JsonPropertyName("encoding")] string? Encoding);
}
