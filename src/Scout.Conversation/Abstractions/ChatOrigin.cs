namespace Scout.Conversation.Abstractions;

/// <summary>
/// The <see cref="ChatMessage.OriginReference"/> of the turn running on the current async flow, set by the
/// conversation conductor for the duration of one turn, so a function the model calls during that turn can
/// read where the request came from. Null outside a turn and for a message without a reference.
/// </summary>
public static class ChatOrigin
{
    private static readonly AsyncLocal<string?> Slot = new();

    /// <summary>The originating reference of the current turn, or null.</summary>
    public static string? Current => Slot.Value;

    internal static Scope Enter(string? originReference)
    {
        Slot.Value = originReference;
        return default;
    }

    internal readonly struct Scope : IDisposable
    {
        public void Dispose() => Slot.Value = null;
    }
}
