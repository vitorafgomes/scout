namespace Scout.Adapter.Discord;

/// <summary>
/// Minimal projection of a Discord message that the translator needs. Exists
/// as a separate record so the translator stays a pure class — Discord.Net's
/// <c>SocketMessage</c> is sealed and hard to construct in unit tests, while
/// this record is trivially constructable.
///
/// The live adapter maps <c>SocketMessage</c> into this record before calling
/// into the translator; tests build it directly.
/// </summary>
public sealed record InboundDiscordMessage(
    ulong MessageId,
    ulong AuthorId,
    ulong ChannelId,
    string Content,
    bool MentionsSelf,
    DateTimeOffset Timestamp);
