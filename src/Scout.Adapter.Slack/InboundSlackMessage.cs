namespace Scout.Adapter.Slack;

/// <summary>
/// Minimal projection of a Slack message event that the translator needs. Exists
/// as a separate record so the translator stays a pure class — SlackNet's
/// <c>MessageEvent</c> carries far more than the translator reads, and building
/// it in tests is awkward, while this record is trivially constructable.
///
/// The live adapter maps <c>MessageEvent</c> into this record before calling
/// into the translator; tests build it directly.
/// </summary>
public sealed record InboundSlackMessage(
    string UserId,
    string ChannelId,
    string Text,
    DateTimeOffset Timestamp,
    string MessageTs,
    string? ThreadTs = null,
    string? ChannelType = null,
    string? Subtype = null,
    string? BotId = null,
    string? AppId = null);
