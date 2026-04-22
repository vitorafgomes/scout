namespace Scout.Plugin.CodeQA.Tests.Composing;

/// <summary>
/// Minimal <see cref="TimeProvider"/> stub used to assert the composer
/// measures latency from the captured timestamp. <see cref="Advance"/>
/// bumps the internal counter; the base class's GetElapsedTime then
/// reports a deterministic span.
/// </summary>
internal sealed class TestTimeProvider : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _ticks;

    public void Advance(TimeSpan by) => _ticks += by.Ticks;

    public void SetTicks(long ticks) => _ticks = ticks;
}
