namespace Scout.Plugin.GitHub.Abstractions;

/// <summary>One commit of a repository, resolved from a branch, a tag, a commit or the default branch.</summary>
/// <param name="Reference">The branch, tag or commit the caller named; the default branch's name when none was given.</param>
/// <param name="Sha">The commit's full SHA.</param>
/// <param name="IsDefaultBranch">True when the reference is the repository's default branch.</param>
public sealed record RepositoryCommit(string Reference, string Sha, bool IsDefaultBranch);

/// <summary>
/// Where a snapshot of a repository becomes a project the Knowledge index can search; the host wires it to the
/// index, a host without one registers nothing.
/// </summary>
public interface IRepositoryProjects
{
    /// <summary>Make <paramref name="path"/> searchable as the project <paramref name="name"/>; a name seen before is replaced and indexed again.</summary>
    void Register(string name, string path);
}
