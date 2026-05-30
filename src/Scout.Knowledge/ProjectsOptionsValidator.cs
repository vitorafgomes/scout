using Microsoft.Extensions.Logging;

namespace Scout.Knowledge;

/// <summary>
/// Logs a warning for each malformed configured project without throwing, so a
/// mis-configured <see cref="ProjectsOptions.SectionName"/> list (empty name,
/// empty path, or a path that is not on disk) is visible at startup instead of
/// failing silently. Never throws: the host must still start.
/// </summary>
public sealed class ProjectsOptionsValidator
{
    private readonly ILogger<ProjectsOptionsValidator> logger;

    /// <summary>Create the validator.</summary>
    /// <param name="logger">Logger used to surface malformed entries.</param>
    public ProjectsOptionsValidator(ILogger<ProjectsOptionsValidator> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        this.logger = logger;
    }

    /// <summary>
    /// Inspect every configured project and log one warning per problem found.
    /// Does not throw.
    /// </summary>
    /// <param name="options">The configured projects to validate.</param>
    public void Validate(ProjectsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        for (var i = 0; i < options.Items.Count; i++)
        {
            var entry = options.Items[i];

            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                this.logger.LogWarning(
                    "Configured project at {Section}:Items:{Index} has an empty Name and will be ignored.",
                    ProjectsOptions.SectionName,
                    i);
            }

            if (string.IsNullOrWhiteSpace(entry.RepoPath))
            {
                this.logger.LogWarning(
                    "Configured project '{Project}' at {Section}:Items:{Index} has an empty RepoPath and will be ignored.",
                    entry.Name,
                    ProjectsOptions.SectionName,
                    i);
            }
            else if (!Directory.Exists(entry.RepoPath))
            {
                this.logger.LogWarning(
                    "Configured project '{Project}' RepoPath '{RepoPath}' does not exist on disk.",
                    entry.Name,
                    entry.RepoPath);
            }
        }
    }
}
