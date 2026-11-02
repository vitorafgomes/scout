using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
            .ShouldBe(new[] { FeatureCatalog.GitHubId, FeatureCatalog.KubernetesId, FeatureCatalog.YouTubeId });
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
            Build().EnableAsync(FeatureCatalog.GitHubId, confirmWrites: false, CancellationToken.None));
    }

    [Fact]
    public async Task EnableAsync_NoPluginWired_ThrowsNotImplemented()
    {
        _credentials.ContainsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        _activator.CanActivate(FeatureCatalog.GitHubId).Returns(false);

        await Should.ThrowAsync<FeatureNotImplementedException>(() =>
            Build().EnableAsync(FeatureCatalog.GitHubId, confirmWrites: false, CancellationToken.None));
    }

    [Fact]
    public async Task EnableAsync_Activatable_PersistsEnabledState()
    {
        _credentials.ContainsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        _activator.CanActivate(FeatureCatalog.GitHubId).Returns(true);

        await Build().EnableAsync(FeatureCatalog.GitHubId, confirmWrites: false, CancellationToken.None);

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
            Build().EnableAsync(FeatureCatalog.GitHubId, confirmWrites: false, CancellationToken.None));

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

    private const string YouTubeWriteRows =
        "Scout's data folder (write), videos.insert (irreversible write), videos.update (irreversible write), " +
        "thumbnails.set (irreversible write), playlistItems.insert (irreversible write), " +
        "comments.insert (irreversible write), comments.setModerationStatus (irreversible write).";

    private static async Task WithFileStoreAsync(Func<JsonFileFeatureStateStore, FeaturesService, Task> test)
    {
        var directory = Directory.CreateTempSubdirectory("scout-grants-").FullName;
        try
        {
            using var store = new JsonFileFeatureStateStore(
                Options.Create(new FeatureStateStoreOptions { FilePath = Path.Combine(directory, "features.json") }),
                NullLogger<JsonFileFeatureStateStore>.Instance);
            var credentials = new InMemoryCredentialStore(isAvailable: true);
            await credentials.StoreAsync("scout/youtube/api_key", "yt-key", CancellationToken.None);
            var activator = Substitute.For<IFeatureActivator>();
            activator.CanActivate(Arg.Any<string>()).Returns(true);
            var service = new FeaturesService(new FeatureCatalog(), store, credentials, activator, []);
            await test(store, service);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public Task Enabling_A_Write_Capable_Feature_Needs_The_Write_Confirmation() =>
        WithFileStoreAsync(async (store, service) =>
        {
            var ex = await Should.ThrowAsync<WriteConfirmationRequiredException>(() =>
                service.EnableAsync(FeatureCatalog.YouTubeId, confirmWrites: false, CancellationToken.None));

            ex.FeatureId.ShouldBe(FeatureCatalog.YouTubeId);
            ex.Message.ShouldBe("Confirm the writes of youtube before enabling it: " + YouTubeWriteRows);
            File.Exists(store.FilePath).ShouldBeFalse();
            var snapshot = (await service.ListAsync(CancellationToken.None)).Single(f => f.Id == FeatureCatalog.YouTubeId);
            snapshot.Enabled.ShouldBeFalse();
            snapshot.Granted.ShouldBeFalse();
        });

    [Fact]
    public Task Confirming_The_Writes_Records_The_Grant() =>
        WithFileStoreAsync(async (store, service) =>
        {
            var before = DateTimeOffset.UtcNow;

            var snapshot = await service.EnableAsync(FeatureCatalog.YouTubeId, confirmWrites: true, CancellationToken.None);

            var after = DateTimeOffset.UtcNow;
            var persisted = await store.GetAsync(FeatureCatalog.YouTubeId, CancellationToken.None);
            persisted.Enabled.ShouldBeTrue();
            persisted.Granted.ShouldBeTrue();
            persisted.GrantedAt.ShouldNotBeNull().ShouldBeInRange(before, after);
            snapshot.Granted.ShouldBeTrue();
            snapshot.GrantedAt.ShouldBe(persisted.GrantedAt);
            snapshot.WriteClass.ShouldBe(WriteClass.Executive);
        });

    [Fact]
    public Task A_Read_Only_Feature_Enables_Without_Confirmation() =>
        WithFileStoreAsync(async (store, service) =>
        {
            var snapshot = await service.EnableAsync(FeatureCatalog.KubernetesId, confirmWrites: false, CancellationToken.None);

            snapshot.Enabled.ShouldBeTrue();
            snapshot.Granted.ShouldBeFalse();
            snapshot.GrantedAt.ShouldBeNull();
            var persisted = await store.GetAsync(FeatureCatalog.KubernetesId, CancellationToken.None);
            persisted.Enabled.ShouldBeTrue();
            persisted.Granted.ShouldBeFalse();
        });

    [Fact]
    public Task Disable_Revokes_The_Grant() =>
        WithFileStoreAsync(async (store, service) =>
        {
            await service.EnableAsync(FeatureCatalog.YouTubeId, confirmWrites: true, CancellationToken.None);

            var snapshot = await service.DisableAsync(FeatureCatalog.YouTubeId, CancellationToken.None);

            snapshot.Granted.ShouldBeFalse();
            snapshot.GrantedAt.ShouldBeNull();
            var persisted = await store.GetAsync(FeatureCatalog.YouTubeId, CancellationToken.None);
            persisted.Enabled.ShouldBeFalse();
            persisted.Granted.ShouldBeFalse();
            persisted.GrantedAt.ShouldBeNull();
        });

    [Fact]
    public Task Deleting_The_Credentials_Revokes_The_Grant() =>
        WithFileStoreAsync(async (store, service) =>
        {
            await service.SetWriteGrantAsync(FeatureCatalog.YouTubeId, granted: true, confirmWrites: true, CancellationToken.None);

            var snapshot = await service.DeleteCredentialsAsync(FeatureCatalog.YouTubeId, CancellationToken.None);

            snapshot.Granted.ShouldBeFalse();
            var persisted = await store.GetAsync(FeatureCatalog.YouTubeId, CancellationToken.None);
            persisted.Granted.ShouldBeFalse();
            persisted.GrantedAt.ShouldBeNull();
        });

    [Fact]
    public Task Granting_Writes_Needs_The_Confirmation_And_Records_The_Grant() =>
        WithFileStoreAsync(async (store, service) =>
        {
            var refused = await Should.ThrowAsync<WriteConfirmationRequiredException>(() =>
                service.SetWriteGrantAsync(FeatureCatalog.YouTubeId, granted: true, confirmWrites: false, CancellationToken.None));
            var notYet = await store.GetAsync(FeatureCatalog.YouTubeId, CancellationToken.None);
            var before = DateTimeOffset.UtcNow;

            var snapshot = await service.SetWriteGrantAsync(
                FeatureCatalog.YouTubeId, granted: true, confirmWrites: true, CancellationToken.None);

            var after = DateTimeOffset.UtcNow;
            refused.Message.ShouldBe("Confirm the writes of youtube before granting them: " + YouTubeWriteRows);
            notYet.Granted.ShouldBeFalse();
            var persisted = await store.GetAsync(FeatureCatalog.YouTubeId, CancellationToken.None);
            persisted.Granted.ShouldBeTrue();
            persisted.GrantedAt.ShouldNotBeNull().ShouldBeInRange(before, after);
            persisted.Enabled.ShouldBeFalse();
            snapshot.Granted.ShouldBeTrue();
            snapshot.GrantedAt.ShouldBe(persisted.GrantedAt);
        });

    [Fact]
    public Task Revoking_Clears_The_Grant() =>
        WithFileStoreAsync(async (store, service) =>
        {
            await service.SetWriteGrantAsync(FeatureCatalog.YouTubeId, granted: true, confirmWrites: true, CancellationToken.None);

            var snapshot = await service.SetWriteGrantAsync(
                FeatureCatalog.YouTubeId, granted: false, confirmWrites: false, CancellationToken.None);

            snapshot.Granted.ShouldBeFalse();
            snapshot.GrantedAt.ShouldBeNull();
            var persisted = await store.GetAsync(FeatureCatalog.YouTubeId, CancellationToken.None);
            persisted.Granted.ShouldBeFalse();
            persisted.GrantedAt.ShouldBeNull();
        });

    [Fact]
    public Task Granting_A_Feature_Without_Write_Rows_Fails() =>
        WithFileStoreAsync(async (store, service) =>
        {
            var ex = await Should.ThrowAsync<FeatureValidationFailedException>(() =>
                service.SetWriteGrantAsync(FeatureCatalog.GitHubId, granted: true, confirmWrites: true, CancellationToken.None));

            ex.Errors.ShouldBe(new Dictionary<string, string> { ["granted"] = "github declares no writes to grant." });
            File.Exists(store.FilePath).ShouldBeFalse();
        });

    [Fact]
    public Task A_Failed_Activation_Leaves_No_Grant() =>
        WithFileStoreAsync(async (store, _) =>
        {
            await store.SetAsync(
                FeatureCatalog.YouTubeId,
                FeatureStates.EmptyDisabled() with { Granted = true, GrantedAt = DateTimeOffset.UtcNow },
                CancellationToken.None);
            var credentials = new InMemoryCredentialStore(isAvailable: true);
            await credentials.StoreAsync("scout/youtube/api_key", "yt-key", CancellationToken.None);
            var activator = Substitute.For<IFeatureActivator>();
            activator.CanActivate(Arg.Any<string>()).Returns(true);
            activator.ActivateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns<Task>(_ => throw new FeatureActivationException("boom"));
            var service = new FeaturesService(new FeatureCatalog(), store, credentials, activator, []);

            await Should.ThrowAsync<FeatureActivationException>(() =>
                service.EnableAsync(FeatureCatalog.YouTubeId, confirmWrites: true, CancellationToken.None));

            var persisted = await store.GetAsync(FeatureCatalog.YouTubeId, CancellationToken.None);
            persisted.LastError.ShouldBe("boom");
            persisted.Granted.ShouldBeFalse();
            persisted.GrantedAt.ShouldBeNull();
        });

    [Fact]
    public async Task With_The_Default_Activator_A_Confirmed_Enable_Still_Ends_In_Not_Implemented()
    {
        _credentials.ContainsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        var service = new FeaturesService(_catalog, _store, _credentials, new NullFeatureActivator(), []);

        await Should.ThrowAsync<FeatureNotImplementedException>(() =>
            service.EnableAsync(FeatureCatalog.YouTubeId, confirmWrites: true, CancellationToken.None));
    }
}
