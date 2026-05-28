using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.Ollama;

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
            default:
                throw new InvalidOperationException(
                    $"Unknown LLM provider '{provider}'. Supported providers: Ollama.");
        }

        services.AddSingleton(sp =>
        {
            var factory = sp.GetRequiredService<ILlmProviderFactory>();
            var builder = Kernel.CreateBuilder();
            factory.AddToKernel(builder);
            return builder.Build();
        });

        services.AddSingleton(sp =>
            sp.GetRequiredService<Kernel>().GetRequiredService<IChatCompletionService>());

        services.AddSingleton(sp =>
            sp.GetRequiredService<Kernel>()
              .GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>());

        services.AddSingleton<IChatGateway>(sp =>
            new ChatGateway(sp.GetRequiredService<IChatCompletionService>()));

        return services;
    }
}
