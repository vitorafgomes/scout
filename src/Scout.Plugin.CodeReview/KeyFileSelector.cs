namespace Scout.Plugin.CodeReview;

/// <summary>
/// Ranks and caps the blob entries of a repository tree down to the handful of
/// files most useful for a repository-level review. The goal is a small,
/// representative corpus: manifests and entry points first, then the shallowest
/// and largest remaining source files. Tests, build output, vendored code, and
/// generated or non-text files are excluded.
/// </summary>
internal static class KeyFileSelector
{
    private static readonly string[] ExcludedSegments =
    {
        "tests", "test", "__tests__", "spec",
        "bin", "obj", "node_modules", "dist", "build", "vendor", ".git",
    };

    // Extensions worth feeding to a code review. Anything outside this allowlist
    // (images, archives, binaries, lock files, etc.) is skipped.
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".fs", ".vb", ".csproj", ".fsproj", ".vbproj", ".sln", ".slnx", ".props", ".targets",
        ".js", ".jsx", ".ts", ".tsx", ".mjs", ".cjs", ".vue", ".svelte",
        ".py", ".pyi", ".go", ".rs", ".java", ".kt", ".kts", ".scala", ".rb", ".php",
        ".c", ".h", ".cc", ".cpp", ".hpp", ".cxx", ".m", ".mm", ".swift",
        ".json", ".yaml", ".yml", ".toml", ".xml", ".gradle", ".sql",
        ".sh", ".bash", ".ps1", ".psm1", ".dockerfile",
        ".md", ".txt", ".rst", ".cshtml", ".razor", ".html", ".css", ".scss",
    };

    private static readonly string[] ManifestNames =
    {
        "package.json", "pyproject.toml", "go.mod", "pom.xml", "cargo.toml",
    };

    private static readonly string[] ManifestExtensions =
    {
        ".csproj", ".sln", ".slnx",
    };

    private static readonly string[] EntryPointNames =
    {
        "program.cs",
    };

    private static readonly string[] EntryPointStems =
    {
        "main", "index", "app",
    };

    /// <summary>
    /// Selects up to <paramref name="maxFiles"/> blob paths from
    /// <paramref name="entries"/>, ranked by review usefulness.
    /// </summary>
    /// <param name="entries">All tree entries; non-blob entries are ignored.</param>
    /// <param name="maxFiles">Maximum number of paths to return.</param>
    /// <returns>The selected repository-relative paths, most useful first.</returns>
    public static IReadOnlyList<string> Select(IReadOnlyList<TreeEntry> entries, int maxFiles)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (maxFiles <= 0)
        {
            return [];
        }

        var candidates = entries
            .Where(e => string.Equals(e.Type, "blob", StringComparison.Ordinal))
            .Where(e => !string.IsNullOrWhiteSpace(e.Path))
            .Where(IsReviewable)
            .ToList();

        return candidates
            .OrderBy(RankOf)
            .ThenBy(DepthOf)
            .ThenByDescending(e => e.Size)
            .ThenBy(e => e.Path, StringComparer.Ordinal)
            .Select(e => e.Path)
            .Take(maxFiles)
            .ToList();
    }

    private static bool IsReviewable(TreeEntry entry)
    {
        var path = entry.Path;
        var fileName = FileName(path);

        if (fileName.Contains(".min.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IsGenerated(fileName))
        {
            return false;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Compare directory segments only (exclude the file name itself).
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (ExcludedSegments.Contains(segments[i], StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return IsManifest(fileName)
            || IsReadme(fileName)
            || TextExtensions.Contains(Extension(fileName));
    }

    private static int RankOf(TreeEntry entry)
    {
        var fileName = FileName(entry.Path);
        if (IsManifest(fileName))
        {
            return 0;
        }
        if (IsReadme(fileName))
        {
            return 1;
        }
        if (IsEntryPoint(fileName))
        {
            return 2;
        }
        return 3;
    }

    private static int DepthOf(TreeEntry entry)
        => entry.Path.Count(c => c == '/');

    private static bool IsManifest(string fileName)
        => ManifestNames.Contains(fileName, StringComparer.OrdinalIgnoreCase)
            || ManifestExtensions.Contains(Extension(fileName), StringComparer.OrdinalIgnoreCase);

    private static bool IsReadme(string fileName)
        => fileName.StartsWith("readme", StringComparison.OrdinalIgnoreCase);

    private static bool IsEntryPoint(string fileName)
    {
        if (EntryPointNames.Contains(fileName, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        var stem = StemOf(fileName);
        return EntryPointStems.Contains(stem, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsGenerated(string fileName)
        => fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".pb.go", StringComparison.OrdinalIgnoreCase);

    private static string FileName(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    private static string Extension(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot < 0 ? string.Empty : fileName[dot..];
    }

    private static string StemOf(string fileName)
    {
        var dot = fileName.IndexOf('.');
        return dot < 0 ? fileName : fileName[..dot];
    }
}
