namespace Scout.Adapter.Discord.Streaming;

/// <summary>
/// Inspection-only snapshot of throttler state. Intended for tests and
/// diagnostic logging.
/// </summary>
public sealed record EmbedEditThrottlerSnapshot(
    int BufferLength,
    int EditCount,
    DateTimeOffset? LastEditAt);
