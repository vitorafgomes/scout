namespace Scout.Core.Plugins;

/// <summary>
/// Declared capabilities of a Scout plugin. The read-only invariant is
/// enforced by construction — the type has no way to express a write-capable
/// plugin. Future orthogonal flags (caching, streaming, etc.) can be added
/// as additional properties without reintroducing a rejectable constructor
/// parameter.
/// </summary>
public sealed class PluginCapabilities
{
    public PluginCapabilities()
    {
    }

    public bool IsReadOnly => true;

    public static PluginCapabilities ReadOnly { get; } = new();
}
