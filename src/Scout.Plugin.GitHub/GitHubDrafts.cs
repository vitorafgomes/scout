using Scout.Plugin.GitHub.Abstractions;

namespace Scout.Plugin.GitHub;

/// <summary>
/// A review or an issue the model composed from chat, kept under Scout's data folder from the moment the write is
/// queued for the owner until the executor posts it or it goes stale. The queue in the control plane holds only
/// the summary the owner reads; the text itself stays here, on the worker's side.
/// </summary>
/// <param name="Id">The draft's own id, the file name.</param>
/// <param name="Kind"><see cref="GitHubDraft.ReviewKind"/> or <see cref="GitHubDraft.IssueKind"/>.</param>
/// <param name="Owner">Repository owner.</param>
/// <param name="Repo">Repository name.</param>
/// <param name="Number">The pull request number for a review; 0 for an issue.</param>
/// <param name="Title">The issue title; empty for a review.</param>
/// <param name="Body">The review body or the issue body.</param>
/// <param name="Comments">The review comments, each on a line of the diff; empty for an issue.</param>
/// <param name="Labels">The issue labels; empty for a review.</param>
/// <param name="OriginReference">The chat the request came from, to tell it the outcome; null when unknown.</param>
/// <param name="CreatedAt">When the draft was written.</param>
/// <param name="ActionId">The queued action's id once the control plane queued it; null before.</param>
/// <param name="PostingAt">When the executor started posting it; null until then. A draft found posting by a run that did not start it is not posted again.</param>
internal sealed record GitHubDraft(
    string Id,
    string Kind,
    string Owner,
    string Repo,
    int Number,
    string Title,
    string Body,
    IReadOnlyList<ReviewComment> Comments,
    IReadOnlyList<string> Labels,
    string? OriginReference,
    DateTimeOffset CreatedAt,
    string? ActionId,
    DateTimeOffset? PostingAt = null)
{
    internal const string ReviewKind = "review";
    internal const string IssueKind = "issue";

    /// <summary>The repository and, for a review, the pull request, as the owner reads it: <c>owner/repo#42</c>.</summary>
    public string Target => Kind == ReviewKind ? $"{Owner}/{Repo}#{Number}" : $"{Owner}/{Repo}";
}

/// <summary>
/// One JSON file per draft in the drafts folder, through <see cref="JsonFiles"/>. Drafts older than <see cref="MaxAge"/>
/// are deleted on each purge: an action the owner never decided, or rejected, leaves its draft behind and this is what
/// clears it.
/// </summary>
internal sealed class GitHubDraftStore
{
    internal static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    private readonly string _directory;

    public GitHubDraftStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
    }

    /// <summary>The folder the drafts live in.</summary>
    internal string Directory => _directory;

    public Task SaveAsync(GitHubDraft draft, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return JsonFiles.SaveAsync(PathOf(draft.Id), draft, ct);
    }

    public Task<GitHubDraft?> FindAsync(string id, CancellationToken ct) => JsonFiles.ReadAsync<GitHubDraft>(PathOf(id), ct);

    /// <summary>The draft whose queued action is <paramref name="actionId"/>, or null.</summary>
    public async Task<GitHubDraft?> FindByActionAsync(string actionId, CancellationToken ct)
    {
        foreach (var file in Files())
        {
            var draft = await JsonFiles.ReadAsync<GitHubDraft>(file, ct).ConfigureAwait(false);
            if (draft is not null && string.Equals(draft.ActionId, actionId, StringComparison.Ordinal))
            {
                return draft;
            }
        }

        return null;
    }

    public Task DeleteAsync(string id)
    {
        File.Delete(PathOf(id));
        return Task.CompletedTask;
    }

    /// <summary>Delete every draft written more than <see cref="MaxAge"/> before <paramref name="now"/>; returns how many.</summary>
    public async Task<int> PurgeAsync(DateTimeOffset now, CancellationToken ct)
    {
        var purged = 0;
        foreach (var file in Files())
        {
            var draft = await JsonFiles.ReadAsync<GitHubDraft>(file, ct).ConfigureAwait(false);
            if (draft is null || draft.CreatedAt <= now - MaxAge)
            {
                File.Delete(file);
                purged++;
            }
        }

        return purged;
    }

    private IEnumerable<string> Files() =>
        System.IO.Directory.Exists(_directory) ? System.IO.Directory.EnumerateFiles(_directory, "*.json") : [];

    private string PathOf(string id) => Path.Combine(_directory, id + ".json");
}
