using Scout.Conversation.Access;

namespace Scout.Host.Console;

/// <summary>
/// Writes <c>SCOUT_PAIRED=&lt;userId&gt;</c> or <c>SCOUT_PAIRING_BURNED</c> to stdout,
/// outside the logging pipeline, so the Electron supervisor adds that user to the
/// allowed users or drops the pending code.
/// </summary>
internal sealed class StdoutPairingSignal(TextWriter? writer = null) : IPairingSignal
{
    private const string MarkerPrefix = "SCOUT_PAIRED=";
    private const string BurnedMarker = "SCOUT_PAIRING_BURNED";

    public void Paired(string userId) => Write(MarkerPrefix + userId);

    public void Burned() => Write(BurnedMarker);

    private void Write(string line)
    {
        var output = writer ?? System.Console.Out;
        output.WriteLine(line);
        output.Flush();
    }
}
