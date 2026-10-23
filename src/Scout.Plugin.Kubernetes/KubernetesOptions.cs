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
    /// The contexts the owner allowed Scout to read, each with the label the model and chat
    /// users see. The first is the default; any other value is refused before a client is built.
    /// </summary>
    public List<KubernetesContextOption> Contexts { get; set; } = [];
}

/// <summary>One allowed kubeconfig context and the label shown instead of its name.</summary>
public sealed class KubernetesContextOption
{
    /// <summary>The context name in the kubeconfig. Never shown to the model or in chat.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The name the model and chat users use for this context; blank means <see cref="Name"/>.</summary>
    public string Label { get; set; } = string.Empty;
}
