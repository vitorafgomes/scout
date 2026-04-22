using Scout.Plugin.CodeQA.Composing;
using Scout.Plugin.CodeQA.Domain;
using Scout.Plugin.CodeQA.Storage;

namespace Scout.Plugin.CodeQA.Tests.Composing;

/// <summary>
/// Deterministic <see cref="IComposer"/> for tests that don't need a real
/// LLM. Echoes the question back, maps every retrieved chunk to a citation,
/// and reports a fixed model name and latency.
/// </summary>
internal sealed class FakeComposer : IComposer
{
    private readonly string _modelUsed;
    private readonly TimeSpan _latency;

    public FakeComposer(string modelUsed = "fake-model", TimeSpan? latency = null)
    {
        if (string.IsNullOrWhiteSpace(modelUsed))
        {
            throw new ArgumentException("modelUsed must be non-empty.", nameof(modelUsed));
        }
        _modelUsed = modelUsed;
        _latency = latency ?? TimeSpan.FromMilliseconds(1);
    }

    public Task<CodeAnswer> ComposeAsync(
        string question,
        IReadOnlyList<VectorSearchResult> context,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var citations = context
            .Select(r => new Citation(
                r.Chunk.FilePath,
                r.Chunk.StartLine,
                r.Chunk.EndLine,
                Math.Clamp(r.Score, -1.0, 1.0)))
            .ToList();

        var text = $"ECHO: {question}";
        return Task.FromResult(new CodeAnswer(text, citations, _modelUsed, _latency));
    }
}
