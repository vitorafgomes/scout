using Microsoft.Extensions.Logging;

namespace Scout.Adapter.Discord.Streaming;

/// <summary>
/// Configuration for <see cref="EmbedEditThrottler"/>. The <see cref="EditAsync"/>
/// delegate is the only seam to the transport; tests inject a lambda and never
/// touch Discord.Net.
/// </summary>
public sealed record EmbedEditThrottlerOptions
{
    /// <summary>
    /// Delegate that performs the actual remote edit. Receives the final
    /// string to display in the embed description.
    /// </summary>
    public required Func<string, CancellationToken, Task> EditAsync { get; init; }

    /// <summary>Minimum interval between edits.</summary>
    public TimeSpan EditInterval { get; init; } = TimeSpan.FromMilliseconds(750);

    /// <summary>Max embed description length (4096 for Discord).</summary>
    public int MaxLength { get; init; } = 4096;

    /// <summary>Cursor character appended during streaming.</summary>
    public string Cursor { get; init; } = "▌";

    /// <summary>Shown on finalize when the buffer is empty.</summary>
    public string EmptyStreamPlaceholder { get; init; } = "(no response from model)";

    /// <summary>Provided for deterministic testing.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Logger for retries and rate-limit backoff.</summary>
    public ILogger? Logger { get; init; }
}
