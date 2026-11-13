using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Scout.ControlPlane.Features;
using Scout.ControlPlane.Tests.Integration;
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
            .ShouldBe(new[] { FeatureCatalog.GitHubId, FeatureCatalog.KubernetesId, FeatureCatalog.YouTubeId, FeatureCatalog.DesktopId });
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
        _activator.CanActivate(FeatureCatalog.KubernetesId).Returns(false);

        await Should.ThrowAsync<FeatureNotImplementedException>(() =>
            Build().EnableAsync(FeatureCatalog.KubernetesId, confirmWrites: false, CancellationToken.None));
    }

    [Fact]
    public async Task EnableAsync_Activatable_PersistsEnabledState()
    {
        _credentials.ContainsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        _activator.CanActivate(FeatureCatalog.KubernetesId).Returns(true);

        await Build().EnableAsync(FeatureCatalog.KubernetesId, confirmWrites: false, CancellationToken.None);

        await _activator.Received(1).ActivateAsync(FeatureCatalog.KubernetesId, Arg.Any<CancellationToken>());
        await _store.Received().SetAsync(
            FeatureCatalog.KubernetesId,
            Arg.Is<FeatureState>(s => s.Enabled && s.LastError == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnableAsync_ActivationThrows_PersistsErrorAndRethrows()
    {
        _credentials.ContainsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        _activator.CanActivate(FeatureCatalog.KubernetesId).Returns(true);
        _activator.ActivateAsync(FeatureCatalog.KubernetesId, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new FeatureActivationException("boom"));

        await Should.ThrowAsync<FeatureActivationException>(() =>
            Build().EnableAsync(FeatureCatalog.KubernetesId, confirmWrites: false, CancellationToken.None));

        await _store.Received().SetAsync(
            FeatureCatalog.KubernetesId,
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
        "Scout's data folder (write), youtubeReporting jobs.create (write), videos.insert (irreversible write), videos.update (irreversible write), " +
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
                service.SetWriteGrantAsync(FeatureCatalog.KubernetesId, granted: true, confirmWrites: true, CancellationToken.None));

            ex.Errors.ShouldBe(new Dictionary<string, string> { ["granted"] = "kubernetes declares no writes to grant." });
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

    [Fact]
    public Task Granting_A_Feature_Already_Granted_Keeps_The_Time_The_Grant_Began() =>
        WithFileStoreAsync(async (store, service) =>
        {
            await service.SetWriteGrantAsync(FeatureCatalog.YouTubeId, granted: true, confirmWrites: true, CancellationToken.None);
            var first = (await store.GetAsync(FeatureCatalog.YouTubeId, CancellationToken.None)).GrantedAt;
            await Task.Delay(20);

            await service.SetWriteGrantAsync(FeatureCatalog.YouTubeId, granted: true, confirmWrites: true, CancellationToken.None);
            var kept = (await store.GetAsync(FeatureCatalog.YouTubeId, CancellationToken.None)).GrantedAt;
            await service.SetWriteGrantAsync(FeatureCatalog.YouTubeId, granted: false, confirmWrites: false, CancellationToken.None);
            await service.SetWriteGrantAsync(FeatureCatalog.YouTubeId, granted: true, confirmWrites: true, CancellationToken.None);
            var renewed = (await store.GetAsync(FeatureCatalog.YouTubeId, CancellationToken.None)).GrantedAt;

            kept.ShouldBe(first);
            renewed.ShouldNotBeNull().ShouldBeGreaterThan(first.ShouldNotBeNull());
        });

    private const string RepoUrl = "https://github.com/octocat/Hello-World";

    private static async Task WithGitHubAsync(Func<JsonFileFeatureStateStore, ICredentialStore, FeaturesService, Task> test, ICredentialStore? credentials = null)
    {
        var directory = Directory.CreateTempSubdirectory("scout-setup-").FullName;
        try
        {
            using var store = new JsonFileFeatureStateStore(
                Options.Create(new FeatureStateStoreOptions { FilePath = Path.Combine(directory, "features.json") }),
                NullLogger<JsonFileFeatureStateStore>.Instance);
            credentials ??= new InMemoryCredentialStore(isAvailable: true);
            var service = new FeaturesService(new FeatureCatalog(), store, credentials, new StubFeatureActivator(), []);
            await test(store, credentials, service);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static FeatureSetupInput Setup(Dictionary<string, string>? credentials = null, Dictionary<string, string?>? settings = null) =>
        new(credentials ?? new Dictionary<string, string>(StringComparer.Ordinal), settings ?? new Dictionary<string, string?>(StringComparer.Ordinal));

    [Theory]
    [InlineData("SetCredentials")]
    [InlineData("Setup")]
    public async Task SetCredentials_Fails_As_Not_Persisted_When_The_Credential_Store_Rejects_The_Secret(string caller)
    {
        _credentials.StoreAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));
        var service = Build();
        var secret = new Dictionary<string, string>(StringComparer.Ordinal) { ["pat"] = "ghp_x" };

        var failed = await Should.ThrowAsync<FeaturePersistFailedException>(() => caller == "Setup"
            ? service.SetupAsync(
                FeatureCatalog.GitHubId,
                Setup(secret, new(StringComparer.Ordinal) { ["default_repo_url"] = RepoUrl }),
                CancellationToken.None)
            : service.SetCredentialsAsync(FeatureCatalog.GitHubId, secret, CancellationToken.None));

        failed.Message.ShouldBe("Credential store is unavailable; no changes were saved.");
        await _store.DidNotReceive().SetAsync(Arg.Any<string>(), Arg.Any<FeatureState>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public Task The_Default_In_Memory_Credential_Store_Answers_Saved_While_Unavailable_Because_Setup_Then_Enable_Runs_On_It() =>
        WithGitHubAsync(
            async (_, credentials, service) =>
            {
                await service.SetupAsync(
                    FeatureCatalog.GitHubId,
                    Setup(new(StringComparer.Ordinal) { ["pat"] = "ghp_first" }, new(StringComparer.Ordinal) { ["default_repo_url"] = RepoUrl }),
                    CancellationToken.None);
                var enabled = await service.EnableAsync(FeatureCatalog.GitHubId, confirmWrites: true, CancellationToken.None);

                credentials.IsAvailable.ShouldBeFalse();
                (await credentials.StoreAsync("scout/github/probe", "kept for the process", CancellationToken.None)).ShouldBeTrue();
                (await credentials.GetAsync("scout/github/pat", CancellationToken.None)).ShouldBe("ghp_first");
                (enabled.Enabled, enabled.HasCredentials).ShouldBe((true, true));
            },
            new InMemoryCredentialStore());

    [Fact]
    public Task Setup_Accepts_A_Partial_Update_Against_The_Stored_Credential_And_The_Persisted_Setting() =>
        WithGitHubAsync(async (store, credentials, service) =>
        {
            await service.SetupAsync(
                FeatureCatalog.GitHubId,
                Setup(new(StringComparer.Ordinal) { ["pat"] = "ghp_first" }, new(StringComparer.Ordinal) { ["default_repo_url"] = RepoUrl }),
                CancellationToken.None);

            await Should.NotThrowAsync(() => service.SetupAsync(
                FeatureCatalog.GitHubId,
                Setup(settings: new(StringComparer.Ordinal) { ["default_repo_url"] = RepoUrl + "-2" }),
                CancellationToken.None));
            await Should.NotThrowAsync(() => service.SetupAsync(
                FeatureCatalog.GitHubId,
                Setup(new(StringComparer.Ordinal) { ["pat"] = "ghp_rotated" }),
                CancellationToken.None));

            var snapshot = (await service.ListAsync(CancellationToken.None)).Single(f => f.Id == FeatureCatalog.GitHubId);
            snapshot.Settings["default_repo_url"].ShouldBe(RepoUrl + "-2");
            snapshot.HasCredentials.ShouldBeTrue();
            (await credentials.GetAsync("scout/github/pat", CancellationToken.None)).ShouldBe("ghp_rotated");
            (await store.GetAsync(FeatureCatalog.GitHubId, CancellationToken.None)).Settings["default_repo_url"].ShouldBe(RepoUrl + "-2");
        });

    [Fact]
    public Task Setup_Still_Refuses_A_Required_Field_Neither_Supplied_Nor_Stored_Or_A_Required_Setting_It_Clears() =>
        WithGitHubAsync(async (store, _, service) =>
        {
            var nothingStored = await Should.ThrowAsync<FeatureValidationFailedException>(() => service.SetupAsync(
                FeatureCatalog.GitHubId,
                Setup(settings: new(StringComparer.Ordinal) { ["default_repo_url"] = RepoUrl }),
                CancellationToken.None));
            await service.SetupAsync(
                FeatureCatalog.GitHubId,
                Setup(new(StringComparer.Ordinal) { ["pat"] = "ghp_first" }, new(StringComparer.Ordinal) { ["default_repo_url"] = RepoUrl }),
                CancellationToken.None);
            var cleared = await Should.ThrowAsync<FeatureValidationFailedException>(() => service.SetupAsync(
                FeatureCatalog.GitHubId,
                Setup(settings: new(StringComparer.Ordinal) { ["default_repo_url"] = null }),
                CancellationToken.None));

            nothingStored.Errors.ShouldBe(new Dictionary<string, string> { ["credentials.pat"] = "Missing required credential 'pat'." });
            cleared.Errors.ShouldBe(new Dictionary<string, string> { ["settings.default_repo_url"] = "Missing required setting 'default_repo_url'." });
            (await store.GetAsync(FeatureCatalog.GitHubId, CancellationToken.None)).Settings["default_repo_url"].ShouldBe(RepoUrl);
        });

    [Theory]
    [InlineData("theme")]
    [InlineData(null)]
    public Task Setup_Refuses_A_Setting_The_Feature_Does_Not_Declare_And_Persists_Nothing(string? value) =>
        WithGitHubAsync(async (store, credentials, service) =>
        {
            var refused = await Should.ThrowAsync<FeatureValidationFailedException>(() => service.SetupAsync(
                FeatureCatalog.GitHubId,
                Setup(
                    new(StringComparer.Ordinal) { ["pat"] = "ghp_first" },
                    new(StringComparer.Ordinal) { ["default_repo_url"] = RepoUrl, ["colour"] = value }),
                CancellationToken.None));

            refused.Errors.ShouldBe(new Dictionary<string, string> { ["settings.colour"] = "Unknown setting 'colour'." });
            File.Exists(store.FilePath).ShouldBeFalse();
            (await credentials.ContainsAsync("scout/github/pat", CancellationToken.None)).ShouldBeFalse();
        });

    [Fact]
    public async Task Setup_Whose_Stored_Credential_Check_Fails_Propagates_The_Failure_And_Persists_Nothing()
    {
        var credentials = Substitute.For<ICredentialStore>();
        credentials.ContainsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(new InvalidOperationException("The keyring is locked.")));

        await WithGitHubAsync(
            async (store, _, service) =>
            {
                var failed = await Should.ThrowAsync<InvalidOperationException>(() => service.SetupAsync(
                    FeatureCatalog.GitHubId,
                    Setup(settings: new(StringComparer.Ordinal) { ["default_repo_url"] = RepoUrl }),
                    CancellationToken.None));

                failed.Message.ShouldBe("The keyring is locked.");
                File.Exists(store.FilePath).ShouldBeFalse();
            },
            credentials);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    public async Task An_Enable_Whose_State_Cannot_Be_Saved_After_Activation_Leaves_The_Plugin_Inactive(string failing)
    {
        var activator = new StubFeatureActivator();
        var store = Substitute.For<IFeatureStateStore>();
        store.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => failing == "read"
                ? throw new IOException("The state file is locked.")
                : Task.FromResult(FeatureStates.EmptyDisabled()));
        store.SetAsync(Arg.Any<string>(), Arg.Any<FeatureState>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new IOException("The state file is locked."));
        var service = new FeaturesService(_catalog, store, _credentials, activator, []);

        var failed = await Should.ThrowAsync<IOException>(() =>
            service.EnableAsync(FeatureCatalog.KubernetesId, confirmWrites: false, CancellationToken.None));

        failed.Message.ShouldBe("The state file is locked.");
        activator.IsActive(FeatureCatalog.KubernetesId).ShouldBeFalse();
    }

    [Fact]
    public async Task An_Enable_Again_Whose_State_Cannot_Be_Saved_Leaves_An_Already_Active_Plugin_Active()
    {
        var activator = new StubFeatureActivator();
        await activator.ActivateAsync(FeatureCatalog.KubernetesId, CancellationToken.None);
        var store = Substitute.For<IFeatureStateStore>();
        store.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(FeatureStates.EmptyDisabled() with { Enabled = true }));
        store.SetAsync(Arg.Any<string>(), Arg.Any<FeatureState>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new IOException("The state file is locked."));
        var service = new FeaturesService(_catalog, store, _credentials, activator, []);

        await Should.ThrowAsync<IOException>(() =>
            service.EnableAsync(FeatureCatalog.KubernetesId, confirmWrites: false, CancellationToken.None));

        activator.IsActive(FeatureCatalog.KubernetesId).ShouldBeTrue();
    }

    [Fact]
    public async Task An_Enable_Whose_State_Save_And_Deactivation_Both_Fail_Reports_The_State_Failure()
    {
        var activator = Substitute.For<IFeatureActivator>();
        activator.CanActivate(FeatureCatalog.KubernetesId).Returns(true);
        activator.DeactivateAsync(FeatureCatalog.KubernetesId, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("The plugin would not stop."));
        var store = Substitute.For<IFeatureStateStore>();
        store.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(FeatureStates.EmptyDisabled()));
        store.SetAsync(Arg.Any<string>(), Arg.Any<FeatureState>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new IOException("The state file is locked."));
        var service = new FeaturesService(_catalog, store, _credentials, activator, []);

        var failed = await Should.ThrowAsync<IOException>(() =>
            service.EnableAsync(FeatureCatalog.KubernetesId, confirmWrites: false, CancellationToken.None));

        failed.Message.ShouldBe("The state file is locked.");
        await activator.Received(1).DeactivateAsync(FeatureCatalog.KubernetesId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Setup_Keeps_A_Revoke_That_Lands_While_The_Credential_Is_Being_Validated()
    {
        var directory = Directory.CreateTempSubdirectory("scout-setup-").FullName;
        try
        {
            using var store = new JsonFileFeatureStateStore(
                Options.Create(new FeatureStateStoreOptions { FilePath = Path.Combine(directory, "features.json") }),
                NullLogger<JsonFileFeatureStateStore>.Instance);
            FeaturesService? service = null;
            var revokeNow = false;
            var validator = Substitute.For<IFeatureCredentialValidator>();
            validator.FeatureId.Returns(FeatureCatalog.GitHubId);
            validator.ValidateAsync(Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>()).Returns(async _ =>
            {
                if (revokeNow)
                {
                    await service!.SetWriteGrantAsync(FeatureCatalog.GitHubId, granted: false, confirmWrites: false, CancellationToken.None);
                }

                return new FeatureCredentialValidationResult(FeatureCredentialValidationOutcome.Valid);
            });
            service = new FeaturesService(new FeatureCatalog(), store, new InMemoryCredentialStore(isAvailable: true), new StubFeatureActivator(), [validator]);
            await service.SetupAsync(
                FeatureCatalog.GitHubId,
                Setup(new(StringComparer.Ordinal) { ["pat"] = "ghp_first" }, new(StringComparer.Ordinal) { ["default_repo_url"] = RepoUrl }),
                CancellationToken.None);
            await service.EnableAsync(FeatureCatalog.GitHubId, confirmWrites: true, CancellationToken.None);
            var grantedBefore = (await store.GetAsync(FeatureCatalog.GitHubId, CancellationToken.None)).Granted;
            revokeNow = true;

            await service.SetupAsync(
                FeatureCatalog.GitHubId,
                Setup(new(StringComparer.Ordinal) { ["pat"] = "ghp_rotated" }, new(StringComparer.Ordinal) { ["default_repo_url"] = RepoUrl + "-2" }),
                CancellationToken.None);

            var state = await store.GetAsync(FeatureCatalog.GitHubId, CancellationToken.None);
            grantedBefore.ShouldBeTrue();
            (state.Granted, state.GrantedAt, state.Enabled).ShouldBe((false, (DateTimeOffset?)null, true));
            state.Settings["default_repo_url"].ShouldBe(RepoUrl + "-2");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public Task Setup_Whose_State_Cannot_Be_Read_Fails_As_Not_Persisted_And_Writes_Nothing() =>
        WithGitHubAsync(async (store, credentials, service) =>
        {
            await store.SetAsync(FeatureCatalog.GitHubId, FeatureStates.EmptyDisabled() with { Enabled = true }, CancellationToken.None);
            var before = await File.ReadAllBytesAsync(store.FilePath);

            FeaturePersistFailedException failed;
            await using (new FileStream(store.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                failed = await Should.ThrowAsync<FeaturePersistFailedException>(() => service.SetupAsync(
                    FeatureCatalog.GitHubId,
                    Setup(new(StringComparer.Ordinal) { ["pat"] = "ghp_first" }, new(StringComparer.Ordinal) { ["default_repo_url"] = RepoUrl }),
                    CancellationToken.None));
            }

            failed.Message.ShouldStartWith("Failed to read the feature's settings: ");
            (await File.ReadAllBytesAsync(store.FilePath)).ShouldBe(before);
            (await credentials.ContainsAsync("scout/github/pat", CancellationToken.None)).ShouldBeFalse();
        });
}
