using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Llm.Abstractions;

namespace Scout.Llm.Providers.None;

/// <summary>
/// <see cref="ILlmProviderFactory"/> for <c>Scout:Llm:Provider = None</c>, the
/// state the desktop app writes when no AI provider is connected. Chat answers
/// with one fixed message and embeddings fail with a clear error; nothing
/// reaches the network.
/// </summary>
public sealed class NoneLlmProviderFactory : ILlmProviderFactory
{
    /// <summary>The reply every chat request receives.</summary>
    public const string NoProviderMessage =
        "No AI provider is connected. Connect one on the AI Sources screen in Scout.";

    /// <inheritdoc />
    public string Name => "None";

    /// <inheritdoc />
    public void AddToKernel(IKernelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<IChatCompletionService>(new NoneChatCompletionService());
        builder.Services.AddSingleton(CreateEmbeddingGenerator());
    }

    /// <inheritdoc />
    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator()
        => new NoneEmbeddingGenerator();
}
