namespace Scout.Knowledge;

/// <summary>
/// Shared include/skip predicate and path normalization for indexable project files.
/// Used by both the full indexer and the incremental file watcher so they agree on
/// which files count and how relative paths are spelled.
/// </summary>
internal static class ProjectFileFilter
{
    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".json", ".yml", ".yaml",
    };

    private static readonly HashSet<string> DocExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt",
    };

    private static readonly HashSet<string> SkipSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", "node_modules", "dist", ".vs",
    };

    /// <summary>True when the path has an indexable extension and no skipped segment.</summary>
    /// <param name="relativePath">Repo-relative path (either separator accepted).</param>
    public static bool IsIndexable(string relativePath)
    {
        var extension = Path.GetExtension(relativePath);
        if (!CodeExtensions.Contains(extension) && !DocExtensions.Contains(extension))
        {
            return false;
        }

        foreach (var segment in relativePath.Split('/', '\\'))
        {
            if (SkipSegments.Contains(segment))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>"doc" for documentation extensions, otherwise "code".</summary>
    /// <param name="path">A file path; only its extension is inspected.</param>
    public static string KindFor(string path)
        => DocExtensions.Contains(Path.GetExtension(path)) ? "doc" : "code";

    /// <summary>Repo-relative path with forward slashes, regardless of platform separators.</summary>
    /// <param name="repoPath">Repository root.</param>
    /// <param name="absolutePath">Absolute path under the root.</param>
    public static string ToRelativePath(string repoPath, string absolutePath)
        => Path.GetRelativePath(repoPath, absolutePath).Replace('\\', '/');
}
