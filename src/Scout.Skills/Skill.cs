namespace Scout.Skills;

/// <summary>
/// A behavioral instruction unit loaded from a markdown file: a name, a
/// natural-language description used for retrieval matching, an instructional
/// body injected into the system prompt, and the source file it came from.
/// </summary>
/// <param name="Name">The skill name (frontmatter <c>name</c>).</param>
/// <param name="Description">
/// The retrieval description (frontmatter <c>description</c>), embedded and
/// cosine-matched against the user message.
/// </param>
/// <param name="Body">The instructional body injected into the system prompt.</param>
/// <param name="SourcePath">The absolute path of the source markdown file.</param>
public sealed record Skill(string Name, string Description, string Body, string SourcePath);
