namespace Scout.Skills;

/// <summary>
/// Settings for the retrieval-based skills system. Bound from
/// <see cref="SectionName"/>. Code defaults make the feature work without any
/// configuration section present.
/// </summary>
public sealed class SkillsOptions
{
    /// <summary>Configuration section path.</summary>
    public const string SectionName = "Scout:Skills";

    /// <summary>Whether skill retrieval is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Directory holding skill markdown files, resolved relative to
    /// <see cref="AppContext.BaseDirectory"/> when not absolute.
    /// </summary>
    public string Directory { get; set; } = "skills";

    /// <summary>Maximum number of skills to inject per turn.</summary>
    public int TopN { get; set; } = 1;

    /// <summary>
    /// Minimum cosine similarity a skill description must reach to be selected.
    /// </summary>
    public double SimilarityThreshold { get; set; } = 0.55;
}
