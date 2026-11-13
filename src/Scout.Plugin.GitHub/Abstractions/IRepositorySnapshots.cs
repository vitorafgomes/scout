namespace Scout.Plugin.GitHub.Abstractions;

/// <summary>
/// The repository snapshots Scout keeps under its data folder, found by commit, so another library reads a snapshot
/// without knowing how the GitHub plugin lays it out on disk.
/// </summary>
public interface IRepositorySnapshots
{
    /// <summary>
    /// The newest snapshot of <paramref name="owner"/>/<paramref name="repo"/> whose commit is
    /// <paramref name="commitOrShortSha"/> or starts with it (at least 7 characters); null when there is none.
    /// </summary>
    RepositorySnapshotInfo? Find(string owner, string repo, string commitOrShortSha);
}

/// <summary>One snapshot: the repository, the full commit, the folder its files are in, and when it was taken.</summary>
/// <param name="Owner">Repository owner or organization login.</param>
/// <param name="Repo">Repository name.</param>
/// <param name="Commit">The commit's full SHA.</param>
/// <param name="Root">Absolute path of the folder that holds the repository's files.</param>
/// <param name="TakenAt">When the snapshot was taken.</param>
public sealed record RepositorySnapshotInfo(string Owner, string Repo, string Commit, string Root, DateTimeOffset TakenAt);
