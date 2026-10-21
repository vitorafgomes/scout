namespace Scout.Plugin.Dates.Tests.Fakes;

/// <summary>
/// A <see cref="TimeProvider"/> pinned to a fixed instant so date computations
/// are deterministic in tests. Only <see cref="GetUtcNow"/> is consumed by
/// <c>RelaxedDate</c>, so nothing else needs overriding.
/// </summary>
internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private readonly DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;
}
