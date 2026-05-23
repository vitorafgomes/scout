using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Scout.Host.Worker.Providers;
using Scout.Host.Worker.Tests.Helpers;
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
        bool autostartDefault = true,
        IOllamaReadinessProbe? probe = null,
        IOllamaApiClient? api = null)
    {
        var locator = Substitute.For<IBinaryLocator>();
        locator.FindOnPathAsync("ollama", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(binaryPath));
        if (api is null)
        {
            api = Substitute.For<IOllamaApiClient>();
            api.GetTagsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(tags));
        }

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
        var injectedApi = api;
        var injectedProbe = probe;

        return new ScoutWorkerWebApplicationFactory
        {
            ConfigureServicesOverride = services =>
            {
                services.AddSingleton(locator);
                services.AddSingleton(injectedApi);
                services.AddSingleton(injectedLauncher);
                services.AddSingleton(injectedRegistry);
                services.AddSingleton(injectedStore);
                if (injectedProbe is not null)
                {
                    services.AddSingleton(injectedProbe);
                }
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
    public async Task PostStop_Ollama_Returns200()
    {
        // Daemon not running -> StopAsync short-circuits with the current status.
        using var factory = NewFactoryWithFakeOllama();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/providers/ollama/stop", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        doc.GetProperty("id").GetString().ShouldBe("ollama");
        doc.GetProperty("running").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task PostStop_LmStudio_Returns501()
    {
        using var factory = NewFactoryWithFakeOllama();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/providers/lm-studio/stop", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("providers/lifecycle-unsupported");
    }

    [Fact]
    public async Task PostStop_Failed_Returns502()
    {
        // Daemon always reachable; pkill helper "succeeds" but the daemon
        // never goes away, so the poll boundary trips with ProviderStopFailed.
        var launcher = Substitute.For<IProcessLauncher>();
        var killer = Substitute.For<ILaunchedProcess>();
        killer.HasExited.Returns(true);
        killer.WaitForExitAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(0));
        launcher.Launch(Arg.Any<ProcessLaunchRequest>()).Returns(killer);

        using var factory = NewFactoryWithFakeOllama(
            tags: new OllamaTags(Array.Empty<ProviderModel>()),
            launcher: launcher);
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/providers/ollama/stop", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("providers/stop-failed");
    }

    [Fact]
    public async Task DeleteModel_Ollama_Returns204()
    {
        var api = Substitute.For<IOllamaApiClient>();
        api.GetTagsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OllamaTags?>(null));
        api.DeleteModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var locator = Substitute.For<IBinaryLocator>();
        locator.FindOnPathAsync("ollama", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("/usr/local/bin/ollama"));

        var store = Substitute.For<ILocalRuntimeConfigStore>();
        store.GetAutostartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        using var factory = new ScoutWorkerWebApplicationFactory
        {
            ConfigureServicesOverride = services =>
            {
                services.AddSingleton(locator);
                services.AddSingleton(api);
                services.AddSingleton<IProcessLauncher>(Substitute.For<IProcessLauncher>());
                services.AddSingleton<ILocalRuntimeRegistry>(new InMemoryLocalRuntimeRegistry());
                services.AddSingleton(store);
            },
        };
        using var client = factory.CreateClient();

        var response = await client.DeleteAsync("/providers/ollama/models/qwen2.5:3b");

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await api.Received(1).DeleteModelAsync("qwen2.5:3b", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteModel_LmStudio_Returns501()
    {
        using var factory = NewFactoryWithFakeOllama();
        using var client = factory.CreateClient();

        var response = await client.DeleteAsync("/providers/lm-studio/models/foo");

        response.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("providers/lifecycle-unsupported");
    }

    [Fact]
    public async Task PostPull_Ollama_StreamsSseEvents()
    {
        var api = Substitute.For<IOllamaApiClient>();
        api.GetTagsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OllamaTags?>(null));
        api.PullAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => YieldPullEvents());

        var locator = Substitute.For<IBinaryLocator>();
        locator.FindOnPathAsync("ollama", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("/usr/local/bin/ollama"));

        var store = Substitute.For<ILocalRuntimeConfigStore>();
        store.GetAutostartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        using var factory = new ScoutWorkerWebApplicationFactory
        {
            ConfigureServicesOverride = services =>
            {
                services.AddSingleton(locator);
                services.AddSingleton(api);
                services.AddSingleton<IProcessLauncher>(Substitute.For<IProcessLauncher>());
                services.AddSingleton<ILocalRuntimeRegistry>(new InMemoryLocalRuntimeRegistry());
                services.AddSingleton(store);
            },
        };
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/providers/ollama/models/qwen2.5:3b/pull", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");

        var body = await response.Content.ReadAsStringAsync();
        var dataLines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("data: ", StringComparison.Ordinal))
            .ToArray();
        dataLines.Length.ShouldBe(2);
        dataLines[0].ShouldContain("pulling manifest");
        dataLines[1].ShouldContain("success");
    }

    private static async IAsyncEnumerable<PullProgress> YieldPullEvents()
    {
        yield return new PullProgress("pulling manifest", null, null, null);
        await Task.Yield();
        yield return new PullProgress("success", null, null, null);
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

    [Fact]
    public async Task GetProviders_WhenProbeFailed_ReturnsStatusWithLastError_AndDoesNotPingHttp()
    {
        var api = Substitute.For<IOllamaApiClient>();
        api.GetTagsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<OllamaTags?>(null));
        var probe = FakeReadinessProbes.NeverReady();

        using var factory = NewFactoryWithFakeOllama(api: api, probe: probe);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/providers");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var providers = await response.Content.ReadFromJsonAsync<List<JsonElement>>();
        providers.ShouldNotBeNull();
        var ollama = providers!.First(p => p.GetProperty("id").GetString() == "ollama");
        ollama.GetProperty("running").GetBoolean().ShouldBeFalse();
        ollama.GetProperty("lastError").GetProperty("code").GetString().ShouldBe("providers/unreachable");
        await api.DidNotReceiveWithAnyArgs().GetTagsAsync(default);
    }

    [Fact]
    public async Task StartProvider_WhenProbeFailed_Returns503()
    {
        var probe = FakeReadinessProbes.NeverReady();
        using var factory = NewFactoryWithFakeOllama(probe: probe);
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/providers/ollama/start", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("providers/not-ready");
    }

    [Fact]
    public async Task PullProviderModel_WhenProbeFailed_EmitsSseError_AndCloses()
    {
        var api = Substitute.For<IOllamaApiClient>();
        api.GetTagsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<OllamaTags?>(null));
        var probe = FakeReadinessProbes.NeverReady();

        using var factory = NewFactoryWithFakeOllama(api: api, probe: probe);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/providers/ollama/models/qwen2.5:3b/pull",
            content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("event: error");
        body.ShouldContain("providers/not-ready");
        api.DidNotReceiveWithAnyArgs().PullAsync(default!, default);
    }
}
