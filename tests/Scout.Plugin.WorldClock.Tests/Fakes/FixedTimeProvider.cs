namespace Scout.Plugin.WorldClock.Tests.Fakes;

/// <summary>
/// A <see cref="TimeProvider"/> pinned to a fixed instant so time-zone
/// computations are deterministic in tests. Only <see cref="GetUtcNow"/> is
/// consumed by the plugin, so nothing else needs overriding. Moving this instant
/// across a daylight-saving transition is how the DST tests exercise both sides.
/// </summary>
internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private readonly DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;
}
