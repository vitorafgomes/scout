namespace Scout.Plugin.SelfInfo;

/// <summary>
/// Configuration for the SelfInfo plugin, bound from "Scout:Plugins:SelfInfo".
/// The plugin self-gates on <see cref="Enabled"/> and, unlike the other plugins,
/// defaults to <c>false</c>: it exposes host diagnostics to anyone who can talk to
/// the bot, and Semantic Kernel functions carry no per-request identity, so there
/// is no admin/DM boundary to gate on. It stays off until a host operator opts in.
/// </summary>
public sealed class SelfInfoOptions
{
    /// <summary>
    /// Whether the plugin is active. Defaults to <c>false</c> (opt-in): with no
    /// explicit opt-in the registration is a clean no-op (no kernel plugin).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Chat user ids allowed to receive the elevated (medium-tier) disclosure.
    /// Empty (the default) means no caller is elevated, so every response stays at
    /// the public-safe tier regardless of who asks. Matched with an ordinal
    /// comparison. This gates OUTPUT DISCLOSURE only; it never authorizes a write.
    /// </summary>
    public IReadOnlyList<string> AdminUserIds { get; set; } = Array.Empty<string>();

    /// <summary>
    /// When <c>true</c>, an admin is elevated only over a direct message; the same
    /// admin asking in a shared channel/guild/group gets the public-safe tier. This
    /// is an AND-constraint on <see cref="AdminUserIds"/>, never an independent
    /// unlock. Defaults to <c>false</c> (admins are elevated in any conversation).
    /// </summary>
    public bool DmOnly { get; set; }
}
