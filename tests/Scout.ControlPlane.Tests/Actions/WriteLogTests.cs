using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Features;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Actions;

public sealed class WriteLogTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Directory.CreateTempSubdirectory("scout-writes-").FullName;
    private readonly RecordingLogger<JsonLinesWriteLog> _logger = new();

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private JsonLinesWriteLog Log() =>
        new(Options.Create(new FeatureStateStoreOptions { FilePath = Path.Combine(_directory, "features.json") }), _logger);

    private static WriteLogEntry Entry(int minute, string action) =>
        new(T0.AddMinutes(minute), "youtube-publish", action, $"video-{minute}", "ok", "owner", $"action-{minute}");

    [Fact]
    public async Task Entries_Append_And_List_Newest_First_With_A_Limit()
    {
        using (var log = Log())
        {
            await log.AppendAsync(Entry(1, "videos.insert"), CancellationToken.None);
            await log.AppendAsync(Entry(2, "thumbnails.set"), CancellationToken.None);
        }

        using var reopened = Log();
        await reopened.AppendAsync(
            new WriteLogEntry(T0.AddMinutes(3), "youtube-production", "Scripts", "script.md", "ok", null, null),
            CancellationToken.None);

        var lastTwo = await reopened.ListAsync(2, CancellationToken.None);
        var all = await reopened.ListAsync(10, CancellationToken.None);

        lastTwo.ShouldBe([
            new WriteLogEntry(T0.AddMinutes(3), "youtube-production", "Scripts", "script.md", "ok", null, null),
            Entry(2, "thumbnails.set"),
        ]);
        all.Select(e => e.Action).ShouldBe(["Scripts", "thumbnails.set", "videos.insert"]);
        File.ReadAllLines(reopened.FilePath).Length.ShouldBe(3);
        reopened.FilePath.ShouldBe(Path.Combine(_directory, "writes.log"));
    }

    [Fact]
    public async Task A_Corrupt_Log_Line_Is_Skipped_And_Logged()
    {
        using var log = Log();
        await log.AppendAsync(Entry(1, "videos.insert"), CancellationToken.None);
        await File.AppendAllTextAsync(log.FilePath, "{ half a line\n");
        await log.AppendAsync(Entry(2, "videos.update"), CancellationToken.None);

        var entries = await log.ListAsync(10, CancellationToken.None);

        entries.ShouldBe([Entry(2, "videos.update"), Entry(1, "videos.insert")]);
        var warning = _logger.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldContain("Skipped malformed line 2");
    }
}
