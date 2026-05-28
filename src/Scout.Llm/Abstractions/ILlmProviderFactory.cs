using Microsoft.SemanticKernel;

namespace Scout.Llm.Abstractions;

/// <summary>
/// Registers a provider's Semantic Kernel services on an
/// <see cref="IKernelBuilder"/>. Every LLM call in Scout — chat completion and
/// embeddings alike — flows through the kernel built from these registrations,
/// so no consumer ever talks to a provider SDK directly.
///
/// Each implementation lives under its own provider folder (Ollama today,
/// AzureOpenAI tomorrow) so the abstraction layer stays free of connector
/// dependencies. The active factory is selected at startup based on
/// <c>LlmOptions.Provider</c>.
/// </summary>
public interface ILlmProviderFactory
{
    /// <summary>Factory identifier. Must match <c>LlmOptions.Provider</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Register the provider's chat-completion and embedding services on the
    /// kernel builder. Both must be added so the single shared kernel can serve
    /// every LLM call.
    /// </summary>
    void AddToKernel(IKernelBuilder builder);
}
