using System.ComponentModel;
using System.Globalization;
using Microsoft.SemanticKernel;
using Scout.Plugin.CodeQA.Composing;
using Scout.Plugin.CodeQA.Domain;
using Scout.Plugin.CodeQA.Retrieval;
using CoreCitation = Scout.Core.Chat.Citation;

namespace Scout.Plugin.CodeQA.Functions;

/// <summary>
/// Semantic Kernel functions exposed by <see cref="CodeQAPlugin"/>. All
/// functions are read-only: they either answer from the indexed repository
/// or report that context is missing.
/// </summary>
public sealed class CodeQAFunctions
{
    private readonly IRetriever _retriever;
    private readonly IComposer _composer;

    /// <summary>Creates the function surface bound to a retriever/composer pair.</summary>
    public CodeQAFunctions(IRetriever retriever, IComposer composer)
    {
        _retriever = retriever ?? throw new ArgumentNullException(nameof(retriever));
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
    }

    /// <summary>
    /// Plumbing entry point used by the message router. Kept internal so
    /// external callers go through the public <see cref="KernelFunction"/>
    /// surface rather than depending on the raw <see cref="CodeAnswer"/>.
    /// </summary>
    internal async Task<CodeAnswer> AskAsync(string question, CancellationToken cancellationToken)
    {
        var hits = await _retriever
            .RetrieveAsync(question, topKOverride: null, cancellationToken)
            .ConfigureAwait(false);
        return await _composer
            .ComposeAsync(question, hits, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Streaming entry point used by the chat router. Runs retrieval eagerly
    /// so the source citations are known before the first token, then hands
    /// back the live token stream produced by the composer.
    /// </summary>
    /// <remarks>
    /// Citations are mapped from the full retrieval result. The composer
    /// may trim further internally to respect the token budget, so this
    /// can over-cite relative to what the model actually saw — acceptable
    /// for a footer "Sources:" hint and preferable to under-citing.
    /// </remarks>
    public async Task<StreamingAnswer> StartStreamingAskAsync(
        string question,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        var hits = await _retriever
            .RetrieveAsync(question, topKOverride: null, cancellationToken)
            .ConfigureAwait(false);

        var citations = hits
            .Select(h =>
            {
                var label = $"{h.Chunk.FilePath}:{h.Chunk.StartLine}-{h.Chunk.EndLine}";
                return new CoreCitation(label)
                {
                    Path = h.Chunk.FilePath,
                    Line = h.Chunk.StartLine,
                };
            })
            .ToArray();

        var tokens = _composer.ComposeStreamingAsync(question, hits, cancellationToken);
        return new StreamingAnswer(citations, tokens);
    }

    /// <summary>
    /// Answers a natural-language question about the configured repository.
    /// Returns the LLM's answer followed by a bullet list of citations.
    /// </summary>
    [KernelFunction("ask_code")]
    [Description("Answers a natural-language question about the configured repository using RAG.")]
    public async Task<string> AskCodeAsync(
        [Description("The user's question in plain English.")] string question,
        CancellationToken cancellationToken)
    {
        var answer = await AskAsync(question, cancellationToken).ConfigureAwait(false);
        if (answer.Citations.Count == 0)
        {
            return answer.Text;
        }

        var footer = string.Join("\n", answer.Citations.Select(c => $"- {c}"));
        return $"{answer.Text}\n\nSources:\n{footer}";
    }

    /// <summary>
    /// Returns the top matching code chunks for a query without composing
    /// an answer. Useful for quick "where is X?" lookups.
    /// </summary>
    [KernelFunction("search_code")]
    [Description("Returns the top matching code chunks for a query without composing an answer.")]
    public async Task<string> SearchCodeAsync(
        [Description("Free-text query against the indexed repository.")] string query,
        [Description("Optional override for top-K results (default from configuration).")] int? topK,
        CancellationToken cancellationToken)
    {
        var hits = await _retriever
            .RetrieveAsync(query, topK, cancellationToken)
            .ConfigureAwait(false);
        if (hits.Count == 0)
        {
            return "No matches.";
        }

        var lines = hits.Select(r =>
        {
            var firstLine = r.Chunk.Content
                .Split('\n')
                .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))
                ?.Trim() ?? string.Empty;
            if (firstLine.Length > 120)
            {
                firstLine = firstLine[..120];
            }
            var score = r.Score.ToString("F3", CultureInfo.InvariantCulture);
            return $"- {r.Chunk.FilePath}:{r.Chunk.StartLine}-{r.Chunk.EndLine} (score={score}) - {firstLine}";
        });
        return string.Join("\n", lines);
    }
}
