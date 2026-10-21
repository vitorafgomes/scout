using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Scout.ControlPlane.Features;
using Scout.ControlPlane.Providers;
using Shouldly;
using Xunit;

namespace Scout.Host.Console.Tests;

/// <summary>
/// Drives <see cref="ControlPlaneHttpHost"/> over a real loopback socket with
/// <see cref="HttpClient"/>. The domain services are built from substituted
/// dependencies so happy paths, lifecycle mapping and every failure mode of the
/// HTTP boundary are exercised end to end.
///
/// All cases live in one class so they never run concurrently: the auth case
/// toggles a process-wide environment variable and must not race a peer host.
/// </summary>
public sealed class ControlPlaneHttpHostTests
{
    private const string TokenEnvVarName = "SCOUT_CONTROLPLANE_TOKEN";

    [Fact]
    public async Task GetProviders_ReturnsMappedDtoArray()
    {
        var models = new[] { new ProviderModel("qwen3:8b", 4_100_000_000L, "2026-01-01T00:00:00Z") };
        await using var fixture = await HostFixture.StartAsync(providers: BuildProviders(models: models));

        using var response = await fixture.Client.GetAsync("/providers");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var doc = await ParseAsync(response);
        var root = doc.RootElement;
        root.ValueKind.ShouldBe(JsonValueKind.Array);
        root.GetArrayLength().ShouldBe(2);

        var ollama = root[0];
        ollama.GetProperty("id").GetString().ShouldBe("ollama");
        ollama.GetProperty("installed").GetBoolean().ShouldBeTrue();
        ollama.GetProperty("running").GetBoolean().ShouldBeTrue();
        ollama.GetProperty("managedByWorker").GetBoolean().ShouldBeFalse();
        ollama.GetProperty("supportsLifecycle").GetBoolean().ShouldBeTrue();

        var model = ollama.GetProperty("models")[0];
        model.GetProperty("name").GetString().ShouldBe("qwen3:8b");
        model.GetProperty("sizeBytes").GetInt64().ShouldBe(4_100_000_000L);
        model.GetProperty("modifiedAt").GetString().ShouldBe("2026-01-01T00:00:00Z");

        root[1].GetProperty("id").GetString().ShouldBe("lm-studio");
    }

