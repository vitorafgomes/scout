namespace Scout.Core.Chat;

/// <summary>
/// Adapter-agnostic citation that backs a <see cref="ChatResponse"/>. The
/// label is the only required field so any source — code chunk, doc page,
/// external URL — fits the same shape. Optional fields let richer adapters
/// (Slack, Teams) render citations as buttons or links without forcing the
/// simpler ones (Discord today) to know about them.
/// </summary>
public sealed record Citation
{
    public Citation(string Label)
    {
        if (string.IsNullOrWhiteSpace(Label))
        {
            throw new ArgumentException("Label must be non-empty.", nameof(Label));
        }

        this.Label = Label;
    }

    public string Label { get; }

    public string? Path { get; init; }

    public int? Line { get; init; }

    public string? Url { get; init; }

    public string? Snippet { get; init; }

    /// <summary>
    /// Adapter-agnostic short form. Used by adapters that render citations
    /// as plain text (Discord's "Sources:" footer today).
    /// </summary>
    public override string ToString()
    {
        if (!string.IsNullOrWhiteSpace(Path))
        {
            return Line is { } line ? $"{Path}:{line}" : Path;
        }
        return Label;
    }
}
