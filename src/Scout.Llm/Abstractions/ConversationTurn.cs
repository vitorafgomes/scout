namespace Scout.Llm.Abstractions;

/// <summary>
/// Author of a replayed <see cref="ConversationTurn"/>.
/// </summary>
public enum ConversationRole
{
    /// <summary>A turn authored by the end user.</summary>
    User,

    /// <summary>A turn authored by the assistant.</summary>
    Assistant,
}

/// <summary>
/// A single prior turn replayed into a chat request to restore conversation
/// context. Turns are provided oldest-first and replayed before the current
/// <see cref="ChatRequest.UserMessage"/>.
/// </summary>
/// <param name="Role">Whether the user or the assistant authored the turn.</param>
/// <param name="Text">The verbatim turn content.</param>
public sealed record ConversationTurn(ConversationRole Role, string Text);
