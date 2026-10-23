namespace Scout.Conversation.Access;

/// <summary>
/// Who may use Scout from chat. Bound from <c>Scout:Access</c>. Ids are the
/// platform user ids the adapters put on <c>ChatMessage.UserId</c>, compared
/// ordinally after trimming.
/// </summary>
public sealed class AccessOptions
{
    public const string ConfigSection = "Scout:Access";

    /// <summary>Platform user ids allowed to use Scout. Empty means no list.</summary>
    public string[] AllowedUserIds { get; set; } = [];

    /// <summary>
    /// When true, an empty <see cref="AllowedUserIds"/> refuses everyone instead of
    /// letting everyone in. The desktop app sets it once an owner credential is connected.
    /// </summary>
    public bool RequireAllowList { get; set; }
}
