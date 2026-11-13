using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scout.ControlPlane.Providers;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Providers;

/// <summary>The local runtime settings over a real file in a temp folder.</summary>
public sealed class JsonFileLocalRuntimeConfigStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("scout-local-runtimes-").FullName;
    private readonly RecordingLogger<JsonFileLocalRuntimeConfigStore> _logger = new();

    private string FilePath => Path.Combine(_directory, "local-runtimes.json");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private JsonFileLocalRuntimeConfigStore Store() =>
        new(Options.Create(new LocalRuntimeConfigStoreOptions { FilePath = FilePath }), _logger);

    [Fact]
    public async Task A_Malformed_Config_File_Is_Moved_Aside_Reads_As_Defaults_And_A_Later_Set_Never_Overwrites_It()
    {
        var malformed = "{ not json"u8.ToArray();
        await File.WriteAllBytesAsync(FilePath, malformed);
        using var store = Store();

        var autostart = await store.GetAutostartAsync("ollama", CancellationToken.None);
        await store.SetAutostartAsync("ollama", enabled: false, CancellationToken.None);

        autostart.ShouldBeTrue();
        var movedAside = Directory.GetFiles(_directory, "local-runtimes.json.corrupt-*").ShouldHaveSingleItem();
        (await File.ReadAllBytesAsync(movedAside)).ShouldBe(malformed);
        (await store.GetAutostartAsync("ollama", CancellationToken.None)).ShouldBeFalse();
        var (level, message) = _logger.Entries.ShouldHaveSingleItem();
        level.ShouldBe(LogLevel.Warning);
        message.ShouldBe($"Local runtime config at {FilePath} is malformed; moved it to {movedAside} and started from defaults");
    }

    [Fact]
    public async Task Many_Reads_At_Once_Of_A_Malformed_Config_File_Move_It_Aside_Once_And_None_Fails()
    {
        await File.WriteAllTextAsync(FilePath, "{ not json" + new string(' ', 200_000));
        using var store = Store();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var reads = Enumerable.Range(0, 64).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            return await store.GetAutostartAsync("ollama", CancellationToken.None);
        })).ToList();
        var all = Task.WhenAll(reads);
        start.SetResult();

        await Should.NotThrowAsync(() => all);
        (await all).ShouldAllBe(autostart => autostart);
        Directory.GetFiles(_directory, "local-runtimes.json.corrupt-*").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_Config_File_That_Cannot_Be_Read_Propagates_The_Error_And_A_Later_Set_Never_Overwrites_It()
    {
        using var store = Store();
        await store.SetAutostartAsync("ollama", enabled: false, CancellationToken.None);
        await store.SetAutostartAsync("lmstudio", enabled: false, CancellationToken.None);
        var before = await File.ReadAllBytesAsync(FilePath);

        await using (new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Should.ThrowAsync<IOException>(() => store.GetAutostartAsync("ollama", CancellationToken.None));
            await Should.ThrowAsync<IOException>(() => store.SetAutostartAsync("ollama", enabled: true, CancellationToken.None));
        }

        (await File.ReadAllBytesAsync(FilePath)).ShouldBe(before);
        Directory.GetFiles(_directory, "local-runtimes.json.corrupt-*").ShouldBeEmpty();
        (await store.GetAutostartAsync("ollama", CancellationToken.None)).ShouldBeFalse();
        (await store.GetAutostartAsync("lmstudio", CancellationToken.None)).ShouldBeFalse();
        _logger.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Config_File_Holding_Null_Is_Moved_Aside_Like_A_Malformed_One_And_A_Later_Set_Never_Overwrites_It()
    {
        var nullDocument = "null"u8.ToArray();
        await File.WriteAllBytesAsync(FilePath, nullDocument);
        using var store = Store();

        var autostart = await store.GetAutostartAsync("ollama", CancellationToken.None);
        await store.SetAutostartAsync("ollama", enabled: false, CancellationToken.None);

        autostart.ShouldBeTrue();
        var movedAside = Directory.GetFiles(_directory, "local-runtimes.json.corrupt-*").ShouldHaveSingleItem();
        (await File.ReadAllBytesAsync(movedAside)).ShouldBe(nullDocument);
        _logger.Entries.ShouldHaveSingleItem().Message
            .ShouldBe($"Local runtime config at {FilePath} is malformed; moved it to {movedAside} and started from defaults");
    }
}
