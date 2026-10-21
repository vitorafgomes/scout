namespace Scout.Plugin.GitHub.Internal;

/// <summary>
/// Pure parser that extracts an (owner, repo) pair from the various forms
/// a user may paste into the <c>default_repo_url</c> setting. No side
/// effects, no network. Accepts:
/// <list type="bullet">
/// <item><c>https://github.com/{owner}/{repo}</c></item>
/// <item><c>github.com/{owner}/{repo}</c></item>
/// <item>a trailing <c>.git</c> and/or trailing slash</item>
/// <item>a bare <c>{owner}/{repo}</c></item>
/// </list>
/// </summary>
internal static class GitHubRepoUrlParser
{
    /// <summary>
    /// Attempts to parse <paramref name="value"/> into an owner/repo pair.
    /// Returns false (and default outs) for anything that does not look
    /// like a GitHub repository reference.
    /// </summary>
    public static bool TryParse(string? value, out string owner, out string repo)
    {
        owner = string.Empty;
        repo = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();

        // Strip a known scheme so the remaining path is uniform.
        text = StripPrefix(text, "https://");
        text = StripPrefix(text, "http://");
        text = StripPrefix(text, "github.com/");
        text = StripPrefix(text, "www.github.com/");

        // After stripping the host the path must not still contain one.
        if (text.Contains("://", StringComparison.Ordinal)
            || text.Contains('@', StringComparison.Ordinal))
        {
            return false;
        }

        text = text.Trim('/');
        if (text.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^4];
        }

        text = text.Trim('/');
        if (text.Length == 0)
        {
            return false;
        }

        var segments = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2)
        {
            return false;
        }

        var candidateOwner = segments[0].Trim();
        var candidateRepo = segments[1].Trim();
        if (candidateOwner.Length == 0 || candidateRepo.Length == 0)
        {
            return false;
        }

        owner = candidateOwner;
        repo = candidateRepo;
        return true;
    }

    private static string StripPrefix(string text, string prefix) =>
        text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? text[prefix.Length..]
            : text;
}
