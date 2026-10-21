namespace Scout.Plugin.CodeQA.Domain;

/// <summary>
/// One indexable piece of source code. Carries the text, the file and line
/// range it came from, and — after embedding — a normalized vector used for
/// cosine-similarity retrieval.
///
/// The embedding is stored as <see cref="ReadOnlyMemory{Single}"/> so
/// consumers get an immutable view, and so the vector store can hold its
/// own copy without risk of aliasing.
/// </summary>
public sealed record CodeChunk
{
    public CodeChunk(
        string Id,
        string FilePath,
        int StartLine,
        int EndLine,
        string Content,
        ReadOnlyMemory<float> Embedding)
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new ArgumentException("Id must be non-empty.", nameof(Id));
        }
        if (string.IsNullOrWhiteSpace(FilePath))
        {
            throw new ArgumentException("FilePath must be non-empty.", nameof(FilePath));
        }
        if (StartLine < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(StartLine), "StartLine must be >= 1.");
        }
        if (EndLine < StartLine)
        {
            throw new ArgumentOutOfRangeException(nameof(EndLine), "EndLine must be >= StartLine.");
        }
        if (string.IsNullOrEmpty(Content))
        {
            throw new ArgumentException("Content must be non-empty.", nameof(Content));
        }

        this.Id = Id;
        this.FilePath = FilePath;
        this.StartLine = StartLine;
        this.EndLine = EndLine;
        this.Content = Content;
        this.Embedding = Embedding;
    }

    public string Id { get; }
    public string FilePath { get; }
    public int StartLine { get; }
    public int EndLine { get; }
    public string Content { get; }
    public ReadOnlyMemory<float> Embedding { get; }
}
