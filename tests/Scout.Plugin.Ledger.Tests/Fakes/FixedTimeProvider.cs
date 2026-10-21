namespace Scout.Plugin.Ledger.Tests.Fakes;

/// <summary>
/// Deterministic <see cref="TimeProvider"/> that always reports a fixed instant,
/// so entry timestamps, month bucketing and formatting are stable across runs.
/// </summary>
internal sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _utcNow;

    public FixedTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;
}
