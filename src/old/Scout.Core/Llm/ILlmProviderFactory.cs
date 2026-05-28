using Microsoft.SemanticKernel;

namespace Scout.Core.Llm;

/// <summary>
/// Registers a Semantic Kernel chat-completion service on an
/// <see cref="IKernelBuilder"/>. Each implementation lives in its own project
/// (e.g. Scout.Llm.Ollama, Scout.Llm.AzureOpenAI) so Scout.Core stays free of
/// provider-specific transitive dependencies and their release cadence.
///
/// Scout.Core never references a provider project directly; the host composes
/// factories via DI at startup based on <see cref="LlmOptions.Provider"/>.
/// </summary>
public interface ILlmProviderFactory
{
    /// <summary>Factory identifier. Must match <see cref="LlmOptions.Provider"/>.</summary>
    string Name { get; }

    /// <summary>Register the provider's chat-completion service on the kernel builder.</summary>
    void AddToKernel(IKernelBuilder builder);
}
