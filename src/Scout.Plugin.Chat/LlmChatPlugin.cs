using Microsoft.SemanticKernel;
using Scout.Core.Plugins;

namespace Scout.Plugin.Chat;

/// <summary>
/// Fallback LLM chat plugin. Streams any inbound message that no other
/// router matched through <c>IChatCompletionService</c>. Exposes no
/// Semantic Kernel functions: routing is fully manual (see
/// <see cref="LlmFallbackRouter"/>). <see cref="Build"/> returns an empty
/// <see cref="KernelPlugin"/> so the registry contract is satisfied
/// without leaking unnecessary SK surface.
/// </summary>
public sealed class LlmChatPlugin : IScoutPlugin
{
    public const string PluginName = "Chat";

    public string Name => PluginName;

    public PluginCapabilities Capabilities => PluginCapabilities.ReadOnly;

    public KernelPlugin Build(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return KernelPluginFactory.CreateFromFunctions(
            PluginName,
            description: "Fallback LLM chat router",
            functions: Array.Empty<KernelFunction>());
    }
}
