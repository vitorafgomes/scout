namespace Scout.ControlPlane.Tests;

internal sealed class StubTime(DateTimeOffset now, Action? onRead = null) : TimeProvider
{
    public override DateTimeOffset GetUtcNow()
    {
        onRead?.Invoke();
        return now;
    }
}
