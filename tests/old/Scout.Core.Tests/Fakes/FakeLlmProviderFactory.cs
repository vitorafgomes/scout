using Microsoft.SemanticKernel;
using Scout.Core.Llm;

namespace Scout.Core.Tests.Fakes;

/// <summary>
/// No-op LLM factory used by host-level unit tests so they do not depend on
/// a real provider project (Scout.Llm.Ollama lives outside Scout.Core's
/// reference graph on purpose).
/// </summary>
public sealed class FakeLlmProviderFactory : ILlmProviderFactory
{
    public string Name => "Fake";

    public void AddToKernel(IKernelBuilder builder)
    {
        // Intentionally adds nothing. V1 ScoutHost.HandleAsync returns a
        // static handshake and does not invoke the kernel; a chat-completion
        // service is only required once routing/intent handling lands.
    }
}
