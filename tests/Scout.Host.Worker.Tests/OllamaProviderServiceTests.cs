using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Scout.Host.Worker.Providers;
using Scout.Host.Worker.Tests.Helpers;
using Shouldly;

namespace Scout.Host.Worker.Tests;

public sealed class OllamaProviderServiceTests
{
    private static IOptions<OllamaProviderOptions> Opts() => Options.Create(new OllamaProviderOptions
    {
        Endpoint = "http://localhost:11434",
        PingTimeout = TimeSpan.FromSeconds(2),
    });

    private static OllamaProviderService NewService(
        IBinaryLocator locator,
        IOllamaApiClient api,
        IProcessLauncher? launcher = null,
        ILocalRuntimeRegistry? registry = null,
        ILocalRuntimeConfigStore? store = null,
        IOptions<OllamaProviderOptions>? options = null,
        IOllamaReadinessProbe? probe = null)
    {
        launcher ??= Substitute.For<IProcessLauncher>();
        registry ??= new InMemoryLocalRuntimeRegistry();
        if (store is null)
        {
            var s = Substitute.For<ILocalRuntimeConfigStore>();
            s.GetAutostartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(true));
            store = s;
        }
        probe ??= FakeReadinessProbes.AlwaysReady();
        return new OllamaProviderService(
            locator,
            api,
            launcher,
            registry,
            store,
            options ?? Opts(),
            probe,
            NullLogger<OllamaProviderService>.Instance);
    }

    [Fact]
    public async Task GetStatusAsync_BinaryAbsent_ReturnsNotInstalled()
    {
        var locator = Substitute.For<IBinaryLocator>();
        locator.FindOnPathAsync("ollama", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        var api = Substitute.For<IOllamaApiClient>();

        var service = NewService(locator, api);

        var status = await service.GetStatusAsync(CancellationToken.None);

        status.Id.ShouldBe("ollama");
        status.Installed.ShouldBeFalse();
        status.Running.ShouldBeFalse();
        status.SupportsLifecycle.ShouldBeFalse();
        status.Models.ShouldBeEmpty();
        status.LastError.ShouldBeNull();
        // The api client must not be invoked when the binary is missing.
        await api.DidNotReceiveWithAnyArgs().GetTagsAsync(default);
    }

    [Fact]
    public async Task GetStatusAsync_BinaryPresentDaemonUnreachable_ReturnsInstalledNotRunningWithLastError()
    {
        var locator = Substitute.For<IBinaryLocator>();
        locator.FindOnPathAsync("ollama", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("/usr/local/bin/ollama"));
        var api = Substitute.For<IOllamaApiClient>();
        api.GetTagsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<OllamaTags?>(null));

        var service = NewService(locator, api);

        var status = await service.GetStatusAsync(CancellationToken.None);

        status.Installed.ShouldBeTrue();
        status.Running.ShouldBeFalse();
        status.SupportsLifecycle.ShouldBeTrue();
        status.Models.ShouldBeEmpty();
        status.LastError.ShouldNotBeNull();
        status.LastError!.Code.ShouldBe("providers/unreachable");
        status.LastError.Message.ShouldContain("http://localhost:11434");
    }

    [Fact]
    public async Task GetStatusAsync_BinaryPresentDaemonReachable_ReturnsInstalledRunningWithModels()
    {
        var locator = Substitute.For<IBinaryLocator>();
        locator.FindOnPathAsync("ollama", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("/usr/local/bin/ollama"));
        var api = Substitute.For<IOllamaApiClient>();
        var tags = new OllamaTags(new[]
        {
            new ProviderModel("qwen2.5:3b", 1_936_000_000L, "2026-05-22T10:00:00Z"),
        });
        api.GetTagsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<OllamaTags?>(tags));

        var service = NewService(locator, api);

        var status = await service.GetStatusAsync(CancellationToken.None);

        status.Installed.ShouldBeTrue();
        status.Running.ShouldBeTrue();
        status.SupportsLifecycle.ShouldBeTrue();
        status.LastError.ShouldBeNull();
        status.Models.Count.ShouldBe(1);
        status.Models[0].Name.ShouldBe("qwen2.5:3b");
        status.Models[0].SizeBytes.ShouldBe(1_936_000_000L);
    }

    [Fact]
    public async Task GetStatusAsync_AutostartFromStore_FlowsThroughDto()
    {
        var locator = Substitute.For<IBinaryLocator>();
        locator.FindOnPathAsync("ollama", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("/usr/local/bin/ollama"));
        var api = Substitute.For<IOllamaApiClient>();
        api.GetTagsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<OllamaTags?>(null));
        var store = Substitute.For<ILocalRuntimeConfigStore>();
        store.GetAutostartAsync("ollama", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        var service = NewService(locator, api, store: store);

        var status = await service.GetStatusAsync(CancellationToken.None);

        status.Autostart.ShouldBeFalse();
    }

    [Fact]
    public async Task GetStatusAsync_WaitsForProbe_BeforePinging()
    {
        var locator = Substitute.For<IBinaryLocator>();
        locator.FindOnPathAsync("ollama", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("/usr/local/bin/ollama"));
        var api = Substitute.For<IOllamaApiClient>();
        api.GetTagsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OllamaTags?>(new OllamaTags(Array.Empty<ProviderModel>())));

        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = FakeReadinessProbes.Pending(gate);

        var service = NewService(locator, api, probe: probe);

        var pending = service.GetStatusAsync(CancellationToken.None);
        await Task.Delay(100);
        pending.IsCompleted.ShouldBeFalse();
        await api.DidNotReceiveWithAnyArgs().GetTagsAsync(default);

        gate.SetResult(true);

        var status = await pending;
        status.Running.ShouldBeTrue();
        await api.Received(1).GetTagsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetStatusEndpoint_BinaryPresentDaemonReachable_ReturnsModelsArray()
    {
        var locator = Substitute.For<IBinaryLocator>();
        locator.FindOnPathAsync("ollama", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("/usr/local/bin/ollama"));
        var api = Substitute.For<IOllamaApiClient>();
        api.GetTagsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OllamaTags?>(new OllamaTags(new[]
            {
                new ProviderModel("qwen2.5:3b", 1_936_000_000L, "2026-05-22T10:00:00Z"),
            })));

        using var factory = new ScoutWorkerWebApplicationFactory
        {
            ConfigureServicesOverride = services =>
            {
                services.AddSingleton(locator);
                services.AddSingleton(api);
                services.AddSingleton<IProcessLauncher>(Substitute.For<IProcessLauncher>());
                services.AddSingleton<ILocalRuntimeRegistry>(new InMemoryLocalRuntimeRegistry());
                var store = Substitute.For<ILocalRuntimeConfigStore>();
                store.GetAutostartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(true));
                services.AddSingleton(store);
            },
        };
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/providers/ollama/status");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        doc.GetProperty("installed").GetBoolean().ShouldBeTrue();
        doc.GetProperty("running").GetBoolean().ShouldBeTrue();
        doc.GetProperty("supportsLifecycle").GetBoolean().ShouldBeTrue();
        var models = doc.GetProperty("models");
        models.GetArrayLength().ShouldBe(1);
        models[0].GetProperty("name").GetString().ShouldBe("qwen2.5:3b");
        models[0].GetProperty("sizeBytes").GetInt64().ShouldBe(1_936_000_000L);
    }

    [Fact]
    public async Task GetStatusEndpoint_BinaryAbsent_ReturnsNotInstalled()
    {
        var locator = Substitute.For<IBinaryLocator>();
        locator.FindOnPathAsync("ollama", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        var api = Substitute.For<IOllamaApiClient>();

        using var factory = new ScoutWorkerWebApplicationFactory
        {
            ConfigureServicesOverride = services =>
            {
                services.AddSingleton(locator);
                services.AddSingleton(api);
                services.AddSingleton<IProcessLauncher>(Substitute.For<IProcessLauncher>());
                services.AddSingleton<ILocalRuntimeRegistry>(new InMemoryLocalRuntimeRegistry());
                var store = Substitute.For<ILocalRuntimeConfigStore>();
                store.GetAutostartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(true));
                services.AddSingleton(store);
            },
        };
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/providers/ollama/status");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        doc.GetProperty("installed").GetBoolean().ShouldBeFalse();
        doc.GetProperty("running").GetBoolean().ShouldBeFalse();
        doc.GetProperty("supportsLifecycle").GetBoolean().ShouldBeFalse();
        doc.GetProperty("models").GetArrayLength().ShouldBe(0);
    }
}
