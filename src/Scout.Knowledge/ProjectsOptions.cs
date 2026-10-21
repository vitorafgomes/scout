namespace Scout.Knowledge;

/// <summary>Configured projects the knowledge agent can answer about.</summary>
public sealed class ProjectsOptions
{
    /// <summary>Configuration section path.</summary>
    public const string SectionName = "Scout:Projects";

    /// <summary>The configured projects.</summary>
    public IList<ProjectEntry> Items { get; set; } = new List<ProjectEntry>();

    /// <summary>Root directories scanned for repositories at startup; explicit Items win on name collision.</summary>
    public IList<string> Roots { get; set; } = new List<string>();

    /// <summary>Incremental file-watch settings; opt-in (default off).</summary>
    public WatchOptions Watch { get; set; } = new();
}

/// <summary>Settings for incremental file-watch re-indexing.</summary>
public sealed class WatchOptions
{
    /// <summary>When true, each project's repo is watched and changed files are re-indexed. Default false.</summary>
    public bool Enabled { get; set; }

    /// <summary>Quiet window in milliseconds before a changed file is re-indexed. Default 750.</summary>
    public int DebounceMilliseconds { get; set; } = 750;
}

/// <summary>One configured project: a name and its repository path.</summary>
public sealed class ProjectEntry
{
    /// <summary>Project name used to reference it in queries and tool calls.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Absolute path to the project's repository root.</summary>
    public string RepoPath { get; set; } = string.Empty;
}
