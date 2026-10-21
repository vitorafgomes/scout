namespace Scout.Plugin.Weather.Tests.Fakes;

/// <summary>
/// A <see cref="TimeProvider"/> pinned to a fixed instant so date resolution is
/// deterministic and never depends on the wall clock. Avoids taking a test-only
/// package dependency for a single overridden member.
/// </summary>
internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private readonly DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;
}
