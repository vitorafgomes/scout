namespace Scout.Adapter.Slack.Streaming;

/// <summary>
/// Identity of the single Slack message a <see cref="SlackResponseSink"/> owns:
/// the channel it was posted to and its timestamp (<c>ts</c>), which together
/// address the message for <c>chat.update</c>. Captured once from the Begin
/// post and reused for every later edit, so the sink can only ever write to the
/// message it created.
/// </summary>
public sealed record SlackMessageHandle(string ChannelId, string Ts);
