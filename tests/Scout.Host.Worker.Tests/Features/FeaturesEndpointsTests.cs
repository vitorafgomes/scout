using Scout.Core.Features;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scout.Host.Worker.Features;
using Scout.Host.Worker.Providers;

namespace Scout.Host.Worker.Tests.Features;

public sealed class FeaturesEndpointsTests : IDisposable
{
    private readonly string _stateFile;
    private readonly InMemoryCredentialStore _credentials = new(isAvailable: true);
    private readonly ScoutWorkerWebApplicationFactory _factory;

    public FeaturesEndpointsTests()
    {
        _stateFile = Path.GetTempFileName();
        File.Delete(_stateFile);

        _factory = new ScoutWorkerWebApplicationFactory
        {
            SwapFeatureStores = true,
            ConfigureServicesOverride = services =>
            {
                // Stand-ins for the providers wiring that ConfigureServicesOverride
                // strips. The endpoint tests under /features never call into these.
                services.AddSingleton(Substitute.For<IBinaryLocator>());
                services.AddSingleton(Substitute.For<IOllamaApiClient>());
                services.AddSingleton(Substitute.For<IProcessLauncher>());
                services.AddSingleton<ILocalRuntimeRegistry>(new InMemoryLocalRuntimeRegistry());
                services.AddSingleton(Substitute.For<ILocalRuntimeConfigStore>());

                services.AddSingleton<ICredentialStore>(_credentials);
                services.AddSingleton<IFeatureStateStore>(_ =>
                    new JsonFileFeatureStateStore(
                        Options.Create(new FeatureStateStoreOptions { FilePath = _stateFile }),
                        NullLogger<JsonFileFeatureStateStore>.Instance));
            },
        };
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { File.Delete(_stateFile); } catch { /* best-effort */ }
        try { File.Delete(_stateFile + ".tmp"); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task List_ReturnsBothFeatures_NeedsCredentialsStatus()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/features/");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var dtos = await response.Content.ReadFromJsonAsync<FeatureDto[]>();
        dtos.ShouldNotBeNull();
        dtos.Length.ShouldBe(2);
        dtos.ShouldContain(d => d.Id == "github" && d.Status == "needs-credentials");
        dtos.ShouldContain(d => d.Id == "kubernetes" && d.Status == "needs-credentials");

        // Schema-driven dialog inputs travel on every list response so
        // the UI never has to special-case feature ids.
        var github = dtos.Single(d => d.Id == "github");
        github.CredentialFields.ShouldNotBeEmpty();
        github.ScopeMatrix.ShouldNotBeNull();
    }

    [Fact]
    public async Task GET_features_includes_credential_fields_and_scope_matrix_for_github()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/features/");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var dtos = await response.Content.ReadFromJsonAsync<FeatureDto[]>();
        dtos.ShouldNotBeNull();

        var github = dtos.Single(d => d.Id == "github");

        github.CredentialFields.Count.ShouldBe(1);
        var pat = github.CredentialFields[0];
        pat.Key.ShouldBe("pat");
        pat.Kind.ShouldBe("password");
        pat.CreateLinkUrl.ShouldBe(
            "https://github.com/settings/tokens/new?scopes=repo,read:org&description=Scout");

        github.ScopeMatrix.ShouldNotBeNull();
        github.ScopeMatrix!.Count.ShouldBe(4);
        github.ScopeMatrix.Select(r => r.Scope).ShouldBe(
            new[] { "repo", "public_repo", "read:org", "read:user" });
    }

    [Fact]
    public async Task GET_features_includes_setup_steps_for_github()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/features/");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var dtos = await response.Content.ReadFromJsonAsync<FeatureDto[]>();
        dtos.ShouldNotBeNull();

        var github = dtos.Single(d => d.Id == "github");

        github.SetupSteps.ShouldNotBeNull();
        github.SetupSteps!.Count.ShouldBe(4);
        github.SetupSteps[0].ShouldStartWith("Click 'Create token on GitHub'");
    }

    [Fact]
    public async Task SetCredentials_EmptyBody_Returns400()
    {
        using var client = _factory.CreateClient();
        using var content = JsonContent.Create(new Dictionary<string, string>());
        var response = await client.PostAsync("/features/github/credentials", content);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var payload = await response.Content.ReadAsStringAsync();
        payload.ShouldContain("features/invalid-credential-body");
    }

    [Fact]
    public async Task SetCredentials_UnknownId_Returns404()
    {
        using var client = _factory.CreateClient();
        using var content = JsonContent.Create(new Dictionary<string, string> { ["pat"] = "x" });
        var response = await client.PostAsync("/features/unknown-feat/credentials", content);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var payload = await response.Content.ReadAsStringAsync();
        payload.ShouldContain("features/unknown");
    }

    [Fact]
    public async Task Enable_WithoutCredentials_Returns412()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsync("/features/github/enable", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        var payload = await response.Content.ReadAsStringAsync();
        payload.ShouldContain("features/credentials-missing");
    }

    [Fact]
    public async Task Enable_WithCredentials_NoFactory_Returns501()
    {
        // Seed credentials directly so we hit the "no factory" branch.
        await _credentials.StoreAsync("scout/github/pat", "x", CancellationToken.None);

        // Real GitHub/Kubernetes plugins now register factories at app
        // startup; strip them here so we still exercise the 501 branch
        // for the case where a feature has no factory wired yet.
        using var stripped = _factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            for (var i = s.Count - 1; i >= 0; i--)
            {
                if (s[i].ServiceType == typeof(FeaturePluginFactoryRegistration))
                {
                    s.RemoveAt(i);
                }
            }
        }));
        using var client = stripped.CreateClient();
        var response = await client.PostAsync("/features/github/enable", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        var payload = await response.Content.ReadAsStringAsync();
        payload.ShouldContain("features/not-yet-implemented");
    }

    [Fact]
    public async Task Disable_UnknownId_Returns404()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsync("/features/unknown-feat/disable", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Disable_Known_Returns200_AndPersistsDisabled()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsync("/features/github/disable", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<FeatureDto>();
        dto.ShouldNotBeNull();
        dto.Enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task DeleteCredentials_RemovesStoredSecrets()
    {
        await _credentials.StoreAsync("scout/github/pat", "x", CancellationToken.None);

        using var client = _factory.CreateClient();
        var response = await client.DeleteAsync("/features/github/credentials");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var present = await _credentials.ContainsAsync(
            "scout/github/pat",
            CancellationToken.None);
        present.ShouldBeFalse();
    }

    [Fact]
    public async Task AddFeaturePlugin_Registers_PluginInFactoryRegistry()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IServiceProvider>(_ => services.BuildServiceProvider());
        FeaturePluginServiceCollectionExtensions.AddFeaturePlugin<NoopPlugin>(services, "noop");

        var provider = services.BuildServiceProvider();
        var registrations = provider.GetServices<FeaturePluginFactoryRegistration>().ToList();
        registrations.ShouldContain(r => r.FeatureId == "noop" && r.PluginType == typeof(NoopPlugin));
    }

    private sealed class NoopPlugin : IFeaturePlugin
    {
        public string FeatureId => "noop";
        public string KernelPluginName => "Noop";
        public Task<Microsoft.SemanticKernel.KernelPlugin> BuildAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Microsoft.SemanticKernel.KernelPluginFactory.CreateFromFunctions("Noop"));
    }
}
