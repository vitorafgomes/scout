using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Core.Chat;
using Scout.Core.Llm;
using Scout.Core.Plugins;
using Scout.Plugin.CodeQA.Chunking;
using Scout.Plugin.CodeQA.Composing;
using Scout.Plugin.CodeQA.Functions;
using Scout.Plugin.CodeQA.Indexing;
using Scout.Plugin.CodeQA.Retrieval;
using Scout.Plugin.CodeQA.Routing;
using Scout.Plugin.CodeQA.Storage;

namespace Scout.Plugin.CodeQA;

/// <summary>
/// DI composition for the CodeQA plugin. Two overloads:
///
/// <list type="bullet">
/// <item>
///   <see cref="AddCodeQAPlugin(IServiceCollection)"/> — core wiring only.
///   The host is responsible for <see cref="Embedding.IEmbedder"/> and
///   <see cref="IChatCompletionService"/>.
/// </item>
/// <item>
///   <see cref="AddCodeQAPlugin(IServiceCollection, IConfiguration)"/> —
///   core wiring plus <see cref="CodeQAOptions"/> binding. Registers
///   nothing when <c>Repository.Owner</c> is empty, mirroring CloudStatus's
///   "opt-in when configured" rule — an unconfigured plugin must never
///   break host startup.
/// </item>
/// </list>
/// </summary>
public static class CodeQAServiceCollectionExtensions
{
    /// <summary>Configuration root bound by the configuring overload.</summary>
    public const string ConfigSection = "Scout:Plugins:CodeQA";

    /// <summary>
    /// Register the CodeQA plugin's pipeline without binding options or
    /// touching configuration. Consumers must bind
    /// <see cref="CodeQAOptions"/> themselves and register their own
    /// <see cref="Embedding.IEmbedder"/> and <see cref="IChatCompletionService"/>.
    /// </summary>
    public static IServiceCollection AddCodeQAPlugin(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IVectorStore, InMemoryVectorStore>();
        services.AddSingleton<IChunker, TokenChunker>();
        services.AddSingleton<IGitHubTreeClient, OctokitGitHubTreeClient>();
        // GitHubIngestor and its collaborator IGitHubTreeClient are both
        // internal to the CodeQA plugin, so the DI container cannot resolve
        // the internal constructor reflectively. Build it explicitly.
        services.AddSingleton<IIngestor>(sp => new GitHubIngestor(
            sp.GetRequiredService<IOptions<CodeQAOptions>>(),
            sp.GetRequiredService<IGitHubTreeClient>(),
            sp.GetService<ILoggerFactory>()));
        services.AddSingleton<IRetriever, TopKRetriever>();
        services.AddSingleton<IComposer, LlmComposer>();
        services.AddSingleton<CodeQAFunctions>();
        services.AddSingleton<IScoutPlugin, CodeQAPlugin>();
        services.AddSingleton<IMessageRouter, CodeQAMessageRouter>();

        return services;
    }

    /// <summary>
    /// Register the CodeQA plugin and bind <see cref="CodeQAOptions"/>
    /// from the <c>Scout:Plugins:CodeQA</c> section. When
    /// <c>Repository.Owner</c> is empty or whitespace the plugin is left
    /// unconfigured and nothing is registered — a bare
    /// <c>appsettings.json</c> must not drag the pipeline up.
    /// </summary>
    /// <remarks>
    /// Also registers <see cref="IChatCompletionService"/> by building a
    /// kernel on the fly via the already-registered
    /// <see cref="ILlmProviderFactory"/>. Scout's kernel is constructed
    /// lazily inside <c>ScoutHost</c> and is not itself a DI service, so
    /// the composer cannot pull the service from the host kernel; it gets
    /// its own.
    /// </remarks>
    public static IServiceCollection AddCodeQAPlugin(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigSection);
        services.Configure<CodeQAOptions>(section);

        var owner = section["Repository:Owner"];
        if (string.IsNullOrWhiteSpace(owner))
        {
            return services;
        }

        services.AddCodeQAPlugin();

        // Only register IChatCompletionService when no other component
        // already provides one. The host lights one up by resolving the
        // active ILlmProviderFactory and building a throwaway kernel,
        // because Scout.Core's kernel is constructed lazily inside
        // ScoutHost and is not itself a DI service.
        if (!services.Any(d => d.ServiceType == typeof(IChatCompletionService)))
        {
            services.AddSingleton<IChatCompletionService>(sp =>
            {
                var llmFactory = sp.GetRequiredService<ILlmProviderFactory>();
                var builder = Kernel.CreateBuilder();
                llmFactory.AddToKernel(builder);
                var kernel = builder.Build();
                return kernel.GetRequiredService<IChatCompletionService>();
            });
        }

        return services;
    }
}
