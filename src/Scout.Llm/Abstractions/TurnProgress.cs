namespace Scout.Llm.Abstractions;

/// <summary>
/// Where a function the model calls during a turn says what it is doing, so a chat that can show it tells the user
/// "Reading pull request file src/Checkout.cs" while the reply is still empty. The conversation conductor enters it
/// for the turn on the current async flow; outside a turn a report goes nowhere.
/// </summary>
public static class TurnProgress
{
    private static readonly AsyncLocal<Func<string, CancellationToken, Task>?> Slot = new();

    /// <summary>True while a turn that can show progress is on the current async flow.</summary>
    public static bool IsEntered => Slot.Value is not null;

    /// <summary>Tell the current turn's chat what is happening now; nothing happens outside a turn.</summary>
    public static Task ReportAsync(string activity, CancellationToken cancellationToken) =>
        Slot.Value is { } report ? report(activity, cancellationToken) : Task.CompletedTask;

    /// <summary>
    /// Route the reports of the current async flow to <paramref name="report"/> until the scope is disposed, which
    /// restores whatever was routed before, so a turn started from inside another leaves the outer one intact.
    /// </summary>
    public static Scope Enter(Func<string, CancellationToken, Task> report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var previous = Slot.Value;
        Slot.Value = report;
        return new Scope(previous);
    }

    /// <summary>Restores the previous routing on dispose.</summary>
    public readonly struct Scope(Func<string, CancellationToken, Task>? previous) : IDisposable
    {
        /// <inheritdoc />
        public void Dispose() => Slot.Value = previous;
    }
}
