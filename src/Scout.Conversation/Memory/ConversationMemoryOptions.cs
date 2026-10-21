namespace Scout.Conversation.Memory;

/// <summary>
/// Settings for the in-memory per-conversation history. Bound from
/// <see cref="ConfigSection"/>. When <see cref="Enabled"/> is false the router
/// neither loads nor writes history, leaving the pipeline behavior-neutral.
/// </summary>
public sealed class ConversationMemoryOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string ConfigSection = "Scout:Conversation:Memory";

    /// <summary>
    /// Whether per-conversation history is replayed and written back. Default true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum number of user/assistant turn-pairs retained per conversation.
    /// Oldest pairs are evicted first once exceeded. Default 6.
    /// </summary>
    public int MaxTurns { get; set; } = 6;

    /// <summary>
    /// Maximum total characters replayed into a request. The newest turns are
    /// kept; older ones are dropped first until the budget fits. Default 8000.
    /// </summary>
    public int MaxReplayedChars { get; set; } = 8000;

    /// <summary>
    /// Idle time after which a conversation's history is considered stale and
    /// dropped on next access. Default 30 minutes.
    /// </summary>
    public TimeSpan IdleTtl { get; set; } = TimeSpan.FromMinutes(30);
}
