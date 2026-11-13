namespace Scout.Audit;

/// <summary>
/// The repository snapshots an audit plans from, found by commit; the host adapts the GitHub plugin's snapshots to it,
/// so this library never reads the plugin's folder layout.
/// </summary>
public interface IAuditSnapshots
{
    /// <summary>
    /// The newest snapshot of <paramref name="owner"/>/<paramref name="repo"/> whose commit is
    /// <paramref name="commitOrShortSha"/> or starts with it (at least 7 characters); null when there is none.
    /// </summary>
    AuditSnapshot? Find(string owner, string repo, string commitOrShortSha);
}

/// <summary>One snapshot: the repository, the full commit, the folder its files are in, and when it was taken.</summary>
/// <param name="Owner">Repository owner or organization login.</param>
/// <param name="Repo">Repository name.</param>
/// <param name="Commit">The commit's full SHA.</param>
/// <param name="Root">Absolute path of the folder that holds the repository's files.</param>
/// <param name="TakenAt">When the snapshot was taken.</param>
public sealed record AuditSnapshot(string Owner, string Repo, string Commit, string Root, DateTimeOffset TakenAt);
