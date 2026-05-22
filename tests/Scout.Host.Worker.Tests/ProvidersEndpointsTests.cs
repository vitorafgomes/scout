using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Scout.Host.Worker.Providers;
using Shouldly;

namespace Scout.Host.Worker.Tests;

public sealed class ProvidersEndpointsTests
{
    private static ScoutWorkerWebApplicationFactory NewFactoryWithFakeOllama(
        string? binaryPath = "/usr/local/bin/ollama",
        OllamaTags? tags = null,
        IProcessLauncher? launcher = null,
        ILocalRuntimeRegistry? registry = null,
        ILocalRuntimeConfigStore? store = null,
        bool autostartDefault = true)
    {
        var locator = Substitute.For<IBinaryLocator>();
        locator.FindOnPathAsync("ollama", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(binaryPath));
        var api = Substitute.For<IOllamaApiClient>();
        api.GetTagsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(tags));

        launcher ??= Substitute.For<IProcessLauncher>();
        registry ??= new InMemoryLocalRuntimeRegistry();
        if (store is null)
        {
            var s = Substitute.For<ILocalRuntimeConfigStore>();
            s.GetAutostartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(autostartDefault));
            store = s;
        }

        var injectedLauncher = launcher;
        var injectedRegistry = registry;
        var injectedStore = store;

        return new ScoutWorkerWebApplicationFactory
        {
            ConfigureServicesOverride = services =>
            {
                services.AddSingleton(locator);
                services.AddSingleton(api);
                services.AddSingleton(injectedLauncher);
                services.AddSingleton(injectedRegistry);
                services.AddSingleton(injectedStore);
            },
        };
    }

    [Fact]
    public async Task GetProviders_ReturnsBothProviders()
    {
        using var factory = NewFactoryWithFakeOllama();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/providers");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var providers = await response.Content.ReadFromJsonAsync<List<JsonElement>>();
        providers.ShouldNotBeNull();
        providers!.Count.ShouldBe(2);
        var ids = providers.Select(p => p.GetProperty("id").GetString()).ToArray();
        ids.ShouldContain("ollama");
        ids.ShouldContain("lm-studio");
    }

    [Fact]
    public async Task GetProviderStatus_Ollama_ReturnsLiveSnapshot()
    {
        using var factory = NewFactoryWithFakeOllama();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/providers/ollama/status");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        doc.GetProperty("id").GetString().ShouldBe("ollama");
        doc.GetProperty("installed").GetBoolean().ShouldBeTrue();
        doc.GetProperty("running").GetBoolean().ShouldBeFalse();
        doc.GetProperty("endpoint").GetString().ShouldBe("http://localhost:11434");
        doc.GetProperty("supportsLifecycle").GetBoolean().ShouldBeTrue();
        doc.GetProperty("models").GetArrayLength().ShouldBe(0);
        doc.GetProperty("lastError").GetProperty("code").GetString().ShouldBe("providers/unreachable");
    }

    [Fact]
    public async Task GetProviderStatus_UnknownId_Returns404()
    {
        using var factory = NewFactoryWithFakeOllama();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/providers/foo/status");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("providers/unknown");
    }

    [Fact]
    public async Task PostStart_Ollama_Returns200WithRealStatus()
    {
        // Daemon already reachable - StartAsync short-circuits and returns the
        // live status with running:true.
        var tags = new OllamaTags(Array.Empty<ProviderModel>());
        using var factory = NewFactoryWithFakeOllama(tags: tags);
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/providers/ollama/start", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        doc.GetProperty("id").GetString().ShouldBe("ollama");
        doc.GetProperty("running").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task PostStart_Ollama_StartTimeout_Returns504()
    {
        // Binary present, api always unreachable, fake launcher returns a
        // process that never exits. StartAsync should time out (StartTimeout
        // overridden to a tiny value via IConfiguration).
        var launcher = Substitute.For<IProcessLauncher>();
        var fakeProc = Substitute.For<ILaunchedProcess>();
        fakeProc.Pid.Returns(4321);
        fakeProc.HasExited.Returns(false);
        fakeProc.StopAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        launcher.Launch(Arg.Any<ProcessLaunchRequest>()).Returns(fakeProc);

        var factory = NewFactoryWithFakeOllama(tags: null, launcher: launcher);
        factory.WithConfiguration("Scout:Providers:Ollama:StartTimeout", "00:00:00.250");
        using (factory)
        {
            using var client = factory.CreateClient();

            var response = await client.PostAsync("/providers/ollama/start", content: null);

            response.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
            var body = await response.Content.ReadAsStringAsync();
            body.ShouldContain("providers/start-timeout");
        }
    }

    [Fact]
    public async Task PostStart_LmStudio_Returns501()
    {
        using var factory = NewFactoryWithFakeOllama();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/providers/lm-studio/start", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("providers/lifecycle-unsupported");
    }

    [Fact]
    public async Task PostRestart_NotManaged_Returns409()
    {
        using var factory = NewFactoryWithFakeOllama();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/providers/ollama/restart", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("providers/not-managed");
    }

    [Fact]
    public async Task PutAutostart_PersistsViaStore()
    {
        var store = Substitute.For<ILocalRuntimeConfigStore>();
        store.GetAutostartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        store.SetAutostartAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        using var factory = NewFactoryWithFakeOllama(store: store);
        using var client = factory.CreateClient();

        using var body = new StringContent("{\"enabled\":false}", Encoding.UTF8, "application/json");
        var response = await client.PutAsync("/providers/ollama/autostart", body);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await store.Received(1).SetAutostartAsync("ollama", false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PutAutostart_UnknownId_Returns404()
    {
        using var factory = NewFactoryWithFakeOllama();
        using var client = factory.CreateClient();

        using var body = new StringContent("{\"enabled\":true}", Encoding.UTF8, "application/json");

        var response = await client.PutAsync("/providers/foo/autostart", body);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
