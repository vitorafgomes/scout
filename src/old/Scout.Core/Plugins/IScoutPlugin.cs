using Microsoft.SemanticKernel;

namespace Scout.Core.Plugins;

/// <summary>
/// A Scout feature plugin. Each implementation contributes a Semantic Kernel
/// plugin plus Scout-specific metadata (capabilities, name) that the
/// <c>PluginRegistry</c> uses to enforce the read-only invariant at startup.
/// </summary>
public interface IScoutPlugin
{
    /// <summary>Unique, stable plugin identifier (used in configuration).</summary>
    string Name { get; }

    /// <summary>Declared capabilities. Must be read-only.</summary>
    PluginCapabilities Capabilities { get; }

    /// <summary>
    /// Build the <see cref="KernelPlugin"/> that will be added to the kernel
    /// at host construction time. <paramref name="services"/> is the host's
    /// DI container so plugins can resolve their own collaborators.
    /// </summary>
    KernelPlugin Build(IServiceProvider services);
}
