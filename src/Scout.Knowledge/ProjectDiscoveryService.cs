using Microsoft.Extensions.Logging;

namespace Scout.Knowledge;

/// <summary>
/// Discovers repositories directly under a configured root directory.
/// A child directory is treated as a repository when it contains a .git entry
/// or a recognizable project marker. Reads directory listings only; never
/// inspects file contents and never writes.
/// </summary>
/// <remarks>Creates the discovery service.</remarks>
public sealed class ProjectDiscoveryService(ILogger<ProjectDiscoveryService> logger)
{
    private static readonly string[] MarkerFiles =
    {
        "package.json", "go.mod", "pyproject.toml", "Cargo.toml",
    };

    private static readonly string[] MarkerGlobs =
    {
        "*.sln", "*.slnx", "*.csproj",
    };

    private readonly ILogger<ProjectDiscoveryService> _logger = logger;

    /// <summary>
    /// Enumerates repositories directly under <paramref name="rootPath"/>.
    /// Returns an empty list (with a warning) when the root is null, empty,
    /// missing, or cannot be opened or listed. Never throws for a single unreadable child.
    /// </summary>
    public Task<IReadOnlyList<ProjectEntry>> DiscoverAsync(string? rootPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            _logger.LogWarning("Discovery root is null or empty; skipping.");
            return Task.FromResult<IReadOnlyList<ProjectEntry>>(Array.Empty<ProjectEntry>());
        }

        if (!Directory.Exists(rootPath))
        {
            _logger.LogWarning("Discovery root does not exist: {Root}", rootPath);
            return Task.FromResult<IReadOnlyList<ProjectEntry>>(Array.Empty<ProjectEntry>());
        }

        var entries = new List<ProjectEntry>();
        string[] children;
        try
        {
            children = ListDirectories(rootPath).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to enumerate discovery root: {Root}", rootPath);
            return Task.FromResult<IReadOnlyList<ProjectEntry>>(Array.Empty<ProjectEntry>());
        }

        foreach (var child in children)
        {
            ct.ThrowIfCancellationRequested();

            if (!IsRepository(child))
            {
                continue;
            }

            var name = Path.GetFileName(child.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            entries.Add(new ProjectEntry { Name = name, RepoPath = child });
        }

        _logger.LogInformation("Discovered {Count} repositories under {Root}", entries.Count, rootPath);
        return Task.FromResult<IReadOnlyList<ProjectEntry>>(entries);
    }

    private static bool IsRepository(string directory)
    {
        try
        {
            if (Directory.Exists(Path.Combine(directory, ".git")) ||
                File.Exists(Path.Combine(directory, ".git")))
            {
                return true;
            }

            foreach (var marker in MarkerFiles)
            {
                if (File.Exists(Path.Combine(directory, marker)))
                {
                    return true;
                }
            }

            foreach (var glob in MarkerGlobs)
            {
                if (Directory.EnumerateFiles(directory, glob, SearchOption.TopDirectoryOnly).Any())
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// Lists the folders directly under a root; the real directory listing unless a test supplies one that fails while
    /// it is being read. Exists for tests only and is never set outside them.
    /// </summary>
    internal Func<string, IEnumerable<string>> ListDirectories { get; init; } = Directory.EnumerateDirectories;
}
