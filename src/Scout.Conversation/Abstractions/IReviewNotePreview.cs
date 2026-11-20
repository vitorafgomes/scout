namespace Scout.Conversation.Abstractions;

/// <summary>
/// Composes the note and the comments a review card's verdict would post, by the same path as the send and with the same
/// reads, and sends nothing. Registered only where GitHub is; the local adapter answers a preview with a sentence saying
/// so when none is.
/// </summary>
public interface IReviewNotePreview
{
    /// <summary>
    /// The note <paramref name="requestJson"/> would post, as JSON: <c>{"body", "comments":[{"id","path","line","body"}],
    /// "dropped":[...]}</c>. Throws with a sentence for the owner when it cannot be composed.
    /// </summary>
    Task<string> PreviewAsync(string requestJson, CancellationToken ct);
}
