using Microsoft.Extensions.Logging;
using Scout.Conversation.Abstractions;

namespace Scout.Adapter.Local;

/// <summary>
/// Posts a later message to the desktop's local conversation as an unsolicited <c>notice</c> event, a
/// <c>SCOUT_LOCAL</c> line with a null id, since the turn that asked has ended. The only payload is
/// <c>session</c>.
/// </summary>
public sealed class LocalOriginPoster(LocalChatAdapter adapter, ILogger<LocalOriginPoster> logger) : IOriginPoster
{
    internal const string Reference = "local:session";
    internal const string NoticeEvent = "notice";
    internal const int MaxTextLength = 32_000;

    /// <inheritdoc />
    public string Kind => "local";

    /// <inheritdoc />
    public Task<bool> PostAsync(string payload, string text, CancellationToken ct)
    {
        if (!string.Equals(payload, "session", StringComparison.Ordinal))
        {
            logger.LogWarning("A local origin reference could not be read; nothing was posted.");
            return Task.FromResult(false);
        }
        if (adapter.RunningWriter is not { } writer)
        {
            logger.LogWarning("The local adapter is not running; nothing was posted to the desktop.");
            return Task.FromResult(false);
        }

        try
        {
            writer.Write(null, NoticeEvent, text.Length > MaxTextLength ? text[..(MaxTextLength - 3)] + "..." : text);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A notice could not be written to the desktop.");
            return Task.FromResult(false);
        }
    }
}
