namespace Scout.Knowledge;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

/// <summary>
/// Turns a project question into a citation-tagged context string for the language model.
/// </summary>
public sealed class ProjectKnowledgeService
{
    private const int TopResults = 5;

    private readonly IEmbeddingGenerator<string, Embedding<float>> embedder;
    private readonly IProjectKnowledgeStore store;
    private readonly ILogger<ProjectKnowledgeService> logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProjectKnowledgeService"/> class.
    /// </summary>
    /// <param name="embedder">The embedding generator.</param>
    /// <param name="store">The knowledge store.</param>
    /// <param name="logger">The logger.</param>
    public ProjectKnowledgeService(
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        IProjectKnowledgeStore store,
        ILogger<ProjectKnowledgeService> logger)
    {
        this.embedder = embedder;
        this.store = store;
        this.logger = logger;
    }

    /// <summary>
    /// Searches the project for content relevant to the question and returns a cited context string.
    /// Never throws: returns a clean message on empty input, no results, or failure.
    /// </summary>
    /// <param name="project">The project identifier.</param>
    /// <param name="question">The natural-language question.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A citation-tagged context string, or a clean status message.</returns>
    public async Task<string> SearchAsync(
        string project,
        string question,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(question))
        {
            return "Provide a project and a question.";
        }

        try
        {
            var vector = await this.embedder.GenerateVectorAsync(
                question,
                cancellationToken: cancellationToken);

            var hits = await this.store.SearchAsync(project, vector, TopResults, cancellationToken);

            if (hits.Count == 0)
            {
                return $"No indexed content found for project '{project}'. Run: index {project}";
            }

            var blocks = hits.Select(FormatHit);
            return string.Join("\n\n", blocks);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Failed to search project {Project}.", project);
            return $"Could not search project '{project}' right now.";
        }
    }

    private static string FormatHit(ChunkRecord record)
    {
        var header = string.IsNullOrWhiteSpace(record.Symbol)
            ? $"[{record.Path}:{record.StartLine}-{record.EndLine}]"
            : $"[{record.Path}:{record.StartLine}-{record.EndLine} {record.Symbol}]";
        return $"{header}\n{record.Content}";
    }
}
