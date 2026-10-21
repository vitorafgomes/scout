namespace Scout.Host.Console;

/// <summary>
/// Emits the startup readiness marker the Electron supervisor watches for.
/// </summary>
internal interface IReadinessSignal
{
    /// <summary>Signal that the host is up and listening.</summary>
    void Signal();
}

/// <summary>
/// Writes the literal marker line to stdout. Kept outside the logging pipeline
/// so the supervisor's exact-match parse sees a clean, unformatted line.
/// </summary>
internal sealed class StdoutReadinessSignal : IReadinessSignal
{
    private const string Marker = "SCOUT_READY";

    public void Signal()
    {
        System.Console.Out.WriteLine(Marker);
        System.Console.Out.Flush();
    }
}
