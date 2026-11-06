using System.Text.RegularExpressions;

namespace Scout.Plugin.GitHub;

/// <summary>
/// What the model may put into a GitHub request path. Owner and repository names, file paths and git
/// references are checked before a URL is built, so a value cannot climb out of the repository's
/// endpoints or reach another one.
/// </summary>
internal static partial class GitHubNames
{
    internal const string InvalidRepository =
        "The repository must be given as an owner and a name made of letters, digits, dots, hyphens or underscores.";

    internal const string InvalidPath =
        "The file path must be relative to the repository root, without a leading slash or a '..' segment.";

    internal const string InvalidReference =
        "The git reference must be a branch, tag or commit name made of letters, digits, dots, slashes, hyphens or underscores.";

    private const int MaxPathLength = 1000;

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,99})$")]
    private static partial Regex RepositoryName();

    [GeneratedRegex(@"^[A-Za-z0-9._/-]{1,200}$")]
    private static partial Regex Reference();

    internal static bool IsValidRepository(string? owner, string? repo) =>
        owner is not null && repo is not null && RepositoryName().IsMatch(owner) && RepositoryName().IsMatch(repo);

    internal static bool IsValidPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaxPathLength || path.StartsWith('/') || path.Contains('\\'))
        {
            return false;
        }

        return path.Split('/').All(segment => segment.Length > 0 && segment != "." && segment != "..");
    }

    internal static bool IsValidReference(string? reference) =>
        string.IsNullOrEmpty(reference) || (Reference().IsMatch(reference) && !reference.Contains("..", StringComparison.Ordinal));
}
