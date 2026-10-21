using System.Text.RegularExpressions;
using Scout.Plugin.CodeReview.Abstractions;

namespace Scout.Plugin.CodeReview;

/// <summary>
/// Parses a GitHub repository URL into <see cref="RepositoryCoordinates"/>.
/// Accepts the common browser/clone forms
/// (<c>https://github.com/{owner}/{repo}</c>) with an optional trailing slash,
/// a <c>.git</c> suffix, or extra path segments (e.g. <c>/tree/main</c>).
/// </summary>
internal static partial class RepositoryUrlParser
{
    // GitHub owner and repository names only use these characters. Validating the
    // charset rejects path traversal and query-injection attempts before the
    // values ever reach a request URL.
    [GeneratedRegex("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SegmentPattern();

    /// <summary>
    /// Attempts to parse <paramref name="url"/> into owner/repo coordinates.
    /// </summary>
    /// <param name="url">The repository URL to parse.</param>
    /// <param name="coords">The parsed coordinates when successful; otherwise null.</param>
    /// <returns>True when the URL is a recognizable GitHub repository URL.</returns>
    public static bool TryParse(string? url, out RepositoryCoordinates? coords)
    {
        coords = null;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && !uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2)
        {
            return false;
        }

        var owner = segments[0];
        var repo = segments[1];

        // Drop a trailing ".git" on the repository segment (clone URL form).
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            repo = repo[..^4];
        }

        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
        {
            return false;
        }

        if (!SegmentPattern().IsMatch(owner) || !SegmentPattern().IsMatch(repo))
        {
            return false;
        }

        coords = new RepositoryCoordinates(owner, repo);
        return true;
    }
}
