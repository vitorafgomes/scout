namespace Scout.Contracts;

/// <summary>
/// Ambient, per-request caller identity for the current conversation turn. It is
/// derived from the inbound chat message at the adapter/conductor boundary and
/// flowed to code that runs during the response (including Semantic Kernel plugin
/// invocations) via <see cref="IScoutRequestContext"/>.
///
/// Security note: this identity gates OUTPUT DISCLOSURE only. It never authorizes
/// a write and must never be treated as a capability. Consumers fail closed:
/// when it is absent they must behave as if the least-privileged caller is asking.
/// </summary>
/// <param name="UserId">Platform user id of the caller (opaque; never rendered to other users).</param>
/// <param name="ChannelId">Platform channel/conversation id the message arrived on.</param>
/// <param name="IsDirectMessage">True when the message arrived over a 1:1 direct message rather than a shared channel/guild/group.</param>
/// <param name="AdapterName">Name of the chat adapter that produced the message (e.g. "Discord", "Slack", "Teams"); null when unknown.</param>
public sealed record ScoutRequestIdentity(
    string UserId,
    string ChannelId,
    bool IsDirectMessage,
    string? AdapterName);
