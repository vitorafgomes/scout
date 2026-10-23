namespace Scout.Plugin.Kubernetes;

/// <summary>
/// Configuration for the Kubernetes plugin, bound from "Scout:Plugins:Kubernetes".
/// The plugin self-gates: with no <see cref="KubeconfigPath"/> or no <see cref="Contexts"/>
/// nothing is registered.
/// </summary>
public sealed class KubernetesOptions
{
    /// <summary>Path of the kubeconfig file on this computer. Read in place, never copied.</summary>
    public string KubeconfigPath { get; set; } = string.Empty;

    /// <summary>
    /// The contexts the owner allowed Scout to read. The first is the default; any other
    /// context is refused before a client is built.
    /// </summary>
    public string[] Contexts { get; set; } = [];
}
