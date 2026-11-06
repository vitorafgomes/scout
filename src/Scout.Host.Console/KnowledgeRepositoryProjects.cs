using Scout.Knowledge;
using Scout.Plugin.GitHub.Abstractions;

namespace Scout.Host.Console;

/// <summary>A repository snapshot the GitHub plugin took becomes a project of the Knowledge index, searchable as owner/repo.</summary>
internal sealed class KnowledgeRepositoryProjects(ProjectResolutionService projects) : IRepositoryProjects
{
    public void Register(string name, string path) =>
        projects.Register(new ProjectEntry { Name = name, RepoPath = path });
}
