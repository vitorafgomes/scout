using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Scout.Knowledge;

/// <summary>Walks a project's local repo, chunks each file, embeds the chunks, and replaces the project's chunk set in the store.</summary>
public sealed class ProjectIndexer(
    IChunker chunker,
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    IProjectKnowledgeStore store,
    ILogger<ProjectIndexer> logger)
{
    /// <summary>Index a project's repository in place: enumerate, chunk, embed, and replace the project's chunks.</summary>
    /// <param name="project">The project to index; its <see cref="ProjectEntry.RepoPath"/> is read locally.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task IndexAsync(ProjectEntry project, CancellationToken ct)
    {
        var records = new List<ChunkRecord>();
        int filesIndexed = 0;

        foreach (var path in EnumerateFiles(project.RepoPath))
        {
            ct.ThrowIfCancellationRequested();

            var fileRecords = await BuildRecordsForFileAsync(project, path, ct).ConfigureAwait(false);
            if (fileRecords.Count == 0)
            {
                continue;
            }

            records.AddRange(fileRecords);
            filesIndexed++;
        }

        await store.ReplaceProjectAsync(project.Name, records, ct).ConfigureAwait(false);

        logger.LogInformation(
            "Indexed project {Project}: {Files} files, {Chunks} chunks.",
            project.Name,
            filesIndexed,
            records.Count);
    }

    /// <summary>
    /// Read, chunk, embed, and build the chunk records for a single file. Returns an empty
    /// list when the file yields no chunks or fails to read/embed (logged, never thrown).
    /// </summary>
    /// <param name="project">The owning project; supplies name and repo root.</param>
    /// <param name="absolutePath">Absolute path to the file on disk.</param>
    /// <param name="ct">Cancellation token.</param>
    internal async Task<IReadOnlyList<ChunkRecord>> BuildRecordsForFileAsync(
        ProjectEntry project,
        string absolutePath,
        CancellationToken ct)
    {
        var kind = ProjectFileFilter.KindFor(absolutePath);
        var relPath = ProjectFileFilter.ToRelativePath(project.RepoPath, absolutePath);

        try
        {
            var text = await File.ReadAllTextAsync(absolutePath, ct).ConfigureAwait(false);
            var chunks = chunker.Chunk(relPath, text);
            if (chunks.Count == 0)
            {
                return Array.Empty<ChunkRecord>();
            }

            var inputs = chunks.Select(chunk => BuildEmbeddingInput(relPath, kind, chunk)).ToList();
            var embeddings = await embedder.GenerateAsync(inputs, options: null, ct).ConfigureAwait(false);

            var records = new List<ChunkRecord>(chunks.Count);
            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                records.Add(new ChunkRecord
                {
                    Id = $"{project.Name}:{relPath}:{chunk.StartLine}",
                    Project = project.Name,
                    Path = relPath,
                    Kind = kind,
                    Symbol = chunk.Symbol,
                    StartLine = chunk.StartLine,
                    EndLine = chunk.EndLine,
                    Content = chunk.Text,
                    Embedding = embeddings[i].Vector,
                });
            }

            return records;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Skipping file that failed to index: {Path}", relPath);
            return Array.Empty<ChunkRecord>();
        }
    }

    private static IEnumerable<string> EnumerateFiles(string repoPath)
    {
        return Directory
            .EnumerateFiles(repoPath, "*", SearchOption.AllDirectories)
            .Where(path => ProjectFileFilter.IsIndexable(ProjectFileFilter.ToRelativePath(repoPath, path)));
    }

    private static string BuildEmbeddingInput(string relPath, string kind, Chunk chunk)
    {
        var symbol = string.IsNullOrEmpty(chunk.Symbol) ? kind : $"{kind}:{chunk.Symbol}";
        return $"File: {relPath} | {symbol} | lines {chunk.StartLine}-{chunk.EndLine}\n{chunk.Text}";
    }
}
