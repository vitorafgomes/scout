namespace Scout.Plugin.GitHub;

/// <summary>
/// The latest container runs of pull requests' tests, by repository and commit, kept in memory so a review's findings
/// carry the run without the model copying it back: at most <see cref="Capacity"/>, the oldest dropped first.
/// </summary>
internal sealed class TestRuns
{
    internal const int Capacity = 20;

    private readonly LinkedList<(string Repository, string Sha, ReviewTests Run)> _runs = new();
    private readonly Lock _gate = new();

    /// <summary>Keep the run of the tests of <paramref name="owner"/>/<paramref name="repo"/> at <paramref name="sha"/>, replacing an earlier one.</summary>
    public void Keep(string owner, string repo, string sha, ReviewTests run)
    {
        var repository = $"{owner}/{repo}";
        lock (_gate)
        {
            foreach (var kept in _runs.Where(r => Same(r, repository, sha)).ToList())
            {
                _runs.Remove(kept);
            }

            _runs.AddFirst((repository, sha, run));
            while (_runs.Count > Capacity)
            {
                _runs.RemoveLast();
            }
        }
    }

    /// <summary>The run kept for <paramref name="owner"/>/<paramref name="repo"/> at the commit <paramref name="commit"/> starts, or null.</summary>
    public ReviewTests? Find(string owner, string repo, string? commit)
    {
        var wanted = (commit ?? string.Empty).Trim();
        if (wanted.Length < 7)
        {
            return null;
        }

        var repository = $"{owner}/{repo}";
        lock (_gate)
        {
            return _runs.FirstOrDefault(r => string.Equals(r.Repository, repository, StringComparison.OrdinalIgnoreCase)
                && r.Sha.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)).Run;
        }
    }

    private static bool Same((string Repository, string Sha, ReviewTests Run) kept, string repository, string sha) =>
        string.Equals(kept.Repository, repository, StringComparison.OrdinalIgnoreCase) && string.Equals(kept.Sha, sha, StringComparison.OrdinalIgnoreCase);
}
