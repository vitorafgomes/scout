using Scout.Conversation.Abstractions;

namespace Scout.Conversation.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IResponseSink"/> that records lifecycle calls and
/// enforces the Begin -> Update* -> (Complete|Fail) contract, including
/// cumulative monotonicity. An optional per-update hook lets tests inject
/// transport failures (429, 5xx) at a chosen update index.
/// </summary>
internal sealed class RecordingResponseSink(
    RenderCapabilities capabilities,
    Action<int, string>? onUpdate = null) : IResponseSink
{
    private bool _begun;
    private bool _terminal;
    private int _updateIndex;

    public RenderCapabilities Capabilities { get; } = capabilities;

    public List<string> Updates { get; } = [];
    public bool Completed { get; private set; }
    public bool Failed { get; private set; }
    public string? FinalText { get; private set; }
    public string? FailReason { get; private set; }
    public IReadOnlyList<Citation> FinalCitations { get; private set; } = [];

    public int BeginCount { get; private set; }
    public int TerminalCount { get; private set; }

    public Task BeginAsync(CancellationToken cancellationToken)
    {
        if (_begun)
        {
            throw new InvalidOperationException("BeginAsync called more than once.");
        }
        _begun = true;
        BeginCount++;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(string cumulativeText, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cumulativeText);
        if (!_begun)
        {
            throw new InvalidOperationException("UpdateAsync before BeginAsync.");
        }
        if (_terminal)
        {
            throw new InvalidOperationException("UpdateAsync after terminal call.");
        }

        var index = _updateIndex++;
        onUpdate?.Invoke(index, cumulativeText);

        Updates.Add(cumulativeText);
        return Task.CompletedTask;
    }

    public Task CompleteAsync(
        string finalText,
        IReadOnlyList<Citation> citations,
        CancellationToken cancellationToken)
    {
        EnterTerminal();
        Completed = true;
        FinalText = finalText;
        FinalCitations = citations;
        return Task.CompletedTask;
    }

    public Task FailAsync(string finalTextSoFar, string reason, CancellationToken cancellationToken)
    {
        EnterTerminal();
        Failed = true;
        FinalText = finalTextSoFar;
        FailReason = reason;
        return Task.CompletedTask;
    }

    private void EnterTerminal()
    {
        // A terminal may stand alone (Begin never reached, e.g. routing failed
        // before the pump opened the sink); the sink reports the failure anyway.
        if (_terminal)
        {
            throw new InvalidOperationException("Second terminal call.");
        }
        _terminal = true;
        TerminalCount++;
    }
}
