using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Scout.Conversation;
using Scout.Conversation.Abstractions;
using Scout.Llm;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.Ollama;
using Scout.Skills;
using Shouldly;
using Xunit;

namespace Scout.Host.Console.Tests;

/// <summary>
/// Composition smoke test that builds the exact production service graph via
/// <see cref="HostComposition.AddScoutHost"/> and validates it end to end. The
/// host hung at startup once on a construction-time DI cycle that every stubbed
/// test missed; this guards the whole class of regressions by exercising the real
/// wiring (no stubbed kernel, embedder, or gateway).
///
/// Two seams keep it network-free: the Ollama factory is rebound over a
/// fail-fast HttpClient so no construction path can reach the network, and the
/// host is only built and resolved, never started, so no adapter opens a gateway.
/// <c>ValidateOnBuild</c> catches missing or captive registrations; the timeout
/// guard turns a cycle/deadlock into a fast failure instead of a hung run.
/// </summary>
public sealed class HostCompositionSmokeTests
{
    private static readonly TimeSpan ResolveBudget = TimeSpan.FromSeconds(10);

    // Any outbound HTTP would mean construction touched the network, which it
    // must not. Throwing here turns that into a fast, loud failure.
    private sealed class FailFastHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException(
                "No network call expected while resolving the host graph.");
    }

    private static readonly Dictionary<string, string?> CoreConfig = new()
    {
        ["Scout:Llm:Provider"] = "Ollama",
        ["Scout:Llm:Ollama:Endpoint"] = "http://localhost:11434",
        ["Scout:Llm:Ollama:Chat:Model"] = "llama3.1",
        ["Scout:Llm:Ollama:Embeddings:Model"] = "nomic-embed-text",
        ["Scout:Conversation:SystemPrompt"] = "You are Scout.",
    };

    // Full host config: an adapter, the GitHub and Kubernetes plugins, and a project so
    // the Discord adapter, both plugins, and the Project Expert agent path all
    // participate. Dummy credentials only; the wiring is what is under test.
    private static IConfiguration FullConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(CoreConfig)
            {
                ["Scout:Adapters:Discord:BotToken"] = "discord-dummy-not-real",
                ["Scout:Plugins:GitHub:Token"] = "github-dummy-not-real",
                ["Scout:Plugins:Kubernetes:KubeconfigPath"] = "/nonexistent/scout-test/kubeconfig",
                ["Scout:Plugins:Kubernetes:Contexts:0:Name"] = "dev",
                ["Scout:Projects:Items:0:Name"] = "scout",
                ["Scout:Projects:Items:0:RepoPath"] = "/repos/scout",
            })
            .Build();

    // Minimal host config: no adapters, no plugin token, no projects. The host
    // must still compose and resolve without throwing.
    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(CoreConfig))
            .Build();

    // Build through the real host builder so the framework-provided services the
    // graph depends on (IHostApplicationLifetime, hosting glue) match production
    // exactly. ConversationWorker pulls IHostApplicationLifetime, which only the
    // host builder registers; a bare ServiceCollection would not reproduce that.
    private static IHost BuildHost(IConfiguration configuration)
    {
        return new HostBuilder()
            .ConfigureHostConfiguration(b => b.AddConfiguration(configuration))
            .ConfigureAppConfiguration(b => b.AddConfiguration(configuration))
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();

                // The exact production wiring. Anything that drifts here drifts in the test.
                services.AddScoutHost(context.Configuration);

                // Rebind the type-registered Ollama factory over a fail-fast HttpClient
                // so no construction path can reach the network. AddScoutLlm registers
                // the factory via TryAddSingleton, so an explicit replace wins.
                services.RemoveAll<ILlmProviderFactory>();
                services.AddSingleton<ILlmProviderFactory>(sp =>
                {
                    var options = sp.GetRequiredService<IOptions<LlmOptions>>();
                    var http = new HttpClient(new FailFastHandler());
                    return new OllamaLlmProviderFactory(options, http);
                });
            })
            // ValidateOnBuild surfaces missing/captive dependencies at build time;
            // ValidateScopes catches scoped services captured by singletons. This is
            // what would have caught the captive/missing registration that hung startup.
            .UseDefaultServiceProvider(o =>
            {
                o.ValidateOnBuild = true;
                o.ValidateScopes = true;
            })
            .Build();
    }

    [Fact]
    public void FullConfig_Builds_And_Resolves_Entire_Host_Graph()
    {
        using var host = BuildHost(FullConfig());
        var provider = host.Services;

        // The shared kernel aggregates every KernelPlugin; resolving it is what
        // re-entered construction and deadlocked before the fix.
        ResolveWithin(() => provider.GetRequiredService<Kernel>()).ShouldNotBeNull();
        ResolveWithin(() => provider.GetRequiredService<IChatGateway>()).ShouldNotBeNull();
        ResolveWithin(() => provider.GetRequiredService<ConversationConductor>()).ShouldNotBeNull();

        // Every hosted service, including ConversationWorker, must construct. The
        // worker pulls the adapters, conductor, lifetime and readiness signal, so
        // resolving it exercises the whole inbound path without starting it.
        var hostedServices = ResolveWithin(
            () => provider.GetServices<IHostedService>().ToList());
        hostedServices.ShouldNotBeEmpty();
        hostedServices.ShouldContain(s => s is ConversationWorker);
    }

    [Fact]
    public void FullConfig_Registers_DiscordAdapter_And_ProjectKnowledgePlugin()
    {
        using var host = BuildHost(FullConfig());
        var provider = host.Services;

        var adapters = ResolveWithin(
            () => provider.GetServices<IChatAdapter>().ToList());
        adapters.ShouldContain(a => a.Name == "Discord");

        var kernel = ResolveWithin(() => provider.GetRequiredService<Kernel>());
        kernel.Plugins.ShouldContain(p => p.Name == "ProjectKnowledge");

        // The tool-based skills plugin is aggregated too. Resolving it here proves
        // its embedding-free catalog builds under the fail-fast HttpClient and the
        // budget, so it adds no construction-time I/O or cycle.
        kernel.Plugins.ShouldContain(p => p.Name == "Skills");
    }

    [Fact]
    public void FullConfig_Puts_The_Kubernetes_Plugin_On_The_Kernel_Without_Reading_The_Kubeconfig()
    {
        using var host = BuildHost(FullConfig());

        var kernel = ResolveWithin(() => host.Services.GetRequiredService<Kernel>());

        kernel.Plugins.ShouldContain(p => p.Name == "Kubernetes");
    }

    [Fact]
    public void Resolves_SkillSelector_Without_Indexing_Or_Network()
    {
        using var host = BuildHost(FullConfig());
        var provider = host.Services;

        // The selector must construct under the fail-fast HttpClient and budget.
        // If it embedded skill descriptions or loaded files at build/resolve time
        // it would hit the network (throwing) or exceed the budget; it must defer
        // all that to the first SelectAsync call.
        var selector = ResolveWithin(() => provider.GetRequiredService<ISkillSelector>());
        selector.ShouldNotBeNull();
    }

    [Fact]
    public void EmptyConfig_Builds_Host_With_No_Adapters_And_Still_Resolves()
    {
        using var host = BuildHost(EmptyConfig());
        var provider = host.Services;

        ResolveWithin(() => provider.GetRequiredService<Kernel>()).ShouldNotBeNull();
        ResolveWithin(() => provider.GetRequiredService<IChatGateway>()).ShouldNotBeNull();

        provider.GetServices<IChatAdapter>().ShouldBeEmpty();

        // No projects configured: the search_project plugin is gated off the kernel
        // so the model never picks a tool that would fail on an empty index, and the
        // Project Expert agent is not registered. The host still builds regardless.
        var kernel = ResolveWithin(() => provider.GetRequiredService<Kernel>());
        kernel.Plugins.ShouldNotContain(p => p.Name == "ProjectKnowledge");
        kernel.Plugins.ShouldNotContain(p => p.Name == "Kubernetes");

        // The host still builds and the worker still resolves.
        var hostedServices = ResolveWithin(
            () => provider.GetServices<IHostedService>().ToList());
        hostedServices.ShouldContain(s => s is ConversationWorker);
    }

    // Run the resolution on a worker thread and join under a budget. A construction
    // cycle re-enters the kernel and deadlocks; the join then times out and the
    // test fails fast instead of hanging the whole run.
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
                "Host graph resolution did not complete within the budget; a " +
                "construction cycle likely re-entered the kernel and deadlocked.");
        }

        if (captured is not null)
        {
            throw new Xunit.Sdk.XunitException(
                $"Host graph resolution threw instead of completing: {captured}");
        }

        return result;
    }
}
