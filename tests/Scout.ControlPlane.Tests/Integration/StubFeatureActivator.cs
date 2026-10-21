using System.Collections.Concurrent;
using Scout.ControlPlane.Features;

namespace Scout.ControlPlane.Tests.Integration;

/// <summary>
/// Permissive activator so the enable/disable round-trip actually toggles state
/// (the production default <c>NullFeatureActivator</c> reports CanActivate=false).
/// </summary>
internal sealed class StubFeatureActivator : IFeatureActivator
{
    private readonly ConcurrentDictionary<string, bool> _active = new(StringComparer.Ordinal);

    public bool CanActivate(string featureId) => true;

    public bool IsActive(string featureId) => _active.TryGetValue(featureId, out var v) && v;

    public Task ActivateAsync(string featureId, CancellationToken ct)
    {
        _active[featureId] = true;
        return Task.CompletedTask;
    }

    public Task DeactivateAsync(string featureId, CancellationToken ct)
    {
        _active[featureId] = false;
        return Task.CompletedTask;
    }
}
