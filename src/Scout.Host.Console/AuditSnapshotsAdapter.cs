using Scout.Audit;
using Scout.Plugin.GitHub.Abstractions;

namespace Scout.Host.Console;

/// <summary>The audit library's <see cref="IAuditSnapshots"/> over the GitHub plugin's <see cref="IRepositorySnapshots"/>, copied field by field.</summary>
internal sealed class AuditSnapshotsAdapter(IRepositorySnapshots snapshots) : IAuditSnapshots
{
    public AuditSnapshot? Find(string owner, string repo, string commitOrShortSha) =>
        snapshots.Find(owner, repo, commitOrShortSha) is { } found
            ? new AuditSnapshot(found.Owner, found.Repo, found.Commit, found.Root, found.TakenAt)
            : null;
}
