using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scout.Conversation.Abstractions;

namespace Scout.Conversation;

/// <summary>
/// <see cref="IOriginNotifier"/> that dispatches a reference to the <see cref="IOriginPoster"/> registered for
/// its <c>&lt;adapter&gt;:</c> prefix. A reference no registered poster claims, Teams among them, returns false.
/// </summary>
public sealed class OriginNotifier(IEnumerable<IOriginPoster> posters, ILogger<OriginNotifier>? logger = null)
    : IOriginNotifier
{
    private readonly IOriginPoster[] _posters = [.. posters];
    private readonly ILogger _logger = logger ?? NullLogger<OriginNotifier>.Instance;

    /// <inheritdoc />
    public async Task<bool> PostAsync(string originReference, string text, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var colon = originReference?.IndexOf(':', StringComparison.Ordinal) ?? -1;
        var kind = colon > 0 ? originReference![..colon] : null;
        var poster = _posters.FirstOrDefault(p => string.Equals(p.Kind, kind, StringComparison.Ordinal));
        if (poster is null)
        {
            _logger.LogWarning("No running chat adapter can post to the originating conversation.");
            return false;
        }

        try
        {
            return await poster.PostAsync(originReference![(colon + 1)..], text, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Posting to the originating {Kind} conversation failed.", poster.Kind);
            return false;
        }
    }
}
