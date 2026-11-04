namespace Scout.Conversation.Abstractions;

/// <summary>
/// Reads the text on a screenshot on this machine. Registered only when a text reader is installed; the local adapter
/// answers a read with a sentence saying so when none is.
/// </summary>
public interface IScreenTextReader
{
    /// <summary>
    /// The text on <paramref name="jpeg"/>, never empty; throws when it cannot be read, so a failure is never an empty answer.
    /// </summary>
    Task<string> ReadAsync(ReadOnlyMemory<byte> jpeg, CancellationToken ct);
}
