using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scout.ControlPlane.Features;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Features;

public sealed class JsonFileFeatureStateStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("scout-state-").FullName;
    private readonly RecordingLogger<JsonFileFeatureStateStore> _logger = new();

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string FilePath => Path.Combine(_directory, "features.json");

    private JsonFileFeatureStateStore Store() =>
        new(Options.Create(new FeatureStateStoreOptions { FilePath = FilePath }), _logger);

    [Fact]
    public async Task A_State_File_Without_Grant_Fields_Reads_As_Not_Granted()
    {
        await File.WriteAllTextAsync(FilePath, """{ "github": { "enabled": true, "settings": { "default_repo_url": "https://example.test/repo" } } }""");
        using var store = Store();

        var state = await store.GetAsync("github", CancellationToken.None);

        state.Enabled.ShouldBeTrue();
        state.Settings["default_repo_url"].ShouldBe("https://example.test/repo");
        state.Granted.ShouldBeFalse();
        state.GrantedAt.ShouldBeNull();
    }

    [Fact]
    public async Task A_Written_Grant_Reads_Back_And_No_Temp_File_Remains()
    {
        var grantedAt = new DateTimeOffset(2026, 11, 2, 10, 30, 0, TimeSpan.Zero);
        using (var writer = Store())
        {
            await writer.SetAsync(
                "youtube-publish",
                FeatureStates.EmptyDisabled() with { Enabled = true, Granted = true, GrantedAt = grantedAt },
                CancellationToken.None);
        }

        using var reader = Store();
        var state = await reader.GetAsync("youtube-publish", CancellationToken.None);

        state.Enabled.ShouldBeTrue();
        state.Granted.ShouldBeTrue();
        state.GrantedAt.ShouldBe(grantedAt);
        File.Exists(FilePath + ".tmp").ShouldBeFalse();
    }

    [Fact]
    public async Task A_State_Without_A_Grant_Writes_No_Grant_Fields()
    {
        using var store = Store();

        await store.SetAsync("github", FeatureStates.EmptyDisabled() with { Enabled = true }, CancellationToken.None);

        var json = await File.ReadAllTextAsync(FilePath);
        json.ShouldNotContain("granted");
    }

    [Fact]
    public async Task A_Corrupt_State_File_Falls_Back_To_Defaults_And_Logs()
    {
        await File.WriteAllTextAsync(FilePath, "{ not json");
        using var store = Store();

        var state = await store.GetAsync("youtube-publish", CancellationToken.None);

        state.Enabled.ShouldBeFalse();
        state.Granted.ShouldBeFalse();
        var warning = _logger.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldContain("malformed or unreadable");
    }
}
