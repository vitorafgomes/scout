using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.AzureAIInference;
using Scout.Llm.Providers.AzureOpenAI;
using Scout.Llm.Providers.Gemini;
using Scout.Llm.Providers.HuggingFace;
using Scout.Llm.Providers.Mistral;
using Scout.Llm.Providers.Ollama;
using Scout.Llm.Providers.OpenAI;
using Scout.Llm.Providers.OpenRouter;

namespace Scout.Llm;

/// <summary>
/// Composition root for the Scout LLM gateway. Wires the active provider, a
/// single shared <see cref="Kernel"/>, the chat-completion and embedding
/// services resolved from that kernel, and the streaming-first
/// <see cref="IChatGateway"/> so every consumer shares one routing surface.
/// </summary>
public static class LlmServiceCollectionExtensions
{
    /// <summary>
    /// Register the Scout LLM gateway. Binds <c>Scout:Llm</c>, selects the
    /// provider, builds one shared kernel, and exposes
    /// <see cref="IChatCompletionService"/> and
    /// <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> from it.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown at startup when <c>LlmOptions.Provider</c> names an unknown provider.
    /// </exception>
    public static IServiceCollection AddScoutLlm(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<LlmOptions>(configuration.GetSection("Scout:Llm"));

        var provider = configuration.GetSection("Scout:Llm")["Provider"];
        if (string.IsNullOrWhiteSpace(provider))
        {
            provider = new LlmOptions().Provider;
        }

        switch (provider)
        {
            case "Ollama":
                services.AddOllamaLlmProvider();
                break;
            case "Gemini":
                services.AddGeminiLlmProvider();
                break;
            case "OpenAI":
                services.AddOpenAILlmProvider();
                break;
            case "OpenRouter":
                services.AddOpenRouterLlmProvider();
                break;
            case "AzureOpenAI":
                services.AddAzureOpenAILlmProvider();
                break;
            case "Mistral":
                services.AddMistralLlmProvider();
                break;
            case "HuggingFace":
                services.AddHuggingFaceLlmProvider();
                break;
            case "AzureAIInference":
                services.AddAzureAIInferenceLlmProvider();
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown LLM provider '{provider}'. Supported providers: Ollama, " +
                    "Gemini, OpenAI, OpenRouter, AzureOpenAI, Mistral, HuggingFace, " +
                    "AzureAIInference.");
        }

        services.AddSingleton(sp =>
        {
            var factory = sp.GetRequiredService<ILlmProviderFactory>();
            var builder = Kernel.CreateBuilder();
            factory.AddToKernel(builder);

            // Register every plugin contributed by feature modules (e.g. GitHub)
            // onto the shared kernel so the LLM can invoke them via
            // function-calling. Self-gating modules contribute none when off.
            foreach (var plugin in sp.GetServices<KernelPlugin>())
            {
                builder.Plugins.Add(plugin);
            }

            return builder.Build();
        });

        services.AddSingleton(sp =>
            sp.GetRequiredService<Kernel>().GetRequiredService<IChatCompletionService>());

        // Resolve the embedding generator straight from the provider factory, NOT
        // from the shared kernel. Deriving it from the kernel created a cycle:
        // kernel -> KernelPlugin (project knowledge) -> ProjectKnowledgeService ->
        // IEmbeddingGenerator -> kernel (still under construction). The kernel
        // still registers its own embedder via AddToKernel for its internal use.
        // Resolve the embedding generator straight from the provider factory, NOT
        // from the shared kernel. Deriving it from the kernel created a cycle:
        // kernel -> KernelPlugin (project knowledge) -> ProjectKnowledgeService ->
        // IEmbeddingGenerator -> kernel (still under construction). The kernel
        // still registers its own embedder via AddToKernel for its internal use.
        services.AddSingleton(sp =>
            sp.GetRequiredService<ILlmProviderFactory>().CreateEmbeddingGenerator());

        services.AddSingleton<IChatGateway>(sp =>
            new ChatGateway(
                sp.GetRequiredService<IChatCompletionService>(),
                sp.GetRequiredService<Kernel>(),
                sp.GetService<IOptions<LlmOptions>>(),
                sp.GetService<ILogger<ChatGateway>>(),
                // Optional: when a feature module registers the Project Expert
                // agent, the gateway streams replies through it so its grounding
                // instructions and tools apply. Absent, the direct chat path runs.
                sp.GetService<ChatCompletionAgent>()));

        return services;
    }
}
