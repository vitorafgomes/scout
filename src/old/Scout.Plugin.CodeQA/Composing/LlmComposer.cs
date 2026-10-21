using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Plugin.CodeQA.Domain;
using Scout.Plugin.CodeQA.Storage;

namespace Scout.Plugin.CodeQA.Composing;

/// <summary>
/// Default <see cref="IComposer"/> implementation. Wraps
/// <see cref="IChatCompletionService"/> (Semantic Kernel) with Scout's
/// system prompt, token-budget context truncation, and citation mapping.
/// </summary>
public sealed class LlmComposer : IComposer
{
    private const int PerChunkStructuralOverheadTokens = 20;

    private readonly IOptions<CodeQAOptions> _options;
    private readonly IChatCompletionService _chat;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LlmComposer> _logger;

    /// <summary>Creates a composer bound to the given chat service.</summary>
    public LlmComposer(
        IOptions<CodeQAOptions> options,
        IChatCompletionService chat,
        TimeProvider? timeProvider = null,
        ILoggerFactory? loggerFactory = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<LlmComposer>();
    }

    /// <inheritdoc />
    public async Task<CodeAnswer> ComposeAsync(
        string question,
        IReadOnlyList<VectorSearchResult> context,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentNullException.ThrowIfNull(context);

        var opts = _options.Value;
        var composer = opts.Composer;

        var (history, settings, trimmed) = BuildRequest(question, context, opts);

        var start = _timeProvider.GetTimestamp();
        var result = await _chat
            .GetChatMessageContentAsync(history, settings, kernel: null, cancellationToken)
            .ConfigureAwait(false);
        var elapsed = _timeProvider.GetElapsedTime(start);
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (string.IsNullOrWhiteSpace(result.Content))
        {
            throw new InvalidOperationException("Chat service returned an empty response.");
        }

        var citations = trimmed
            .Select(r => new Citation(
                r.Chunk.FilePath,
                r.Chunk.StartLine,
                r.Chunk.EndLine,
                Math.Clamp(r.Score, -1.0, 1.0)))
            .ToList();

        var model = string.IsNullOrWhiteSpace(composer.Model)
            ? (result.ModelId ?? "(provider default)")
            : composer.Model;

        _logger.LogDebug(
            "Composer produced answer: chunks={ChunkCount}, latencyMs={LatencyMs}, model={Model}.",
            citations.Count, elapsed.TotalMilliseconds, model);

        return new CodeAnswer(result.Content!, citations, model, elapsed);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> ComposeStreamingAsync(
        string question,
        IReadOnlyList<VectorSearchResult> context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentNullException.ThrowIfNull(context);

        var opts = _options.Value;
        var (history, settings, _) = BuildRequest(question, context, opts);

        var start = _timeProvider.GetTimestamp();
        var chunkCount = 0;

        var stream = _chat.GetStreamingChatMessageContentsAsync(
            history, settings, kernel: null, cancellationToken);

        await foreach (var chunk in stream.ConfigureAwait(false))
        {
            if (!string.IsNullOrEmpty(chunk.Content))
            {
                chunkCount++;
                yield return chunk.Content!;
            }
        }

        var elapsed = _timeProvider.GetElapsedTime(start);
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }
        _logger.LogDebug(
            "Streaming composer produced {ChunkCount} chunks in {LatencyMs} ms.",
            chunkCount, elapsed.TotalMilliseconds);
    }

    private (ChatHistory History, PromptExecutionSettings Settings, List<VectorSearchResult> Trimmed)
        BuildRequest(
            string question,
            IReadOnlyList<VectorSearchResult> context,
            CodeQAOptions opts)
    {
        var composer = opts.Composer;
        var trimmed = TruncateToBudget(context, opts);

        var history = new ChatHistory();
        history.AddSystemMessage(CodeQAPromptTemplate.SystemPrompt);
        history.AddUserMessage(CodeQAPromptTemplate.BuildUserPrompt(question, trimmed));

        var settings = new PromptExecutionSettings
        {
            ExtensionData = new Dictionary<string, object>
            {
                ["temperature"] = composer.Temperature,
                ["max_tokens"] = composer.MaxOutputTokens,
            },
        };

        return (history, settings, trimmed);
    }

    private static List<VectorSearchResult> TruncateToBudget(
        IReadOnlyList<VectorSearchResult> context,
        CodeQAOptions opts)
    {
        var budget = opts.Retrieval.MaxContextTokens;
        var approx = Math.Max(1, opts.Chunking.ApproxBytesPerToken);
        var trimmed = new List<VectorSearchResult>(context.Count);
        var running = 0;

        foreach (var hit in context)
        {
            var cost = (hit.Chunk.Content.Length / approx) + PerChunkStructuralOverheadTokens;
            if (trimmed.Count > 0 && running + cost > budget)
            {
                break;
            }
            trimmed.Add(hit);
            running += cost;
        }

        return trimmed;
    }
}
