using Microsoft.SemanticKernel;

namespace Scout.Core.Plugins;

/// <summary>
/// Holds the set of plugins a Scout host will load. Enforces two startup
/// invariants: every plugin declares read-only capabilities, and plugin names
/// are unique. Both are checked at registration time; failures are unrecoverable
/// and prevent host startup.
/// </summary>
public sealed class PluginRegistry
{
    private readonly Dictionary<string, IScoutPlugin> _plugins = new(StringComparer.Ordinal);
    private readonly List<IScoutPlugin> _order = new();

    public IReadOnlyList<IScoutPlugin> Plugins => _order;

    public void Register(IScoutPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);

        if (plugin.Capabilities is null || !plugin.Capabilities.IsReadOnly)
        {
            throw new ReadOnlyViolationException(plugin.Name);
        }

        if (!_plugins.TryAdd(plugin.Name, plugin))
        {
            throw new InvalidOperationException(
                $"A plugin with name '{plugin.Name}' is already registered.");
        }

        _order.Add(plugin);
    }

    public IReadOnlyList<KernelPlugin> BuildAll(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var built = new List<KernelPlugin>(_order.Count);
        foreach (var plugin in _order)
        {
            built.Add(plugin.Build(services));
        }
        return built;
    }
}
