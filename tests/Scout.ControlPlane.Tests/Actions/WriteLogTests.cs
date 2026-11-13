using System.Text.Json;
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
    public async Task Listing_Since_A_Time_Reads_Back_Only_To_The_First_Older_Entry()
    {
        using var log = Log();
        await File.WriteAllTextAsync(log.FilePath, "{ an old broken line\n");
        await log.AppendAsync(Entry(1, "videos.insert"), CancellationToken.None);
        await log.AppendAsync(Entry(5, "thumbnails.set"), CancellationToken.None);
        await log.AppendAsync(Entry(6, "videos.update"), CancellationToken.None);

        var since = await log.ListSinceAsync(T0.AddMinutes(3), CancellationToken.None);

        since.Select(e => e.Action).ShouldBe(["videos.update", "thumbnails.set"]);
        _logger.Entries.ShouldBeEmpty();
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
        var (level, message) = _logger.Entries.ShouldHaveSingleItem();
        level.ShouldBe(LogLevel.Warning);
        message.ShouldContain("Skipped malformed line 2");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(400)]
    public async Task The_Tail_Read_Lists_The_Newest_Entries_First_For_Small_And_Large_Files_And_Lines_Longer_Than_A_Block(int count)
    {
        using var log = Log();
        var appended = new List<WriteLogEntry>();
        for (var i = 0; i < count; i++)
        {
            var target = i % 50 == 7 ? new string('x', JsonLinesWriteLog.BlockSize * 3) : $"video-{i}";
            var entry = new WriteLogEntry(T0.AddMinutes(i), "youtube", "videos.insert", target, "ok", "owner", $"action-{i}");
            appended.Add(entry);
            await log.AppendAsync(entry, CancellationToken.None);
        }

        var newestFirst = Enumerable.Reverse(appended).ToList();
        foreach (var limit in new[] { 1, 2, (count / 2) + 1, count, count + 10 })
        {
            (await log.ListAsync(limit, CancellationToken.None)).ShouldBe(newestFirst.Take(limit).ToList(), $"limit {limit}");
        }
    }

    [Fact]
    public async Task The_Tail_Read_Skips_Blank_Lines_Reads_A_Last_Line_Without_Newline_And_Counts_A_Malformed_Line_From_The_End()
    {
        using var log = Log();
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        await File.WriteAllTextAsync(
            log.FilePath,
            JsonSerializer.Serialize(Entry(1, "videos.insert"), json) + "\n{ half a line\n\n   \n"
            + JsonSerializer.Serialize(Entry(2, "videos.update"), json) + "\n"
            + JsonSerializer.Serialize(Entry(3, "thumbnails.set"), json));

        var lastTwo = await log.ListAsync(2, CancellationToken.None);
        var lastThree = await log.ListAsync(3, CancellationToken.None);
        var all = await log.ListAsync(10, CancellationToken.None);

        lastTwo.ShouldBe([Entry(3, "thumbnails.set"), Entry(2, "videos.update")]);
        lastThree.ShouldBe([Entry(3, "thumbnails.set"), Entry(2, "videos.update"), Entry(1, "videos.insert")]);
        all.ShouldBe([Entry(3, "thumbnails.set"), Entry(2, "videos.update"), Entry(1, "videos.insert")]);
        _logger.Entries.Select(e => e.Message).Distinct().ShouldHaveSingleItem()
            .ShouldBe($"Skipped malformed line 5 from the end of the write log at {log.FilePath}");
    }

    [Fact]
    public async Task Malformed_Log_Lines_Do_Not_Consume_The_Requested_Entry_Limit()
    {
        using var log = Log();
        await log.AppendAsync(Entry(1, "videos.insert"), CancellationToken.None);
        await log.AppendAsync(Entry(2, "videos.update"), CancellationToken.None);
        await File.AppendAllTextAsync(log.FilePath, "{ half a line\n{ another half\n");
        await log.AppendAsync(Entry(3, "thumbnails.set"), CancellationToken.None);

        var lastThree = await log.ListAsync(3, CancellationToken.None);

        lastThree.ShouldBe([Entry(3, "thumbnails.set"), Entry(2, "videos.update"), Entry(1, "videos.insert")]);
        _logger.Entries.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_Write_Log_Line_Written_Before_Groups_Existed_Still_Reads()
    {
        using var log = Log();
        await File.WriteAllTextAsync(
            log.FilePath,
            """{"at":"2026-11-01T09:00:00+00:00","featureId":"github","action":"New issues","target":"GitHubActions.create_issue","outcome":"rejected: not now","approvedBy":"owner","actionId":"old1"}""" + "\n");

        var old = (await log.ListAsync(10, CancellationToken.None)).ShouldHaveSingleItem();

        old.ShouldBe(new WriteLogEntry(
            new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero), "github", "New issues", "GitHubActions.create_issue", "rejected: not now", "owner", "old1"));
        old.GroupId.ShouldBeNull();
        _logger.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_Entry_Keeps_Its_Group_Id_And_A_Rejection_Takes_The_Group_Of_Its_Action()
    {
        var action = new PendingAction("a1", "github", "New issues", "GitHubActions.create_issue", "s", T0, PendingActionStatus.Rejected, T0)
        {
            GroupId = "turn-1",
        };
        using (var log = Log())
        {
            await log.AppendAsync(WriteLogEntry.Rejected(action, "not now"), CancellationToken.None);
            await log.AppendAsync(WriteLogEntry.Rejected(action with { GroupId = null }, null), CancellationToken.None);
        }

        using var reopened = Log();
        var entries = await reopened.ListAsync(10, CancellationToken.None);

        entries.Select(e => (e.Outcome, e.ActionId, e.GroupId)).ShouldBe([
            ("rejected", "a1", (string?)null),
            ("rejected: not now", "a1", "turn-1"),
        ]);
    }
}
