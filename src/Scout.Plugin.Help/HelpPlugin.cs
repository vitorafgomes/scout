using Microsoft.SemanticKernel;
using Scout.Core.Plugins;

namespace Scout.Plugin.Help;

/// <summary>
/// Baseline Scout plugin that answers simple introspection commands
/// (<c>ping</c>, <c>help</c>, <c>version</c>). Ships with no external
/// configuration and is registered unconditionally so Scout always has a
/// visible heartbeat, even before any feature plugin is wired up.
///
/// The plugin exposes no Semantic Kernel functions; routing is handled
/// entirely by <see cref="HelpMessageRouter"/>. <see cref="Build"/> returns
/// an empty <see cref="KernelPlugin"/> so the registry contract is
/// satisfied without leaking unnecessary SK surface.
/// </summary>
public sealed class HelpPlugin : IScoutPlugin
{
    public const string PluginName = "Help";

    public string Name => PluginName;

    public PluginCapabilities Capabilities => PluginCapabilities.ReadOnly;

    public KernelPlugin Build(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return KernelPluginFactory.CreateFromFunctions(
            PluginName,
            description: "Responds to ping and help",
            functions: Array.Empty<KernelFunction>());
    }
}
