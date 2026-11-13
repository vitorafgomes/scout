using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Scout.Conversation;
using Scout.Conversation.Abstractions;
using Scout.Knowledge;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.Ollama;
using Scout.Skills;
using Shouldly;
using Xunit;

namespace Scout.Host.Console.Tests;

/// <summary>
/// Composition smoke test for the minimal <c>index</c> host graph built via
/// <see cref="HostComposition.AddScoutIndexHost"/>. Confirms the index path
/// resolves exactly what <see cref="IndexEntryPoint"/> needs and registers none
/// of the inbound chat path (no adapters, no conversation worker, no skills),
/// and that it builds with no network call or indexing at build time.
/// </summary>
public sealed class IndexHostCompositionSmokeTests
{
    private static readonly TimeSpan ResolveBudget = TimeSpan.FromSeconds(10);

    private sealed class FailFastHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException(
                "No network call expected while resolving the index host graph.");
    }

    private static IConfiguration IndexConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Llm:Provider"] = "Ollama",
                ["Scout:Llm:Ollama:Endpoint"] = "http://localhost:11434",
                ["Scout:Llm:Ollama:Chat:Model"] = "llama3.1",
                ["Scout:Llm:Ollama:Embeddings:Model"] = "nomic-embed-text",
                ["Scout:Projects:Items:0:Name"] = "scout",
                ["Scout:Projects:Items:0:RepoPath"] = AppContext.BaseDirectory,
            })
            .Build();

    private static IHost BuildIndexHost(IConfiguration configuration)
    {
        return new HostBuilder()
            .ConfigureHostConfiguration(b => b.AddConfiguration(configuration))
            .ConfigureAppConfiguration(b => b.AddConfiguration(configuration))
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();

                // The exact index wiring. Anything that drifts here drifts in the test.
                services.AddScoutIndexHost(context.Configuration);

                // Rebind the Ollama factory over a fail-fast HttpClient so no
                // construction path can reach the network.
                services.RemoveAll<ILlmProviderFactory>();
                services.AddSingleton<ILlmProviderFactory>(sp =>
                {
                    var options = sp.GetRequiredService<IOptions<LlmOptions>>();
                    var http = new HttpClient(new FailFastHandler());
                    return new OllamaLlmProviderFactory(options, http);
                });
            })
            .UseDefaultServiceProvider(o =>
            {
                o.ValidateOnBuild = true;
                o.ValidateScopes = true;
            })
            .Build();
    }

    [Fact]
    public void IndexHost_Resolves_Indexer_And_Store()
    {
        using var host = BuildIndexHost(IndexConfig());
        var provider = host.Services;

        ResolveWithin(() => provider.GetRequiredService<ProjectIndexer>()).ShouldNotBeNull();
        ResolveWithin(() => provider.GetRequiredService<IProjectKnowledgeStore>()).ShouldNotBeNull();
    }

    [Fact]
    public void IndexHost_Registers_No_ChatAdapters_Or_ConversationWorker_Or_SkillSelector()
    {
        using var host = BuildIndexHost(IndexConfig());
        var provider = host.Services;

        provider.GetServices<IChatAdapter>().ShouldBeEmpty();

        // No conversation pipeline and no skills in the minimal index graph.
        provider.GetService<ISkillSelector>().ShouldBeNull();
        provider.GetService<ConversationConductor>().ShouldBeNull();

        // No ConversationWorker hosted service in the minimal graph.
        var hostedServices = provider.GetServices<IHostedService>().ToList();
        hostedServices.ShouldNotContain(s => s is ConversationWorker);
    }

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
                "Index host graph resolution did not complete within the budget.");
        }

        if (captured is not null)
        {
            throw new Xunit.Sdk.XunitException(
                $"Index host graph resolution threw instead of completing: {captured}");
        }

        return result;
    }
}
