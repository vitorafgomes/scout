namespace Scout.Knowledge;

/// <summary>
/// Shared include/skip predicate and path normalization for indexable project files.
/// Used by both the full indexer and the incremental file watcher so they agree on
/// which files count and how relative paths are spelled.
/// </summary>
internal static class ProjectFileFilter
{
    /// <summary>Source and configuration of every language the bundled skills cover, plus the manifests that define a project.</summary>
    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".fs", ".vb", ".ts", ".tsx", ".mts", ".js", ".jsx", ".mjs", ".cjs", ".py", ".go", ".java", ".kt", ".kts",
        ".rs", ".rb", ".php", ".swift", ".scala", ".dart", ".c", ".h", ".cpp", ".hpp", ".cc", ".hh", ".m", ".mm",
        ".sql", ".sh", ".bash", ".ps1", ".psm1", ".ex", ".exs", ".clj", ".cljs", ".hs", ".lua", ".pl", ".r",
        ".html", ".css", ".scss", ".vue", ".svelte", ".proto", ".graphql", ".tf",
        ".json", ".yml", ".yaml", ".toml", ".xml", ".props", ".targets", ".csproj", ".fsproj", ".sln", ".slnx", ".gradle",
    };

    /// <summary>Files that define a project and carry no extension.</summary>
    private static readonly HashSet<string> CodeFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dockerfile", "Makefile", "Jenkinsfile", "Gemfile", "Rakefile", "Procfile", "CMakeLists.txt",
    };

    private static readonly HashSet<string> DocExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt", ".rst", ".adoc",
    };

    private static readonly HashSet<string> SkipSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", "node_modules", "dist", ".vs", "vendor", ".idea", "__pycache__", ".venv", "venv",
    };

    /// <summary>Output folders only at the repository root: a nested `target/` or `build/` is as likely to be source as output.</summary>
    private static readonly HashSet<string> RootOutputFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "target",
    };

    /// <summary>Files that hold credentials by convention; they never enter the index, whatever their extension.</summary>
    private static readonly HashSet<string> SecretExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pem", ".key", ".pfx", ".p12", ".jks", ".keystore", ".crt", ".cer",
    };

    /// <summary>True when the path has an indexable extension or name (a build file, or a committed .env template), no skipped segment, and is not a credentials file.</summary>
    /// <param name="relativePath">Repo-relative path (either separator accepted).</param>
    public static bool IsIndexable(string relativePath)
    {
        var name = Path.GetFileName(relativePath);
        var extension = Path.GetExtension(relativePath);
        if (IsSecretsFile(name, extension))
        {
            return false;
        }

        var namedBuildFile = CodeFileNames.Contains(name) || (name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) && IsTemplate(name));
        if (!CodeExtensions.Contains(extension) && !DocExtensions.Contains(extension) && !namedBuildFile)
        {
            return false;
        }

        var segments = relativePath.Split('/', '\\');
        if (segments.Length > 1 && RootOutputFolders.Contains(segments[0]))
        {
            return false;
        }

        foreach (var segment in segments)
        {
            if (SkipSegments.Contains(segment))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A .env file (not its committed .example, .sample or .template), a secrets file, or a certificate or key by extension.</summary>
    private static bool IsSecretsFile(string name, string extension) =>
        (name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) && !IsTemplate(name))
        || name.Equals("secrets.json", StringComparison.OrdinalIgnoreCase)
        || SecretExtensions.Contains(extension);

    private static bool IsTemplate(string name) =>
        name.EndsWith(".example", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".sample", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".template", StringComparison.OrdinalIgnoreCase);

    /// <summary>"doc" for documentation extensions, otherwise "code"; a build file named in <see cref="CodeFileNames"/> is code whatever its extension.</summary>
    /// <param name="path">A file path; its name and extension are inspected.</param>
    public static string KindFor(string path)
        => !CodeFileNames.Contains(Path.GetFileName(path)) && DocExtensions.Contains(Path.GetExtension(path)) ? "doc" : "code";

    /// <summary>Repo-relative path with forward slashes, regardless of platform separators.</summary>
    /// <param name="repoPath">Repository root.</param>
    /// <param name="absolutePath">Absolute path under the root.</param>
    public static string ToRelativePath(string repoPath, string absolutePath)
        => Path.GetRelativePath(repoPath, absolutePath).Replace('\\', '/');
}
