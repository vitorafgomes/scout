using Microsoft.SemanticKernel;

namespace Scout.Host.Worker.Features;

/// <summary>
/// Marker abstraction for a Scout feature plugin. Phase 2 and 3 supply
/// concrete implementations; Phase 1 only ships the seam.
///
/// A feature plugin builds the Semantic Kernel <see cref="KernelPlugin"/>
/// to attach when the feature is enabled, and tears it down on disable.
/// Resolution of credentials and per-feature configuration happens
/// inside the implementation; the registrar passes only the feature id.
/// </summary>
public interface IFeaturePlugin
{
    /// <summary>Feature id this plugin implements.</summary>
    string FeatureId { get; }

    /// <summary>
    /// Name of the <see cref="KernelPlugin"/> the plugin contributes.
    /// Used by the registrar to look the plugin up for removal on
    /// disable.
    /// </summary>
    string KernelPluginName { get; }

    /// <summary>
    /// Build the kernel plugin to register. Implementations may resolve
    /// credentials from <see cref="ICredentialStore"/> internally.
    /// </summary>
    Task<KernelPlugin> BuildAsync(CancellationToken cancellationToken);
}
