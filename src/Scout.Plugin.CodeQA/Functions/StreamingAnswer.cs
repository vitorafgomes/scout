using Scout.Core.Chat;

namespace Scout.Plugin.CodeQA.Functions;

/// <summary>
/// Carrier for the streaming CodeQA path. Pairs the (already resolved)
/// adapter-agnostic citations with the live token sequence that the chat
/// adapter pulls. Citations are computed up front so the router can render
/// the sources footer once the stream finishes — and so they survive a
/// truncated / cancelled stream.
/// </summary>
public sealed record StreamingAnswer(
    IReadOnlyList<Citation> Citations,
    IAsyncEnumerable<string> Tokens);
