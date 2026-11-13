namespace Scout.Llm.Abstractions;

/// <summary>
/// Blocks a function the model calls during a turn hands to Scout for the end of the reply, so a structured result
/// (the review's findings as a <c>scout-findings</c> block) reaches the chat however the model words its answer. The
/// conversation conductor begins it for the turn on the current async flow and the stream pump takes the blocks when
/// the turn completes; outside a turn an attached block is dropped.
/// </summary>
public static class TurnAttachments
{
    /// <summary>Blocks one turn keeps; a block past this is dropped.</summary>
    public const int MaxBlocks = 4;

    /// <summary>Characters of one block; a longer block is dropped.</summary>
    public const int MaxBlockLength = 64_000;

    private static readonly AsyncLocal<List<string>?> Slot = new();

    /// <summary>
    /// Keep the blocks attached on the current async flow for one turn until the scope is disposed, which restores
    /// whatever was kept before, so a turn started from inside another leaves the outer one intact.
    /// </summary>
    public static Scope Begin()
    {
        var previous = Slot.Value;
        Slot.Value = [];
        return new Scope(previous);
    }

    /// <summary>
    /// Add <paramref name="block"/> to the current turn's reply. A block whose first line equals an attached block's
    /// first line replaces it in place, so a function called twice in a turn leaves only its latest block. Nothing
    /// happens outside a turn, for an empty block, for a block over <see cref="MaxBlockLength"/> characters, or for a
    /// new block past <see cref="MaxBlocks"/>.
    /// </summary>
    public static void Attach(string block)
    {
        if (Slot.Value is not { } blocks || string.IsNullOrEmpty(block) || block.Length > MaxBlockLength)
        {
            return;
        }

        var head = FirstLine(block);
        lock (blocks)
        {
            var same = blocks.FindIndex(b => string.Equals(FirstLine(b), head, StringComparison.Ordinal));
            if (same >= 0)
            {
                blocks[same] = block;
            }
            else if (blocks.Count < MaxBlocks)
            {
                blocks.Add(block);
            }
        }
    }

    /// <summary>Whether the current turn holds a block not yet taken; false outside a turn. Nothing is cleared.</summary>
    public static bool Pending
    {
        get
        {
            if (Slot.Value is not { } blocks)
            {
                return false;
            }

            lock (blocks)
            {
                return blocks.Count > 0;
            }
        }
    }

    /// <summary>The current turn's blocks in the order they were first attached, cleared; empty outside a turn.</summary>
    public static IReadOnlyList<string> Take()
    {
        if (Slot.Value is not { } blocks)
        {
            return [];
        }

        lock (blocks)
        {
            var taken = blocks.ToArray();
            blocks.Clear();
            return taken;
        }
    }

    private static string FirstLine(string block)
    {
        var end = block.IndexOf('\n', StringComparison.Ordinal);
        return end < 0 ? block : block[..end];
    }

    /// <summary>Restores the previous turn's blocks on dispose.</summary>
    public readonly struct Scope(List<string>? previous) : IDisposable
    {
        /// <inheritdoc />
        public void Dispose() => Slot.Value = previous;
    }
}
