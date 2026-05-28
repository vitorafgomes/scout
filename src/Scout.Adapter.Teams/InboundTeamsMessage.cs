namespace Scout.Adapter.Teams;

/// <summary>
/// Minimal projection of a Teams inbound <c>Activity</c> that the translator
/// needs. Exists as a separate record so the translator stays a pure class:
/// Bot Framework's <c>Activity</c> is awkward to hand-build in unit tests, while
/// this record is trivially constructable. The live adapter maps the inbound
/// <c>Activity</c> into this record before calling the translator; tests build
/// it directly.
/// </summary>
public sealed record InboundTeamsMessage(
    string ActivityId,
    string ActivityType,
    string FromId,
    string ConversationId,
    string Text,
    bool IsGroup,
    string? RecipientId = null,
    string? RecipientMention = null,
    DateTimeOffset? Timestamp = null);
