using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Scout.Llm.Abstractions;

namespace Scout.Conversation.Memory;

/// <summary>
/// In-process, RAM-only history store keyed by conversation. This is NOT a
/// durable write boundary: nothing leaves the process, nothing is persisted,
/// and the read-only invariant is preserved. History is lost on restart by
/// design.
/// </summary>
/// <remarks>
/// Backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/> of per-conversation
/// entries. Each entry carries its own lock object, so concurrent
/// <see cref="Load"/>/<see cref="Append"/> for the same key serialize while
/// different keys never contend. TTL eviction is lazy: a stale entry is dropped
/// on the next access rather than on a timer.
/// </remarks>
public sealed class InMemoryConversationMemoryStore : IConversationMemoryStore
{
    private readonly ConcurrentDictionary<string, ConversationEntry> _entries = new(StringComparer.Ordinal);
    private readonly ConversationMemoryOptions _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryConversationMemoryStore"/> class.
    /// </summary>
    /// <param name="options">The memory options.</param>
    /// <param name="timeProvider">The clock used for last-touch and TTL.</param>
    public InMemoryConversationMemoryStore(
        IOptions<ConversationMemoryOptions> options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new ArgumentException(
            "ConversationMemoryOptions must be configured.", nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public IReadOnlyList<ConversationTurn> Load(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (!_entries.TryGetValue(key, out var entry))
        {
            return Array.Empty<ConversationTurn>();
        }

        lock (entry.Gate)
        {
            if (IsExpired(entry))
            {
                _entries.TryRemove(key, out _);
                return Array.Empty<ConversationTurn>();
            }

            return Snapshot(entry);
        }
    }

    /// <inheritdoc />
    public void Append(string key, ConversationTurn user, ConversationTurn assistant)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(assistant);

        var entry = _entries.GetOrAdd(key, static _ => new ConversationEntry());

        lock (entry.Gate)
        {
            if (IsExpired(entry))
            {
                entry.Turns.Clear();
            }

            entry.Turns.Add(user);
            entry.Turns.Add(assistant);

            // FIFO-evict whole pairs past the turn budget (two entries per pair).
            var maxEntries = Math.Max(0, _options.MaxTurns) * 2;
            while (entry.Turns.Count > maxEntries)
            {
                entry.Turns.RemoveRange(0, 2);
            }

            entry.LastTouchUtc = _timeProvider.GetUtcNow();
        }
    }

    private IReadOnlyList<ConversationTurn> Snapshot(ConversationEntry entry)
    {
        var maxEntries = Math.Max(0, _options.MaxTurns) * 2;
        var startIndex = Math.Max(0, entry.Turns.Count - maxEntries);

        // Apply the character budget newest-first, then return oldest-first.
        var budget = Math.Max(0, _options.MaxReplayedChars);
        var used = 0;
        var firstKept = entry.Turns.Count;
        for (var i = entry.Turns.Count - 1; i >= startIndex; i--)
        {
            var length = entry.Turns[i].Text?.Length ?? 0;
            if (used + length > budget && i != entry.Turns.Count - 1)
            {
                break;
            }

            used += length;
            firstKept = i;
        }

        var count = entry.Turns.Count - firstKept;
        if (count <= 0)
        {
            return Array.Empty<ConversationTurn>();
        }

        var result = new ConversationTurn[count];
        entry.Turns.CopyTo(firstKept, result, 0, count);
        return result;
    }

    private bool IsExpired(ConversationEntry entry) =>
        _timeProvider.GetUtcNow() - entry.LastTouchUtc >= _options.IdleTtl;

    private sealed class ConversationEntry
    {
        public object Gate { get; } = new();

        public List<ConversationTurn> Turns { get; } = [];

        public DateTimeOffset LastTouchUtc { get; set; }
    }
}
