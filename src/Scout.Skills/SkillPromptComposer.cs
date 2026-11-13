using System.Text;

namespace Scout.Skills;

/// <summary>
/// Composes the per-turn system prompt by appending the bodies of the selected
/// skills under a delimiter beneath the base prompt and, when the model chooses the
/// skill itself, a catalog of every skill's name and one-line summary.
/// </summary>
public static class SkillPromptComposer
{
    private const int SummaryMaxLength = 160;

    private static readonly string[] ClauseDashes = [" - ", " \u2014 "];

    /// <summary>
    /// Returns the base prompt unchanged when no skills and no catalog are supplied;
    /// otherwise the base prompt followed by each skill body under a labeled delimiter
    /// and, when <paramref name="modelChoiceCatalog"/> is not empty, the skills section.
    /// </summary>
    /// <param name="basePrompt">The base system prompt.</param>
    /// <param name="skills">The selected skills, in injection order.</param>
    /// <param name="modelChoiceCatalog">The skills the model chooses from itself; empty or null when the selector selects.</param>
    /// <returns>The composed system prompt.</returns>
    public static string Compose(
        string basePrompt,
        IReadOnlyList<Skill> skills,
        IReadOnlyList<Skill>? modelChoiceCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(skills);

        if (skills.Count == 0 && (modelChoiceCatalog is null || modelChoiceCatalog.Count == 0))
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

        if (modelChoiceCatalog is { Count: > 0 })
        {
            builder.Append("\n\n").Append(CatalogHeader(modelChoiceCatalog));
            builder.AppendJoin('\n', modelChoiceCatalog.Select(s => $"- {s.Name}: {Summary(s.Description)}"));
        }

        return builder.ToString();
    }

    private static string CatalogHeader(IReadOnlyList<Skill> catalog)
    {
        var routes = new List<string>(2);
        if (catalog.Any(s => string.Equals(s.Name, "code-review", StringComparison.OrdinalIgnoreCase)))
        {
            routes.Add("for a code review of a pull request use code-review");
        }

        if (catalog.Any(s => string.Equals(s.Name, "code-audit", StringComparison.OrdinalIgnoreCase)))
        {
            routes.Add("for a review or audit of a whole repository or project use code-audit");
        }

        var routing = routes.Count == 0 ? string.Empty : "; " + string.Join(", ", routes);
        return $"# Skills\nBefore answering, if the request matches one of these skills, call load_skill with its name and follow it{routing}.\n";
    }

    private static string Summary(string description)
    {
        var text = description.Trim().Trim('"').Trim();
        var end = text.Length;
        foreach (var dash in ClauseDashes)
        {
            var at = text.IndexOf(dash, StringComparison.Ordinal);
            if (at >= 0 && at < end)
            {
                end = at;
            }
        }

        for (var i = 0; i < end; i++)
        {
            if (text[i] == '.' && (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1])))
            {
                end = i;
                break;
            }
        }

        return text[..Math.Min(end, SummaryMaxLength)].Trim();
    }
}
