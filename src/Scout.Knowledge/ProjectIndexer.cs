using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Scout.Knowledge;

/// <summary>
/// Walks a project's local repo, chunks each file, embeds the chunks, and replaces the project's chunk set in the store.
/// When the provider has no embeddings, or its vectors are not <see cref="ChunkRecord.EmbeddingDimensions"/> long, the
/// chunks are stored without a vector, and the embedder is not asked again.
/// </summary>
public sealed class ProjectIndexer(
    IChunker chunker,
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    IProjectKnowledgeStore store,
    ILogger<ProjectIndexer> logger)
{
    private const int MaxEmbeddingInputCharacters = 16_000;

    private volatile bool _withoutEmbeddings;
    private volatile int _foreignDimensions;

    /// <summary>
    /// Index a project's repository in place: enumerate, chunk, embed, and replace the project's chunks. A file or a
    /// folder below the root that cannot be read is skipped with a warning, while a root that cannot be read throws; an embedding call that fails throws before anything is replaced, so
    /// the index the project already has is left as it was. The path the chunks came from is recorded after them; when that write fails the index stays and a warning is logged.
    /// </summary>
    /// <param name="project">The project to index; its <see cref="ProjectEntry.RepoPath"/> is read locally.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task IndexAsync(ProjectEntry project, CancellationToken ct)
    {
        var records = new List<ChunkRecord>();
        int filesIndexed = 0;

        foreach (var path in EnumerateFiles(project.RepoPath))
        {
            ct.ThrowIfCancellationRequested();

            string text;
            try
            {
                text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(
                    ex,
                    "Skipping file that could not be read: {Path}",
                    ProjectFileFilter.ToRelativePath(project.RepoPath, path));
                continue;
            }

            var fileRecords = await BuildRecordsAsync(project, path, text, ct).ConfigureAwait(false);
            if (fileRecords.Count == 0)
            {
                continue;
            }

            records.AddRange(fileRecords);
            filesIndexed++;
        }

        await store.ReplaceProjectAsync(project.Name, records, ct).ConfigureAwait(false);
        try
        {
            await store.SetIndexedPathAsync(project.Name, project.RepoPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(
                ex,
                "Indexed project {Project}, but could not record the path it was indexed from; it is recorded on a later use.",
                project.Name);
        }

        logger.LogInformation(
            "Indexed project {Project}: {Files} files, {Chunks} chunks.",
            project.Name,
            filesIndexed,
            records.Count);

        if (_foreignDimensions != 0)
        {
            logger.LogWarning(
                "Vectors are off for {Project}: the embedding model returns {Dimensions} dimensions, the index stores {Stored}.",
                project.Name,
                _foreignDimensions,
                ChunkRecord.EmbeddingDimensions);
        }
        else if (_withoutEmbeddings)
        {
            logger.LogInformation(
                "Indexed project {Project} without embeddings: the AI provider has none, so search_project and find_symbol use the full-text index.",
                project.Name);
        }
    }

    /// <summary>
    /// Read, chunk, embed, and build the chunk records for a single file. Returns an empty list only when the file
    /// yields no chunks; a file that cannot be read and an embedding call that fails both throw, so the caller never
    /// takes a failure for an empty file.
    /// A provider without embeddings (<see cref="NotSupportedException"/>), or one whose vectors are not
    /// <see cref="ChunkRecord.EmbeddingDimensions"/> long, yields records with an empty <see cref="ChunkRecord.Embedding"/>.
    /// A field is never embedded, and the text sent for any chunk is cut at a fixed length; the stored content is whole.
    /// </summary>
    /// <param name="project">The owning project; supplies name and repo root.</param>
    /// <param name="absolutePath">Absolute path to the file on disk.</param>
    /// <param name="ct">Cancellation token.</param>
    internal async Task<IReadOnlyList<ChunkRecord>> BuildRecordsForFileAsync(
        ProjectEntry project,
        string absolutePath,
        CancellationToken ct)
    {
        var text = await File.ReadAllTextAsync(absolutePath, ct).ConfigureAwait(false);
        return await BuildRecordsAsync(project, absolutePath, text, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ChunkRecord>> BuildRecordsAsync(
        ProjectEntry project,
        string absolutePath,
        string text,
        CancellationToken ct)
    {
        var kind = ProjectFileFilter.KindFor(absolutePath);
        var relPath = ProjectFileFilter.ToRelativePath(project.RepoPath, absolutePath);

        var chunks = chunker.Chunk(relPath, text);
        if (chunks.Count == 0)
        {
            return Array.Empty<ChunkRecord>();
        }

        var inputs = chunks.Where(IsEmbedded).Select(chunk => BuildEmbeddingInput(relPath, kind, chunk)).ToList();
        var embeddings = inputs.Count == 0 ? null : await EmbedAsync(inputs, ct).ConfigureAwait(false);

        var records = new List<ChunkRecord>(chunks.Count);
        int embedded = 0;
        for (int i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            records.Add(new ChunkRecord
            {
                Id = $"{project.Name}:{relPath}:{chunk.StartLine}:{i}",
                Project = project.Name,
                Path = relPath,
                Kind = kind,
                Symbol = chunk.Symbol,
                StartLine = chunk.StartLine,
                EndLine = chunk.EndLine,
                Content = chunk.Text,
                Embedding = embeddings is null || !IsEmbedded(chunk) ? ReadOnlyMemory<float>.Empty : embeddings[embedded++].Vector,
            });
        }

        return records;
    }

    private async Task<GeneratedEmbeddings<Embedding<float>>?> EmbedAsync(List<string> inputs, CancellationToken ct)
    {
        if (_withoutEmbeddings || _foreignDimensions != 0)
        {
            return null;
        }

        try
        {
            var embeddings = await embedder.GenerateAsync(inputs, options: null, ct).ConfigureAwait(false);
            if (embeddings.FirstOrDefault(e => e.Vector.Length != ChunkRecord.EmbeddingDimensions) is { } foreign)
            {
                _foreignDimensions = foreign.Vector.Length;
                return null;
            }

            return embeddings;
        }
        catch (NotSupportedException)
        {
            _withoutEmbeddings = true;
            return null;
        }
    }

    private IEnumerable<string> EnumerateFiles(string repoPath)
    {
        var folders = new Queue<string>([repoPath]);
        while (folders.TryDequeue(out var folder))
        {
            string[] files;
            string[] subfolders;
            try
            {
                files = Directory.GetFiles(folder);
                subfolders = Directory.GetDirectories(folder);
            }
            catch (Exception ex) when (folder != repoPath && ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(
                    ex,
                    "Skipping directory that could not be read: {Path}",
                    ProjectFileFilter.ToRelativePath(repoPath, folder));
                continue;
            }

            foreach (var file in files.Where(path => ProjectFileFilter.IsIndexable(ProjectFileFilter.ToRelativePath(repoPath, path))))
            {
                yield return file;
            }

            foreach (var subfolder in subfolders)
            {
                folders.Enqueue(subfolder);
            }
        }
    }

    private static bool IsEmbedded(Chunk chunk) => chunk.Kind != FileChunker.FieldKind;

    private static string BuildEmbeddingInput(string relPath, string kind, Chunk chunk)
    {
        var symbol = string.IsNullOrEmpty(chunk.Symbol) ? kind : $"{kind}:{chunk.Symbol}";
        var input = $"File: {relPath} | {symbol} | lines {chunk.StartLine}-{chunk.EndLine}\n{chunk.Text}";
        if (input.Length <= MaxEmbeddingInputCharacters)
        {
            return input;
        }

        var length = char.IsHighSurrogate(input[MaxEmbeddingInputCharacters - 1])
            ? MaxEmbeddingInputCharacters - 1
            : MaxEmbeddingInputCharacters;
        return input[..length];
    }
}
