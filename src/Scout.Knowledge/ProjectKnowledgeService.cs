namespace Scout.Knowledge;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

/// <summary>
/// Turns a project question into a citation-tagged context string for the language model.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="ProjectKnowledgeService"/> class.
/// </remarks>
/// <param name="embedder">The embedding generator.</param>
/// <param name="store">The knowledge store.</param>
/// <param name="resolution">Resolves a project name and lazily indexes it on first use.</param>
/// <param name="logger">The logger.</param>
public sealed class ProjectKnowledgeService(
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    IProjectKnowledgeStore store,
    ProjectResolutionService resolution,
    ILogger<ProjectKnowledgeService> logger)
{
    private const int TopResults = 5;
    private const int FindSymbolTopResults = 10;

    private readonly IEmbeddingGenerator<string, Embedding<float>> embedder = embedder;
    private readonly IProjectKnowledgeStore store = store;
    private readonly ProjectResolutionService resolution = resolution;
    private readonly ILogger<ProjectKnowledgeService> logger = logger;

    /// <summary>
    /// Searches the project for content relevant to the question and returns a cited context string:
    /// by vector similarity, or by full-text search when the AI provider has no embeddings or the
    /// vector search finds nothing (a project indexed without vectors).
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

        var entry = await this.resolution.ResolveAsync(project, cancellationToken);

        if (entry is null)
        {
            return $"Unknown project '{project}'.";
        }

        try
        {
            var vector = await this.VectorOrNullAsync(question, cancellationToken);
            if (vector is not { } query)
            {
                var textHits = await this.store.SearchTextAsync(entry.Name, question, TopResults, cancellationToken);
                return textHits.Count == 0
                    ? $"No content in project '{project}' matches the question."
                    : string.Join("\n\n", textHits.Select(FormatHit));
            }

            var hits = await this.store.SearchAsync(entry.Name, query, TopResults, cancellationToken);
            if (hits.Count == 0)
            {
                hits = await this.store.SearchTextAsync(entry.Name, question, TopResults, cancellationToken);
            }

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

    /// <summary>
    /// Finds a symbol by name in an indexed project using metadata only (no embedding).
    /// Exact matches come before partial matches. Never throws: returns a clean message
    /// on empty input, no results, or failure.
    /// </summary>
    /// <param name="project">The project identifier.</param>
    /// <param name="symbol">The symbol name to look up.</param>
    /// <param name="kind">Optional coarse kind filter ("code"/"doc"); null or empty to skip.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <param name="top">Maximum number of results; non-positive falls back to the default.</param>
    /// <returns>A citation-tagged context string, or a clean status message.</returns>
    public async Task<string> FindSymbolAsync(
        string project,
        string symbol,
        string? kind,
        CancellationToken cancellationToken,
        int top = FindSymbolTopResults)
    {
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(symbol))
        {
            return "Provide a project and a symbol.";
        }

        var entry = await this.resolution.ResolveAsync(project, cancellationToken);

        if (entry is null)
        {
            return $"Unknown project '{project}'.";
        }

        var limit = top > 0 ? top : FindSymbolTopResults;

        try
        {
            var hits = await this.store.FindSymbolAsync(entry.Name, symbol, kind, limit, cancellationToken);

            if (hits.Count == 0)
            {
                return $"No symbol matching '{symbol}' found in project '{project}'. If the project is not indexed yet, run: index {project}.";
            }

            var blocks = hits.Select(FormatHit);
            return string.Join("\n\n", blocks);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Failed to find symbol {Symbol} in project {Project}.", symbol, project);
            return $"Could not find symbol '{symbol}' in project '{project}' right now.";
        }
    }

    private async Task<ReadOnlyMemory<float>?> VectorOrNullAsync(string question, CancellationToken cancellationToken)
    {
        try
        {
            return await this.embedder.GenerateVectorAsync(question, cancellationToken: cancellationToken);
        }
        catch (NotSupportedException)
        {
            return null;
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
