using NSubstitute;
using Scout.ControlPlane.Features;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Features;

/// <summary>
/// Unit coverage for the <see cref="FeaturesService"/> domain facade over the
/// real <see cref="FeatureCatalog"/> with substituted persistence/activation.
/// </summary>
public sealed class FeaturesServiceTests
{
    private readonly IFeatureCatalog _catalog = new FeatureCatalog();
    private readonly IFeatureStateStore _store = Substitute.For<IFeatureStateStore>();
    private readonly ICredentialStore _credentials = Substitute.For<ICredentialStore>();
    private readonly IFeatureActivator _activator = Substitute.For<IFeatureActivator>();

    public FeaturesServiceTests()
    {
        _store.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(FeatureStates.EmptyDisabled()));
        _store.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, FeatureState>>(
                new Dictionary<string, FeatureState>(StringComparer.Ordinal)));
        _credentials.StoreAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        _credentials.ContainsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));
    }

    private FeaturesService Build() => new(
        _catalog,
        _store,
        _credentials,
        _activator,
        Array.Empty<IFeatureCredentialValidator>());

    [Fact]
    public async Task ListAsync_ReturnsEveryCatalogFeature()
    {
        var snapshots = await Build().ListAsync(CancellationToken.None);

        snapshots.Select(s => s.Id)
            .ShouldBe(new[] { FeatureCatalog.GitHubId, FeatureCatalog.KubernetesId });
    }

    [Fact]
    public async Task SetCredentialsAsync_UnknownFeature_Throws()
    {
        await Should.ThrowAsync<FeatureUnknownException>(() =>
            Build().SetCredentialsAsync(
                "nope",
                new Dictionary<string, string>(),
                CancellationToken.None));
    }

    [Fact]
    public async Task SetCredentialsAsync_MissingRequiredField_ThrowsValidation()
    {
        var ex = await Should.ThrowAsync<FeatureValidationFailedException>(() =>
            Build().SetCredentialsAsync(
                FeatureCatalog.GitHubId,
                new Dictionary<string, string>(StringComparer.Ordinal),
                CancellationToken.None));

        ex.Errors.Keys.ShouldContain("credentials.pat");
    }

    [Fact]
    public async Task SetCredentialsAsync_Valid_PersistsAndReportsHasCredentials()
    {
        _credentials.ContainsAsync("scout/github/pat", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var snapshot = await Build().SetCredentialsAsync(
            FeatureCatalog.GitHubId,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["pat"] = "ghp_x" },
            CancellationToken.None);

        await _credentials.Received(1)
            .StoreAsync("scout/github/pat", "ghp_x", Arg.Any<CancellationToken>());
        snapshot.HasCredentials.ShouldBeTrue();
    }

    [Fact]
    public async Task EnableAsync_MissingCredentials_Throws()
    {
        await Should.ThrowAsync<FeatureCredentialsMissingException>(() =>
            Build().EnableAsync(FeatureCatalog.GitHubId, CancellationToken.None));
    }

    [Fact]
    public async Task EnableAsync_NoPluginWired_ThrowsNotImplemented()
    {
        _credentials.ContainsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        _activator.CanActivate(FeatureCatalog.GitHubId).Returns(false);

        await Should.ThrowAsync<FeatureNotImplementedException>(() =>
            Build().EnableAsync(FeatureCatalog.GitHubId, CancellationToken.None));
    }

    [Fact]
    public async Task EnableAsync_Activatable_PersistsEnabledState()
    {
        _credentials.ContainsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        _activator.CanActivate(FeatureCatalog.GitHubId).Returns(true);

        await Build().EnableAsync(FeatureCatalog.GitHubId, CancellationToken.None);

        await _activator.Received(1).ActivateAsync(FeatureCatalog.GitHubId, Arg.Any<CancellationToken>());
        await _store.Received().SetAsync(
            FeatureCatalog.GitHubId,
            Arg.Is<FeatureState>(s => s.Enabled && s.LastError == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnableAsync_ActivationThrows_PersistsErrorAndRethrows()
    {
        _credentials.ContainsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        _activator.CanActivate(FeatureCatalog.GitHubId).Returns(true);
        _activator.ActivateAsync(FeatureCatalog.GitHubId, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new FeatureActivationException("boom"));

        await Should.ThrowAsync<FeatureActivationException>(() =>
            Build().EnableAsync(FeatureCatalog.GitHubId, CancellationToken.None));

        await _store.Received().SetAsync(
            FeatureCatalog.GitHubId,
            Arg.Is<FeatureState>(s => !s.Enabled && s.LastError == "boom"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisableAsync_DeactivatesAndClearsState()
    {
        _activator.IsActive(FeatureCatalog.GitHubId).Returns(true);

        var snapshot = await Build().DisableAsync(FeatureCatalog.GitHubId, CancellationToken.None);

        await _activator.Received(1).DeactivateAsync(FeatureCatalog.GitHubId, Arg.Any<CancellationToken>());
        await _store.Received().SetAsync(
            FeatureCatalog.GitHubId,
            Arg.Is<FeatureState>(s => !s.Enabled),
            Arg.Any<CancellationToken>());
        snapshot.Enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task DeleteCredentialsAsync_RemovesSecretsAndDisables()
    {
        _activator.IsActive(FeatureCatalog.GitHubId).Returns(true);

        await Build().DeleteCredentialsAsync(FeatureCatalog.GitHubId, CancellationToken.None);

        await _credentials.Received().DeleteAsync("scout/github/pat", Arg.Any<CancellationToken>());
        await _activator.Received(1).DeactivateAsync(FeatureCatalog.GitHubId, Arg.Any<CancellationToken>());
        await _store.Received().SetAsync(
            FeatureCatalog.GitHubId,
            Arg.Is<FeatureState>(s => !s.Enabled),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetupAsync_MissingRequiredSetting_ThrowsValidation()
    {
        // GitHub requires the 'default_repo_url' setting and the 'pat' credential.
        var input = new FeatureSetupInput(
            Credentials: new Dictionary<string, string>(StringComparer.Ordinal) { ["pat"] = "ghp_x" },
            Settings: new Dictionary<string, string?>(StringComparer.Ordinal));

        var ex = await Should.ThrowAsync<FeatureValidationFailedException>(() =>
            Build().SetupAsync(FeatureCatalog.GitHubId, input, CancellationToken.None));

        ex.Errors.Keys.ShouldContain("settings.default_repo_url");
    }
}
