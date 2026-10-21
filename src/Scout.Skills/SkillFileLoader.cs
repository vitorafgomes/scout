using Microsoft.Extensions.Logging;

namespace Scout.Skills;

/// <summary>
/// Loads <see cref="Skill"/> instances from markdown files in a directory.
/// Each file must start with a <c>---</c> frontmatter fence holding
/// <c>key: value</c> lines (required: <c>name</c>, <c>description</c>), a
/// closing <c>---</c> fence, then a non-empty body. Malformed files are skipped
/// with a warning; loading never throws.
/// </summary>
public sealed class SkillFileLoader
{
    private const string Fence = "---";

    private readonly ILogger<SkillFileLoader> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SkillFileLoader"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public SkillFileLoader(ILogger<SkillFileLoader> logger)
        => _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Loads all valid skills from the given directory. Returns an empty list
    /// for an absent or empty directory; never throws.
    /// </summary>
    /// <param name="directory">The directory to scan for <c>*.md</c> files.</param>
    /// <returns>The valid skills found, in file-name order.</returns>
    public IReadOnlyList<Skill> Load(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return [];
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(directory, "*.md", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate skills in {Directory}.", directory);
            return [];
        }

        var skills = new List<Skill>(files.Length);
        foreach (var file in files)
        {
            var skill = TryParse(file);
            if (skill is not null)
            {
                skills.Add(skill);
            }
        }

        return skills;
    }

    private Skill? TryParse(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read skill file {Path}.", path);
            return null;
        }

        // Normalize line endings so parsing is platform-independent.
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        if (lines.Length == 0 || lines[0].Trim() != Fence)
        {
            _logger.LogWarning("Skill {Path} has no opening frontmatter fence; skipping.", path);
            return null;
        }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var closeIndex = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == Fence)
            {
                closeIndex = i;
                break;
            }

            var line = lines[i];
            var sep = line.IndexOf(':', StringComparison.Ordinal);
            if (sep <= 0)
            {
                continue;
            }

            var key = line[..sep].Trim();
            var value = line[(sep + 1)..].Trim();
            if (key.Length > 0)
            {
                fields[key] = value;
            }
        }

        if (closeIndex < 0)
        {
            _logger.LogWarning("Skill {Path} has no closing frontmatter fence; skipping.", path);
            return null;
        }

        if (!fields.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name) ||
            !fields.TryGetValue("description", out var description) || string.IsNullOrWhiteSpace(description))
        {
            _logger.LogWarning("Skill {Path} is missing name or description; skipping.", path);
            return null;
        }

        var body = string.Join("\n", lines.Skip(closeIndex + 1)).Trim();
        if (body.Length == 0)
        {
            _logger.LogWarning("Skill {Path} has an empty body; skipping.", path);
            return null;
        }

        return new Skill(name, description, body, path);
    }
}
