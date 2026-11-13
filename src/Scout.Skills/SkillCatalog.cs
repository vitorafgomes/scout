using Microsoft.Extensions.Options;

namespace Scout.Skills;

/// <summary>
/// Embedding-free skill catalog. Loads every skill once via
/// <see cref="SkillFileLoader"/> from the configured directory on first access
/// and caches the result for the process lifetime, so DI build and host startup
/// perform zero file I/O. Depends only on the loader and options, never on the
/// embedding generator, keeping the kernel-aggregated <see cref="SkillsPlugin"/>
/// free of the construction cycle.
/// </summary>
public sealed class SkillCatalog : ISkillCatalog
{
    private readonly Lazy<IReadOnlyList<Skill>> _skills;

    /// <summary>
    /// Initializes a new instance of the <see cref="SkillCatalog"/> class.
    /// </summary>
    /// <param name="loader">The skill file loader.</param>
    /// <param name="options">The skills options.</param>
    public SkillCatalog(SkillFileLoader loader, IOptions<SkillsOptions> options)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(options);
        var settings = options.Value ?? throw new ArgumentException(
            "SkillsOptions must be configured.", nameof(options));

        // Lazy + publication-only execution guarantees files load once, on first
        // use, never in the constructor or DI factory.
        _skills = new Lazy<IReadOnlyList<Skill>>(
            () => loader.Load(ResolveDirectory(settings.Directory)),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public IReadOnlyList<Skill> All() => _skills.Value;

    /// <inheritdoc />
    public Skill? Find(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        foreach (var skill in _skills.Value)
        {
            if (string.Equals(skill.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return skill;
            }
        }

        return null;
    }

    private static string ResolveDirectory(string directory)
        => Path.IsPathRooted(directory)
            ? directory
            : Path.Combine(AppContext.BaseDirectory, directory);
}
