namespace Scout.Plugin.CodeReview.Abstractions;

/// <summary>
/// A single text file pulled from a repository for review. This is the fetch
/// layer's type; it is deliberately kept separate from the reviewing layer's
/// <c>ReviewFile</c> and hand-mapped in <c>AnalyzeRepositoryAsync</c> so the
/// fetch contract and the reviewing contract can evolve apart.
/// </summary>
/// <param name="Path">Repository-relative path, e.g. "src/Program.cs".</param>
/// <param name="Content">Decoded UTF-8 text content.</param>
public sealed record RepositoryFile(string Path, string Content);
