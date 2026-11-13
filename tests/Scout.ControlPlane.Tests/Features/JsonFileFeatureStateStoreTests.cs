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
    public async Task A_Malformed_State_File_Is_Moved_Aside_Reads_As_Defaults_And_A_Later_Set_Never_Overwrites_It()
    {
        var malformed = "{ not json"u8.ToArray();
        await File.WriteAllBytesAsync(FilePath, malformed);
        using var store = Store();

        var state = await store.GetAsync("youtube-publish", CancellationToken.None);
        await store.SetAsync("github", FeatureStates.EmptyDisabled() with { Enabled = true }, CancellationToken.None);

        state.Enabled.ShouldBeFalse();
        state.Granted.ShouldBeFalse();
        var movedAside = Directory.GetFiles(_directory, "features.json.corrupt-*").ShouldHaveSingleItem();
        (await File.ReadAllBytesAsync(movedAside)).ShouldBe(malformed);
        (await store.GetAllAsync(CancellationToken.None)).Keys.ShouldBe(["github"]);
        var (level, message) = _logger.Entries.ShouldHaveSingleItem();
        level.ShouldBe(LogLevel.Warning);
        message.ShouldBe($"Feature state file at {FilePath} is malformed; moved it to {movedAside} and started from defaults");
    }

    [Fact]
    public async Task Many_Reads_At_Once_Of_A_Malformed_State_File_Move_It_Aside_Once_And_None_Fails()
    {
        await File.WriteAllTextAsync(FilePath, "{ not json" + new string(' ', 200_000));
        using var store = Store();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var reads = Enumerable.Range(0, 64).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            return await store.GetAsync("youtube", CancellationToken.None);
        })).ToList();
        var all = Task.WhenAll(reads);
        start.SetResult();

        await Should.NotThrowAsync(() => all);
        (await all).ShouldAllBe(state => !state.Enabled && !state.Granted);
        Directory.GetFiles(_directory, "features.json.corrupt-*").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_State_File_That_Cannot_Be_Read_Propagates_The_Error_And_A_Later_Set_Never_Overwrites_It()
    {
        var grantedAt = new DateTimeOffset(2026, 11, 2, 10, 30, 0, TimeSpan.Zero);
        using var store = Store();
        await store.SetAsync(
            "youtube",
            FeatureStates.EmptyDisabled() with { Enabled = true, Granted = true, GrantedAt = grantedAt },
            CancellationToken.None);
        var before = await File.ReadAllBytesAsync(FilePath);

        await using (new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Should.ThrowAsync<IOException>(() => store.GetAsync("youtube", CancellationToken.None));
            await Should.ThrowAsync<IOException>(() => store.GetAllAsync(CancellationToken.None));
            await Should.ThrowAsync<IOException>(() =>
                store.SetAsync("github", FeatureStates.EmptyDisabled() with { Enabled = true }, CancellationToken.None));
        }

        (await File.ReadAllBytesAsync(FilePath)).ShouldBe(before);
        Directory.GetFiles(_directory, "features.json.corrupt-*").ShouldBeEmpty();
        var kept = await store.GetAsync("youtube", CancellationToken.None);
        (kept.Enabled, kept.Granted, kept.GrantedAt).ShouldBe((true, true, (DateTimeOffset?)grantedAt));
    }

    [Fact]
    public async Task A_State_File_Holding_Null_Is_Moved_Aside_Like_A_Malformed_One_And_A_Later_Set_Never_Overwrites_It()
    {
        var nullDocument = "null"u8.ToArray();
        await File.WriteAllBytesAsync(FilePath, nullDocument);
        using var store = Store();

        var state = await store.GetAsync("youtube-publish", CancellationToken.None);
        await store.SetAsync("github", FeatureStates.EmptyDisabled() with { Enabled = true }, CancellationToken.None);

        state.Enabled.ShouldBeFalse();
        var movedAside = Directory.GetFiles(_directory, "features.json.corrupt-*").ShouldHaveSingleItem();
        (await File.ReadAllBytesAsync(movedAside)).ShouldBe(nullDocument);
        (await store.GetAllAsync(CancellationToken.None)).Keys.ShouldBe(["github"]);
        var (level, message) = _logger.Entries.ShouldHaveSingleItem();
        level.ShouldBe(LogLevel.Warning);
        message.ShouldBe($"Feature state file at {FilePath} is malformed; moved it to {movedAside} and started from defaults");
    }
}
