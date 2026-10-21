namespace Scout.Plugin.Reminders.Tests.Fakes;

/// <summary>
/// Deterministic <see cref="TimeProvider"/> that always reports a fixed instant,
/// so reminder timestamps and relative-date resolution are stable across runs.
/// </summary>
internal sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _utcNow;

    public FixedTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;
}
