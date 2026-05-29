namespace Scout.Knowledge;

/// <summary>Configured projects the knowledge agent can answer about.</summary>
public sealed class ProjectsOptions
{
    /// <summary>Configuration section path.</summary>
    public const string SectionName = "Scout:Projects";

    /// <summary>The configured projects.</summary>
    public IList<ProjectEntry> Items { get; set; } = new List<ProjectEntry>();
}

/// <summary>One configured project: a name and its repository path.</summary>
public sealed class ProjectEntry
{
    /// <summary>Project name used to reference it (e.g. "cegos-buddy").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Absolute path to the project's repository root.</summary>
    public string RepoPath { get; set; } = string.Empty;
}