    [Fact]
    public async Task StartLmStudio_MapsLifecycleUnsupported()
    {
        await using var fixture = await HostFixture.StartAsync(providers: BuildProviders());

        using var response = await fixture.Client.PostAsync("/providers/lm-studio/start", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        (await ReadCodeAsync(response)).ShouldBe("providers/lifecycle-unsupported");
    }

    [Fact]
    public async Task PutAutostart_ReturnsNoContent()
    {
        await using var fixture = await HostFixture.StartAsync(providers: BuildProviders());

        using var content = new StringContent("{\"enabled\":true}", Encoding.UTF8, "application/json");
        using var response = await fixture.Client.PutAsync("/providers/ollama/autostart", content);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldBeNullOrEmpty();
    }

    [Fact]
    public async Task Auth_RejectsMissingOrWrongToken_AcceptsCorrectToken()
    {
        await using var fixture = await HostFixture.StartAsync(
            providers: BuildProviders(),
            token: "s3cr3t-token",
            sendTokenByDefault: false);

        using (var missing = await fixture.Client.GetAsync("/providers"))
        {
            missing.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await ReadCodeAsync(missing)).ShouldBe("providers/request-failed");
        }

        using (var wrongRequest = new HttpRequestMessage(HttpMethod.Get, "/providers"))
        {
            wrongRequest.Headers.Add("x-scout-token", "not-the-token");
            using var wrong = await fixture.Client.SendAsync(wrongRequest);
            wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using (var okRequest = new HttpRequestMessage(HttpMethod.Get, "/providers"))
        {
            okRequest.Headers.Add("x-scout-token", "s3cr3t-token");
            using var ok = await fixture.Client.SendAsync(okRequest);
            ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task NoToken_DoesNotBindControlSurface()
    {
        Environment.SetEnvironmentVariable(TokenEnvVarName, null);
        var host = new ControlPlaneHttpHost(
            BuildProviders(),
            BuildFeatures(),
            NullLogger<ControlPlaneHttpHost>.Instance);

        try
        {
            await host.StartAsync(CancellationToken.None);
            host.Port.ShouldBe(0);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task OversizedRequestBody_MapsToPayloadTooLarge()
    {
        await using var fixture = await HostFixture.StartAsync(features: BuildFeatures());

        var oversized = new string('x', 300 * 1024);
        using var content = new StringContent(
            $"{{\"pat\":\"{oversized}\"}}", Encoding.UTF8, "application/json");
        using var response = await fixture.Client.PostAsync("/features/github/credentials", content);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task UnknownProvider_MapsToUnknownError()
    {
        await using var fixture = await HostFixture.StartAsync(providers: BuildProviders());

        using var response = await fixture.Client.GetAsync("/providers/does-not-exist/status");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).ShouldBe("providers/unknown");
    }

    [Fact]
    public async Task UnreachableBackend_MapsToUnreachableError()
    {
        // Probe never becomes ready and the daemon does not answer /api/tags, so
        // a stop must surface the runtime as unreachable, not fake success.
        var providers = BuildProviders(
            running: false,
            ready: false,
            state: OllamaReadinessState.Failed);
        await using var fixture = await HostFixture.StartAsync(providers: providers);

        using var response = await fixture.Client.PostAsync("/providers/ollama/stop", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await ReadCodeAsync(response)).ShouldBe("providers/unreachable");
    }

    [Fact]
    public async Task GetFeatures_ReturnsCatalogSnapshot()
    {
        await using var fixture = await HostFixture.StartAsync(features: BuildFeatures());

        using var response = await fixture.Client.GetAsync("/features");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var doc = await ParseAsync(response);
        doc.RootElement.ValueKind.ShouldBe(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().ShouldBe(3);

        var github = doc.RootElement.EnumerateArray()
            .First(e => e.GetProperty("id").GetString() == "github");
        github.GetProperty("requiresCredentials").GetBoolean().ShouldBeTrue();
        github.GetProperty("status").GetString().ShouldBe("needs-credentials");
        github.GetProperty("credentialFields")[0].GetProperty("key").GetString().ShouldBe("pat");
    }

    [Fact]
    public async Task EnableFeatureWithoutCredentials_MapsToCredentialsMissing()
    {
        await using var fixture = await HostFixture.StartAsync(features: BuildFeatures());

        using var response = await fixture.Client.PostAsync("/features/github/enable", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await ReadCodeAsync(response)).ShouldBe("features/credentials-missing");
    }

    [Fact]
    public async Task UnknownFeature_MapsToUnknownError()
    {
        await using var fixture = await HostFixture.StartAsync(features: BuildFeatures());

        using var response = await fixture.Client.PostAsync("/features/not-a-feature/enable", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).ShouldBe("features/unknown");
    }

    private static ProvidersService BuildProviders(
        bool installed = true,
        bool running = true,
        bool autostart = false,
        bool ready = true,
        OllamaReadinessState state = OllamaReadinessState.Ready,
        IReadOnlyList<ProviderModel>? models = null)
    {
        var locator = Substitute.For<IBinaryLocator>();
        locator.FindOnPathAsync("ollama", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(installed ? "/usr/bin/ollama" : null));

        var api = Substitute.For<IOllamaApiClient>();
        api.GetTagsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OllamaTags?>(
                running ? new OllamaTags(models ?? Array.Empty<ProviderModel>()) : null));

        var launcher = Substitute.For<IProcessLauncher>();

        var registry = Substitute.For<ILocalRuntimeRegistry>();
        registry.GetOwned("ollama").Returns((ILaunchedProcess?)null);

        var configStore = Substitute.For<ILocalRuntimeConfigStore>();
        configStore.GetAutostartAsync("ollama", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(autostart));

        var probe = Substitute.For<IOllamaReadinessProbe>();
        probe.Current.Returns(state);
        probe.WaitReadyAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ready));

        var ollama = new OllamaProviderService(
            locator,
            api,
            launcher,
            registry,
            configStore,
            Options.Create(new OllamaProviderOptions()),
            probe,
            NullLogger<OllamaProviderService>.Instance);

        return new ProvidersService(ollama);
    }

    private static FeaturesService BuildFeatures()
    {
        var stateStore = Substitute.For<IFeatureStateStore>();
        stateStore.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, FeatureState>>(
                new Dictionary<string, FeatureState>(StringComparer.Ordinal)));
        stateStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(FeatureStates.EmptyDisabled()));

        return new FeaturesService(
            new FeatureCatalog(),
            stateStore,
            new InMemoryCredentialStore(),
            new NullFeatureActivator(),
            Array.Empty<IFeatureCredentialValidator>());
    }

    private static async Task<JsonDocument> ParseAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body);
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using var doc = await ParseAsync(response);
        return doc.RootElement.GetProperty("code").GetString();
    }

    private sealed class HostFixture : IAsyncDisposable
    {
        private readonly ControlPlaneHttpHost _host;

        private HostFixture(ControlPlaneHttpHost host, HttpClient client)
        {
            _host = host;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<HostFixture> StartAsync(
            ProvidersService? providers = null,
            FeaturesService? features = null,
            string token = "test-token",
            bool sendTokenByDefault = true)
        {
            // The surface fails closed without a token, so every fixture serves
            // an authenticated host by default. The auth test opts out of the
            // default header to drive the missing/wrong/correct cases itself.
            Environment.SetEnvironmentVariable(TokenEnvVarName, token);

            var host = new ControlPlaneHttpHost(
                providers ?? BuildProviders(),
                features ?? BuildFeatures(),
                NullLogger<ControlPlaneHttpHost>.Instance);

            await host.StartAsync(CancellationToken.None);

            var client = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{host.Port}/"),
            };
            if (sendTokenByDefault)
            {
                client.DefaultRequestHeaders.Add("x-scout-token", token);
            }

            return new HostFixture(host, client);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _host.StopAsync(CancellationToken.None);
            Environment.SetEnvironmentVariable(TokenEnvVarName, null);
        }
    }
}
