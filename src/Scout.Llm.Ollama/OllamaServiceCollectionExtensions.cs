using Microsoft.Extensions.DependencyInjection;
using Scout.Core.Llm;

namespace Scout.Llm.Ollama;

/// <summary>
/// DI extension for registering the Ollama LLM provider. Binds
/// <see cref="OllamaLlmProviderFactory"/> as the active
/// <see cref="ILlmProviderFactory"/>. Expects <see cref="LlmOptions"/> to
/// be configured elsewhere — typically by <c>AddScout(IConfiguration)</c>,
/// which binds it from the <c>Scout:Llm</c> section.
/// </summary>
public static class OllamaServiceCollectionExtensions
{
    public static IServiceCollection AddOllamaLlmProvider(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ILlmProviderFactory, OllamaLlmProviderFactory>();
        return services;
    }
}
