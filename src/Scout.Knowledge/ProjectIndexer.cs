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
    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".json", ".yml", ".yaml",
    };

    private static readonly HashSet<string> DocExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt",
    };

    private static readonly HashSet<string> SkipSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", "node_modules", "dist", ".vs",
    };

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

            var extension = Path.GetExtension(path);
            var kind = DocExtensions.Contains(extension) ? "doc" : "code";
            var relPath = ToRelativePath(project.RepoPath, path);

            try
            {
                var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                var chunks = chunker.Chunk(relPath, text);
                if (chunks.Count == 0)
                {
                    continue;
                }

                var inputs = chunks.Select(chunk => BuildEmbeddingInput(relPath, kind, chunk)).ToList();
                var embeddings = await embedder.GenerateAsync(inputs, options: null, ct).ConfigureAwait(false);

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

                filesIndexed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Skipping file that failed to index: {Path}", relPath);
            }
        }

        await store.ReplaceProjectAsync(project.Name, records, ct).ConfigureAwait(false);

        logger.LogInformation(
            "Indexed project {Project}: {Files} files, {Chunks} chunks.",
            project.Name,
            filesIndexed,
            records.Count);
    }

    private static IEnumerable<string> EnumerateFiles(string repoPath)
    {
        return Directory
            .EnumerateFiles(repoPath, "*", SearchOption.AllDirectories)
            .Where(IsIncluded);

        bool IsIncluded(string path)
        {
            var extension = Path.GetExtension(path);
            if (!CodeExtensions.Contains(extension) && !DocExtensions.Contains(extension))
            {
                return false;
            }

            var relative = Path.GetRelativePath(repoPath, path);
            foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (SkipSegments.Contains(segment))
                {
                    return false;
                }
            }

            return true;
        }
    }

    private static string ToRelativePath(string repoPath, string path)
        => Path.GetRelativePath(repoPath, path).Replace('\\', '/');

    private static string BuildEmbeddingInput(string relPath, string kind, Chunk chunk)
    {
        var symbol = string.IsNullOrEmpty(chunk.Symbol) ? kind : $"{kind}:{chunk.Symbol}";
        return $"File: {relPath} | {symbol} | lines {chunk.StartLine}-{chunk.EndLine}\n{chunk.Text}";
    }
}
