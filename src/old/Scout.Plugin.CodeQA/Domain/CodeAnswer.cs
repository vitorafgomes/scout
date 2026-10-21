namespace Scout.Plugin.CodeQA.Domain;

/// <summary>
/// Final output of the CodeQA pipeline. The answer text is the LLM's
/// response grounded in retrieved <see cref="CodeChunk"/> values; the
/// citations are the file/line pointers the chat adapter renders.
/// </summary>
public sealed record CodeAnswer
{
    public CodeAnswer(
        string Text,
        IReadOnlyList<Citation> Citations,
        string ModelUsed,
        TimeSpan Latency)
    {
        if (string.IsNullOrWhiteSpace(Text))
        {
            throw new ArgumentException("Text must be non-empty.", nameof(Text));
        }
        ArgumentNullException.ThrowIfNull(Citations);
        if (string.IsNullOrWhiteSpace(ModelUsed))
        {
            throw new ArgumentException("ModelUsed must be non-empty.", nameof(ModelUsed));
        }
        if (Latency < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Latency), "Latency must be non-negative.");
        }

        this.Text = Text;
        this.Citations = Citations;
        this.ModelUsed = ModelUsed;
        this.Latency = Latency;
    }

    public string Text { get; }
    public IReadOnlyList<Citation> Citations { get; }
    public string ModelUsed { get; }
    public TimeSpan Latency { get; }
}
