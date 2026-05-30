using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Scout.Conversation;
using Scout.Knowledge;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.Ollama;

namespace Scout.Llm.Tests;

/// <summary>
/// Guards the construction-time dependency cycle that hung host startup: the
/// shared <see cref="Kernel"/> aggregates every <c>KernelPlugin</c>, and the
/// project-knowledge plugin pulls an <c>IEmbeddingGenerator</c>. When that
/// embedder was derived from the kernel, resolving the kernel re-entered the
/// kernel mid-construction and deadlocked.
///
/// The test wires the real LLM + Conversation + Knowledge modules together (no
/// stubbed embedder, which is what let the earlier DI test miss the cycle) and
/// asserts the kernel and chat gateway resolve under a hard timeout. The Ollama
/// factory is built over a stubbed HttpMessageHandler that fails every request,
/// so the wiring is exercised without any real network call.
/// </summary>
public sealed class CompositionCycleRegressionTests
{
    private static readonly TimeSpan ResolveBudget = TimeSpan.FromSeconds(10);

    // Any outbound HTTP would mean construction touched the network, which it
    // must not. Throwing here turns that into a fast, loud failure.
    private sealed class FailFastHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException(
                "No network call expected while resolving the DI graph.");
    }

    private static IConfiguration Config() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Llm:Provider"] = "Ollama",
                ["Scout:Llm:Ollama:Endpoint"] = "http://localhost:11434",
                ["Scout:Llm:Ollama:Chat:Model"] = "llama3.1",
                ["Scout:Llm:Ollama:Embeddings:Model"] = "nomic-embed-text",
                ["Scout:Conversation:SystemPrompt"] = "You are Scout.",
                // At least one project so the Project Expert agent is registered
                // and the full knowledge graph (plugin + agent over the shared
                // kernel) participates in resolution.
                ["Scout:Projects:Items:0:Name"] = "scout",
                ["Scout:Projects:Items:0:RepoPath"] = "/repos/scout",
            })
            .Build();

    private static ServiceProvider BuildProvider()
    {
        var configuration = Config();
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddScoutLlm(configuration);
        services.AddScoutConversation(configuration);
        services.AddProjectKnowledge(configuration);

        // Replace the type-registered Ollama factory with one bound to a stubbed
        // HttpClient so no construction path can reach the network. AddScoutLlm
        // registers the factory via TryAddSingleton, so an explicit replace wins.
        services.RemoveAll<ILlmProviderFactory>();
        services.AddSingleton<ILlmProviderFactory>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<LlmOptions>>();
            var http = new HttpClient(new FailFastHandler());
            return new OllamaLlmProviderFactory(options, http);
        });

        return services.BuildServiceProvider();
    }

    [Fact]
    public void Resolving_Shared_Kernel_Does_Not_Deadlock_Or_Cycle()
    {
        using var provider = BuildProvider();

        // A regression re-enters the kernel mid-construction and hangs, so guard
        // with a timeout: a stalled resolve fails fast instead of freezing the run.
        var kernel = ResolveWithin(() => provider.GetRequiredService<Kernel>());

        kernel.ShouldNotBeNull();
    }

    [Fact]
    public void Resolving_Chat_Gateway_Graph_Does_Not_Deadlock_Or_Cycle()
    {
        using var provider = BuildProvider();

        var gateway = ResolveWithin(() => provider.GetRequiredService<IChatGateway>());

        gateway.ShouldNotBeNull();
    }

    [Fact]
    public void Shared_Kernel_Still_Carries_Search_Project_Plugin()
    {
        using var provider = BuildProvider();

        var kernel = ResolveWithin(() => provider.GetRequiredService<Kernel>());

        kernel.Plugins.ShouldContain(p => p.Name == "ProjectKnowledge");
    }

    // Run the resolution on a worker thread and join under a budget. If the graph
    // deadlocks (the pre-fix behaviour) the join times out and the test fails
    // rather than hanging the entire test process.
    private static T ResolveWithin<T>(Func<T> resolve)
    {
        T result = default!;
        Exception? captured = null;

        var worker = new Thread(() =>
        {
            try
            {
                result = resolve();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        })
        {
            IsBackground = true,
        };

        worker.Start();

        if (!worker.Join(ResolveBudget))
        {
            throw new Xunit.Sdk.XunitException(
                "DI resolution did not complete within the budget; a construction " +
                "cycle likely re-entered the kernel and deadlocked.");
        }

        if (captured is not null)
        {
            throw new Xunit.Sdk.XunitException(
                $"DI resolution threw instead of completing: {captured}");
        }

        return result;
    }
}
