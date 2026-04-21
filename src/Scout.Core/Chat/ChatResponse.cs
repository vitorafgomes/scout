namespace Scout.Core.Chat;

/// <summary>
/// Outbound response produced by the Scout core and rendered by a chat adapter.
/// </summary>
public sealed record ChatResponse(string Text, IReadOnlyList<string>? Citations = null)
{
    public static ChatResponse Plain(string text) => new(text);
}
