using System.Diagnostics;

namespace Scout.Conversation.Telemetry;

/// <summary>
/// Single <see cref="ActivitySource"/> for the conversation core. Consumers
/// wire an OpenTelemetry tracer provider to the source name to collect spans.
/// </summary>
public static class ScoutActivitySource
{
    /// <summary>The source name to subscribe in tracer configuration.</summary>
    public const string Name = "Scout.Conversation";

    /// <summary>The shared activity source.</summary>
    public static readonly ActivitySource Instance = new(Name);
}
