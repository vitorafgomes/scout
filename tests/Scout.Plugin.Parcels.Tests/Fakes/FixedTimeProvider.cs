namespace Scout.Plugin.Parcels.Tests.Fakes;

/// <summary>
/// Deterministic <see cref="TimeProvider"/> that always reports a fixed instant,
/// so watch-list timestamps and formatting are stable across test runs.
/// </summary>
internal sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _utcNow;

    public FixedTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;
}
