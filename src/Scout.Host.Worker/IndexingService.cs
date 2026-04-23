using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scout.Plugin.CodeQA;
using Scout.Plugin.CodeQA.Chunking;
using Scout.Plugin.CodeQA.Domain;
using Scout.Plugin.CodeQA.Embedding;
using Scout.Plugin.CodeQA.Indexing;
using Scout.Plugin.CodeQA.Storage;

namespace Scout.Host.Worker;

/// <summary>
/// Host-side orchestrator that drives the CodeQA ingestion pipeline end
/// to end: pull files from GitHub, chunk them, embed the chunks in
/// provider-sized batches, then upsert the resulting vectors. Used both
/// by the <c>index</c> CLI entry point and by the startup indexer that
/// runs alongside the worker.
/// </summary>
internal sealed class IndexingService
{
    private readonly IIngestor _ingestor;
    private readonly IChunker _chunker;
    private readonly IEmbedder _embedder;
    private readonly IVectorStore _store;
    private readonly CodeQAOptions _options;
    private readonly ILogger<IndexingService> _logger;

    public IndexingService(
        IIngestor ingestor,
        IChunker chunker,
        IEmbedder embedder,
        IVectorStore store,
        IOptions<CodeQAOptions> options,
        ILogger<IndexingService> logger)
    {
        _ingestor = ingestor ?? throw new ArgumentNullException(nameof(ingestor));
        _chunker = chunker ?? throw new ArgumentNullException(nameof(chunker));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Runs one full ingestion pass. Exceptions from any stage propagate
    /// unchanged — callers own the retry / failure policy.
    /// </summary>
    public async Task<IndexingSummary> RunAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        var files = await _ingestor.IngestAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Ingested {Files} files", files.Count);

        var chunks = new List<CodeChunk>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileChunks = _chunker.Chunk(file.Content, file.Path, cancellationToken);
            chunks.AddRange(fileChunks);
        }
        _logger.LogInformation("Produced {Chunks} chunks", chunks.Count);

        var batchSize = Math.Max(1, _options.Embeddings.MaxBatchSize);
        var embedded = new List<CodeChunk>(chunks.Count);
        for (var offset = 0; offset < chunks.Count; offset += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var take = Math.Min(batchSize, chunks.Count - offset);
            var batch = chunks.GetRange(offset, take);
            var texts = batch.Select(c => c.Content).ToArray();

            var vectors = await _embedder
                .EmbedAsync(texts, cancellationToken)
                .ConfigureAwait(false);

            for (var i = 0; i < batch.Count; i++)
            {
                var original = batch[i];
                embedded.Add(new CodeChunk(
                    Id: original.Id,
                    FilePath: original.FilePath,
                    StartLine: original.StartLine,
                    EndLine: original.EndLine,
                    Content: original.Content,
                    Embedding: vectors[i]));
            }
        }

        if (embedded.Count > 0)
        {
            await _store.UpsertAsync(embedded, cancellationToken).ConfigureAwait(false);
        }
        _logger.LogInformation("Stored {Vectors} vectors", embedded.Count);

        stopwatch.Stop();
        var elapsed = stopwatch.Elapsed;
        _logger.LogInformation("Elapsed {Elapsed}", elapsed);

        return new IndexingSummary(
            FilesIngested: files.Count,
            ChunksProduced: chunks.Count,
            VectorsWritten: embedded.Count,
            Elapsed: elapsed);
    }
}

/// <summary>Counters from one <see cref="IndexingService.RunAsync"/> pass.</summary>
internal sealed record IndexingSummary(
    int FilesIngested,
    int ChunksProduced,
    int VectorsWritten,
    TimeSpan Elapsed);
