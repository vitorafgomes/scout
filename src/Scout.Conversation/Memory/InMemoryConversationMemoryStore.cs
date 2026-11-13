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
/// <see cref="Load"/>/<see cref="Append(string, ConversationTurn, ConversationTurn, CancellationToken)"/> for the same key serialize while
/// different keys never contend; an append that finds its entry removed while it
/// waited for the lock starts again on the entry now in the dictionary, so the
/// turn is never written to a detached entry, and an append for a turn already
/// cancelled stores nothing. TTL eviction is lazy: a stale entry
/// is dropped on the next access to it, and every <see cref="Append(string, ConversationTurn, ConversationTurn, CancellationToken)"/> sweeps the
/// other expired entries, so one-shot keys (a thread per question) do not pile up.
/// The character budget is applied to whole user/assistant pairs, newest first,
/// and the newest pair is kept whole even when it alone is over the budget.
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
                _entries.TryRemove(new KeyValuePair<string, ConversationEntry>(key, entry));
                return Array.Empty<ConversationTurn>();
            }

            return Snapshot(entry);
        }
    }

    /// <inheritdoc />
    public void Append(string key, ConversationTurn user, ConversationTurn assistant) =>
        Append(key, user, assistant, CancellationToken.None);

    /// <inheritdoc />
    public void Append(string key, ConversationTurn user, ConversationTurn assistant, CancellationToken turn)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(assistant);

        while (!TryAppend(key, user, assistant, turn))
        {
        }

        SweepExpired();
    }

    private bool TryAppend(string key, ConversationTurn user, ConversationTurn assistant, CancellationToken turn)
    {
        var now = _timeProvider.GetUtcNow();
        var entry = _entries.GetOrAdd(key, static (_, touched) => new ConversationEntry { LastTouchUtc = touched }, now);

        lock (entry.Gate)
        {
            if (turn.IsCancellationRequested)
            {
                return true;
            }

            if (!_entries.TryGetValue(key, out var current) || !ReferenceEquals(current, entry))
            {
                return false;
            }

            if (IsExpired(entry))
            {
                entry.Turns.Clear();
            }

            entry.Turns.Add(user);
            entry.Turns.Add(assistant);

            var maxEntries = Math.Max(0, _options.MaxTurns) * 2;
            while (entry.Turns.Count > maxEntries)
            {
                entry.Turns.RemoveRange(0, 2);
            }

            entry.LastTouchUtc = _timeProvider.GetUtcNow();
            return true;
        }
    }

    /// <inheritdoc />
    public void Clear(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _entries.TryRemove(key, out _);
    }

    internal int Count => _entries.Count;

    private void SweepExpired()
    {
        foreach (var pair in _entries)
        {
            lock (pair.Value.Gate)
            {
                if (IsExpired(pair.Value))
                {
                    _entries.TryRemove(pair);
                }
            }
        }
    }

    private ConversationTurn[] Snapshot(ConversationEntry entry)
    {
        // Defensive re-bound: Append already FIFO-evicts to exactly MaxTurns*2.
        var maxEntries = Math.Max(0, _options.MaxTurns) * 2;
        var startIndex = Math.Max(0, entry.Turns.Count - maxEntries);

        var budget = Math.Max(0, _options.MaxReplayedChars);
        var used = 0;
        var firstKept = entry.Turns.Count;
        for (var i = entry.Turns.Count - 2; i >= startIndex; i -= 2)
        {
            var length = (entry.Turns[i].Text?.Length ?? 0) + (entry.Turns[i + 1].Text?.Length ?? 0);
            if (used + length > budget && i != entry.Turns.Count - 2)
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
