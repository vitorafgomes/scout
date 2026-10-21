using System.ComponentModel;
using Microsoft.SemanticKernel;

namespace Scout.Skills;

/// <summary>
/// Tool-based skills surface exposed to the LLM as Semantic Kernel functions.
/// The model lists available skills and loads a named skill's full instructions
/// on demand, complementing the retrieval-based auto-injection. Depends only on
/// the embedding-free <see cref="ISkillCatalog"/>, so the shared kernel can
/// aggregate this plugin without re-entering the embedder. Every call returns a
/// clean, non-empty string and never throws back into the kernel.
/// </summary>
public sealed class SkillsPlugin
{
    private readonly ISkillCatalog _catalog;

    /// <summary>Creates the plugin over an embedding-free skill catalog.</summary>
    /// <param name="catalog">The skill catalog. Must be non-null.</param>
    public SkillsPlugin(ISkillCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <summary>List the available skills by name and when to use each.</summary>
    /// <returns>One <c>name: description</c> line per skill, or a clean message when none.</returns>
    [KernelFunction("list_skills")]
    [Description("List the available skills (process guides) by name and when to use each.")]
    public string ListSkills()
    {
        var skills = _catalog.All();
        return skills.Count == 0
            ? "No skills available."
            : string.Join("\n", skills.Select(s => $"{s.Name}: {s.Description}"));
    }

    /// <summary>Load the full instructions for a named skill.</summary>
    /// <param name="name">Exact skill name from <c>list_skills</c>.</param>
    /// <returns>The skill body, or a clean not-found message when unknown.</returns>
    [KernelFunction("load_skill")]
    [Description("Load the full instructions for a named skill. Use after list_skills to get a skill's guidance.")]
    public string LoadSkill(
        [Description("Exact skill name from list_skills.")] string name)
    {
        var skill = _catalog.Find(name);
        return skill is null
            ? $"Skill '{name}' not found. Use list_skills to see available skills."
            : skill.Body;
    }
}
