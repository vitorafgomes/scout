using Scout.Plugin.CodeQA.Domain;

namespace Scout.Plugin.CodeQA.Chunking;

/// <summary>
/// Splits a source file into retrieval-sized <see cref="CodeChunk"/> units.
/// Implementations are pure and deterministic: same input yields byte-identical
/// output.
/// </summary>
public interface IChunker
{
    /// <summary>
    /// Produces chunks for a single file. The returned list is never null; an
    /// empty <paramref name="content"/> yields an empty list. Line numbers in
    /// the result are 1-based, inclusive on both ends.
    /// </summary>
    IReadOnlyList<CodeChunk> Chunk(string content, string filePath, CancellationToken cancellationToken);
}
