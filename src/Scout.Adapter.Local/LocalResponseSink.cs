using Scout.Conversation.Abstractions;

namespace Scout.Adapter.Local;

/// <summary>
/// Streams one reply to the desktop's quick-ask window as <c>begin</c>,
/// <c>progress</c> (what Scout is doing before the text arrives), <c>update</c>
/// (the full text so far), then <c>complete</c> or <c>fail</c> events tagged
/// with the request id.
/// </summary>
internal sealed class LocalResponseSink(string requestId, LocalEventWriter writer) : IResponseSink
{
    internal const string FailedFooter = "(Scout could not finish this answer; see Scout logs)";
    internal const string FailedReply = "Scout could not answer that; see Scout logs.";

    private int _terminal;
    private string? _lastUpdate;

    public RenderCapabilities Capabilities { get; } = new()
    {
        FineGrainedEdit = true,
        MaxBodyLength = 32_000,
    };

    public bool IsTerminal => Volatile.Read(ref _terminal) == 1;

    /// <summary>Ends the reply with <paramref name="text"/> unless it has already ended.</summary>
    public void TryFail(string text)
    {
        if (Interlocked.Exchange(ref _terminal, 1) == 0)
        {
            writer.Write(requestId, "fail", text);
        }
    }

    public Task BeginAsync(CancellationToken cancellationToken)
    {
        writer.Write(requestId, "begin");
        return Task.CompletedTask;
    }

    public Task UpdateAsync(string cumulativeText, CancellationToken cancellationToken)
    {
        if (IsTerminal)
        {
            throw new InvalidOperationException("The reply is already finished.");
        }
        writer.Write(requestId, "update", cumulativeText);
        _lastUpdate = cumulativeText;
        return Task.CompletedTask;
    }

    public Task ProgressAsync(string activity, CancellationToken cancellationToken)
    {
        if (!IsTerminal && !string.IsNullOrWhiteSpace(activity))
        {
            writer.Write(requestId, "progress", activity);
        }

        return Task.CompletedTask;
    }

    public Task CompleteAsync(string finalText, IReadOnlyList<Citation> citations, CancellationToken cancellationToken)
    {
        EnterTerminal();
        writer.Write(requestId, "complete", finalText, citations);
        return Task.CompletedTask;
    }

    public Task FailAsync(string finalTextSoFar, string reason, CancellationToken cancellationToken)
    {
        EnterTerminal();
        if (_lastUpdate is not null && !_lastUpdate.StartsWith(finalTextSoFar, StringComparison.Ordinal))
        {
            writer.Write(requestId, "update", finalTextSoFar);
        }
        var text = _lastUpdate is not null ? FailedFooter
            : finalTextSoFar.Length == 0 ? FailedReply
            : finalTextSoFar + "\n\n" + FailedFooter;
        writer.Write(requestId, "fail", text);
        return Task.CompletedTask;
    }

    private void EnterTerminal()
    {
        if (Interlocked.Exchange(ref _terminal, 1) == 1)
        {
            throw new InvalidOperationException("The reply is already finished.");
        }
    }
}
