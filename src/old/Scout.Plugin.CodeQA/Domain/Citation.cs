namespace Scout.Plugin.CodeQA.Domain;

/// <summary>
/// Anchor back to source code that supports a <see cref="CodeAnswer"/>. A
/// citation is always grounded in a file path and line range so chat output
/// can render a clickable / copyable reference.
/// </summary>
public sealed record Citation
{
    public Citation(string FilePath, int StartLine, int EndLine, double RelevanceScore)
    {
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
        if (RelevanceScore is < -1 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RelevanceScore), "RelevanceScore must be a cosine similarity in [-1, 1].");
        }

        this.FilePath = FilePath;
        this.StartLine = StartLine;
        this.EndLine = EndLine;
        this.RelevanceScore = RelevanceScore;
    }

    public string FilePath { get; }
    public int StartLine { get; }
    public int EndLine { get; }
    public double RelevanceScore { get; }

    public override string ToString() =>
        $"{FilePath}:{StartLine}-{EndLine}";
}
