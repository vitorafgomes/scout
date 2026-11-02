namespace Scout.Conversation.Access;

/// <summary>
/// Tells the desktop app which user paired with the configured code. The host
/// supplies the implementation; without one, pairing is refused.
/// </summary>
public interface IPairingSignal
{
    /// <summary>Report the platform user id that sent the valid pairing code.</summary>
    void Paired(string userId);

    /// <summary>Report that the pairing code can no longer be used in this process.</summary>
    void Burned();
}
