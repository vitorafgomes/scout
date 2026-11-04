using System.Collections.Concurrent;

namespace Scout.Conversation.Abstractions;

/// <summary>
/// The <see cref="ChatMessage.OriginReference"/> of the turn running on the current async flow, set by the
/// conversation conductor for the duration of one turn, so a function the model calls during that turn can
/// read where the request came from and whether Scout's owner asked, and remember a mark for later turns of the
/// same conversation, keyed as the conversation memory keys it. Null, not the owner and with no marks outside a turn.
/// </summary>
public static class ChatOrigin
{
    internal const int KeptCap = 256;

    private static readonly AsyncLocal<Turn?> Slot = new();
    private static readonly Dictionary<(string ConversationKey, string Mark), DateTimeOffset> Kept = [];
    private static readonly Lock KeptGate = new();

    /// <summary>The originating reference of the current turn, or null.</summary>
    public static string? Current => Slot.Value?.OriginReference;

    /// <summary>
    /// True when the current turn's sender is Scout's owner: the local quick-ask user, or an allowed chat user listed in
    /// <c>Scout:Access:OwnerUserIds</c>. Set from the platform event by the conductor, never from a function parameter;
    /// false outside a turn.
    /// </summary>
    public static bool IsOwner => Slot.Value?.IsOwner ?? false;

    internal static int KeptCount
    {
        get
        {
            lock (KeptGate)
            {
                return Kept.Count;
            }
        }
    }

    /// <summary>
    /// Remember <paramref name="mark"/> for the rest of the current turn and, when the turn has a conversation key, for
    /// <paramref name="lifetime"/> in every later turn of the same conversation; outside a turn nothing is remembered.
    /// At most 256 marks outlive their turn; the one closest to expiring gives way to a new one.
    /// </summary>
    public static void Remember(string mark, TimeSpan lifetime)
    {
        if (Slot.Value is not { } turn)
        {
            return;
        }

        turn.Marks.TryAdd(mark, 0);
        if (turn.ConversationKey is not { } key)
        {
            return;
        }

        var now = turn.Time.GetUtcNow();
        lock (KeptGate)
        {
            foreach (var expired in Kept.Where(k => k.Value <= now).Select(k => k.Key).ToList())
            {
                Kept.Remove(expired);
            }

            if (!Kept.ContainsKey((key, mark)) && Kept.Count >= KeptCap)
            {
                Kept.Remove(Kept.MinBy(k => k.Value).Key);
            }

            Kept[(key, mark)] = now + lifetime;
        }
    }

    /// <summary>
    /// True when a function of the current turn remembered <paramref name="mark"/>, or a turn of the same conversation
    /// remembered it within its lifetime; false outside a turn.
    /// </summary>
    public static bool Remembers(string mark)
    {
        if (Slot.Value is not { } turn)
        {
            return false;
        }

        if (turn.Marks.ContainsKey(mark))
        {
            return true;
        }

        if (turn.ConversationKey is not { } key)
        {
            return false;
        }

        lock (KeptGate)
        {
            return Kept.TryGetValue((key, mark), out var until) && until > turn.Time.GetUtcNow();
        }
    }

    internal static Scope Enter(string? originReference, bool isOwner, TimeProvider time, string? conversationKey)
    {
        Slot.Value = new Turn(originReference, isOwner, time, conversationKey);
        return default;
    }

    private sealed record Turn(string? OriginReference, bool IsOwner, TimeProvider Time, string? ConversationKey)
    {
        public ConcurrentDictionary<string, byte> Marks { get; } = new(StringComparer.Ordinal);
    }

    internal readonly struct Scope : IDisposable
    {
        public void Dispose() => Slot.Value = null;
    }
}
