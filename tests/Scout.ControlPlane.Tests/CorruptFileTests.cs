using Microsoft.Extensions.Options;
using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Features;
using Scout.ControlPlane.Operator;
using Scout.ControlPlane.Providers;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests;

/// <summary>The move-aside of a file that does not parse, on a fixed clock, and what each store does when the move fails.</summary>
public sealed class CorruptFileTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Directory.CreateTempSubdirectory("scout-corrupt-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData(0, "state.json.corrupt-20261109120000")]
    [InlineData(1, "state.json.corrupt-20261109120000-1")]
    [InlineData(2, "state.json.corrupt-20261109120000-2")]
    public void A_File_Is_Moved_Aside_Under_The_Stamp_Of_Its_Second_Then_1_Then_2_And_Never_Over_An_Earlier_One(int alreadyTaken, string expected)
    {
        var path = Path.Combine(_directory, "state.json");
        string[] earlier = ["state.json.corrupt-20261109120000", "state.json.corrupt-20261109120000-1"];
        foreach (var name in earlier.Take(alreadyTaken))
        {
            File.WriteAllText(Path.Combine(_directory, name), "earlier " + name);
        }

        File.WriteAllText(path, "{ not json");

        var aside = CorruptFile.MoveAside(path, new StubTime(Noon));

        aside.ShouldBe(Path.Combine(_directory, expected));
        File.ReadAllText(aside).ShouldBe("{ not json");
        File.Exists(path).ShouldBeFalse();
        foreach (var name in earlier.Take(alreadyTaken))
        {
            File.ReadAllText(Path.Combine(_directory, name)).ShouldBe("earlier " + name);
        }
    }

    [Theory]
    [InlineData("the action queue", "actions.json")]
    [InlineData("the feature state store", "features.json")]
    [InlineData("the operator store", "mandate.json")]
    [InlineData("the runtime config store", "local-runtimes.json")]
    public async Task A_Malformed_File_That_Cannot_Be_Moved_Aside_Fails_The_Read_And_Nothing_Is_Reported_As_Moved(string store, string file)
    {
        var path = Path.Combine(_directory, file);
        await File.WriteAllTextAsync(path, "{ not json");
        var gone = new StubTime(Noon, () => File.Delete(path));
        var options = Options.Create(new FeatureStateStoreOptions { FilePath = Path.Combine(_directory, "features.json") });
        var queueLog = new RecordingLogger<JsonFileActionQueue>();
        var stateLog = new RecordingLogger<JsonFileFeatureStateStore>();
        var operatorLog = new RecordingLogger<JsonFileOperatorStore>();
        var runtimeLog = new RecordingLogger<JsonFileLocalRuntimeConfigStore>();
        using var queue = new JsonFileActionQueue(options, queueLog, gone);
        using var states = new JsonFileFeatureStateStore(options, stateLog, gone);
        using var operatorStore = new JsonFileOperatorStore(options, operatorLog, gone);
        using var runtimes = new JsonFileLocalRuntimeConfigStore(Options.Create(new LocalRuntimeConfigStoreOptions { FilePath = path }), runtimeLog, gone);

        await Should.ThrowAsync<FileNotFoundException>(() => store switch
        {
            "the action queue" => queue.ListPendingAsync(CancellationToken.None),
            "the feature state store" => states.GetAsync("youtube", CancellationToken.None),
            "the operator store" => operatorStore.ReadMandateAsync(CancellationToken.None),
            _ => (Task)runtimes.GetAutostartAsync("ollama", CancellationToken.None),
        });

        Directory.GetFiles(_directory, "*.corrupt-*").ShouldBeEmpty();
        queueLog.Entries.Concat(stateLog.Entries).Concat(operatorLog.Entries).Concat(runtimeLog.Entries).ShouldBeEmpty();
    }
}
