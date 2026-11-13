namespace Scout.Plugin.GitHub;

/// <summary>
/// The conventions of one repository as Scout discovered them for an audit, or as the owner corrected them in chat:
/// what the project's own files say about naming, layers, error handling, tests, dependency injection and logging.
/// Read before every audit so findings are judged against the house rules, not against a generic list.
/// </summary>
/// <param name="Owner">Repository owner.</param>
/// <param name="Repo">Repository name.</param>
/// <param name="Conventions">The profile, in Markdown.</param>
/// <param name="UpdatedAt">When it was last written.</param>
/// <param name="OriginReference">The chat the last write came from; null when unknown.</param>
internal sealed record GitHubProjectProfile(
    string Owner,
    string Repo,
    string Conventions,
    DateTimeOffset UpdatedAt,
    string? OriginReference);

/// <summary>One JSON file per repository under the profiles folder, through <see cref="JsonFiles"/>.</summary>
internal sealed class GitHubProfileStore
{
    private readonly string _directory;

    public GitHubProfileStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
    }

    /// <summary>The folder the profiles live in.</summary>
    internal string Directory => _directory;

    public Task SaveAsync(GitHubProjectProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return JsonFiles.SaveAsync(PathOf(profile.Owner, profile.Repo), profile, ct);
    }

    public Task<GitHubProjectProfile?> FindAsync(string owner, string repo, CancellationToken ct) =>
        JsonFiles.ReadAsync<GitHubProjectProfile>(PathOf(owner, repo), ct);

    /// <summary>Owner and repository are validated names (letters, digits, dots, hyphens, underscores), so they are safe as a file name.</summary>
    private string PathOf(string owner, string repo) =>
        Path.Combine(_directory, $"{owner.ToLowerInvariant()}__{repo.ToLowerInvariant()}.json");
}
