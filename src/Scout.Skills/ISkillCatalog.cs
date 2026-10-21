namespace Scout.Skills;

/// <summary>
/// Embedding-free access to the loaded skills: names, descriptions, and bodies.
/// Backs the tool-based <see cref="SkillsPlugin"/> so the shared kernel can
/// aggregate that plugin without pulling in the embedding generator, avoiding the
/// kernel-to-embedder construction cycle. Files load lazily on first access.
/// </summary>
public interface ISkillCatalog
{
    /// <summary>All loaded skills, in file-name order. Empty when none exist.</summary>
    /// <returns>The loaded skills.</returns>
    IReadOnlyList<Skill> All();

    /// <summary>Find a skill by exact (case-insensitive) name.</summary>
    /// <param name="name">The skill name from <see cref="Skill.Name"/>.</param>
    /// <returns>The matching skill, or <c>null</c> when none matches.</returns>
    Skill? Find(string name);
}
