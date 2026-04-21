using System.Diagnostics;

namespace Scout.Core.Telemetry;

/// <summary>
/// Single <see cref="ActivitySource"/> used by the Scout core and by every
/// adapter or plugin that wants to participate in the same distributed trace.
/// OpenTelemetry consumers subscribe to <see cref="SourceName"/>.
/// </summary>
public static class ScoutActivitySource
{
    public const string SourceName = "Scout";

    public static readonly ActivitySource Source = new(SourceName);
}
