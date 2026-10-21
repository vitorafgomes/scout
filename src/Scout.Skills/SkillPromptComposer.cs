using System.Text;

namespace Scout.Skills;

/// <summary>
/// Composes the per-turn system prompt by appending the bodies of the selected
/// skills under a delimiter beneath the base prompt.
/// </summary>
public static class SkillPromptComposer
{
    /// <summary>
    /// Returns the base prompt unchanged when no skills are supplied; otherwise
    /// the base prompt followed by each skill body under a labeled delimiter.
    /// </summary>
    /// <param name="basePrompt">The base system prompt.</param>
    /// <param name="skills">The selected skills, in injection order.</param>
    /// <returns>The composed system prompt.</returns>
    public static string Compose(string basePrompt, IReadOnlyList<Skill> skills)
    {
        ArgumentNullException.ThrowIfNull(skills);

        if (skills.Count == 0)
        {
            return basePrompt;
        }

        var builder = new StringBuilder(basePrompt);
        foreach (var skill in skills)
        {
            builder.Append("\n\n# Relevant skill: ")
                .Append(skill.Name)
                .Append('\n')
                .Append(skill.Body);
        }

        return builder.ToString();
    }
}
