using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Features;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Actions;

public sealed class ActionQueueTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("scout-actions-").FullName;
    private readonly RecordingLogger<JsonFileActionQueue> _logger = new();

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private JsonFileActionQueue Queue() =>
        new(Options.Create(new FeatureStateStoreOptions { FilePath = Path.Combine(_directory, "features.json") }), _logger);

    [Fact]
    public async Task A_Queued_Action_Stays_Pending_Until_The_Owner_Decides()
    {
        string firstId;
        using (var queue = Queue())
        {
            firstId = (await queue.EnqueueAsync(
                "youtube-publish", "videos.insert", "video.mp4", "Upload 'Intro' as private", CancellationToken.None)).Id;
            await queue.EnqueueAsync(
                "youtube-engage", "comments.insert", "comment 42", "Reply 'Thanks!'", CancellationToken.None);
        }

        using var reopened = Queue();
        var pending = await reopened.ListPendingAsync(CancellationToken.None);

        reopened.FilePath.ShouldBe(Path.Combine(_directory, "actions.json"));
        pending.Select(a => (a.FeatureId, a.Action, a.Target, a.Summary, a.Status, a.DecidedAt)).ShouldBe([
            ("youtube-publish", "videos.insert", "video.mp4", "Upload 'Intro' as private", PendingActionStatus.Pending, (DateTimeOffset?)null),
            ("youtube-engage", "comments.insert", "comment 42", "Reply 'Thanks!'", PendingActionStatus.Pending, null),
        ]);
        pending[0].Id.ShouldBe(firstId);

        await reopened.ApproveAsync(firstId, reason: null, CancellationToken.None);

        (await reopened.ListPendingAsync(CancellationToken.None)).ShouldHaveSingleItem().FeatureId.ShouldBe("youtube-engage");
    }

    [Fact]
    public async Task Approve_And_Reject_Record_The_Decision_And_Time()
    {
        using var queue = Queue();
        var upload = await queue.EnqueueAsync("youtube-publish", "videos.insert", "a.mp4", "Upload A", CancellationToken.None);
        var reply = await queue.EnqueueAsync("youtube-engage", "comments.insert", "c1", "Reply", CancellationToken.None);
        var before = DateTimeOffset.UtcNow;

        var approved = await queue.ApproveAsync(upload.Id, reason: null, CancellationToken.None);
        var rejected = await queue.RejectAsync(reply.Id, "Wrong tone", CancellationToken.None);

        var after = DateTimeOffset.UtcNow;
        approved.Status.ShouldBe(PendingActionStatus.Approved);
        approved.DecidedAt.ShouldNotBeNull().ShouldBeInRange(before, after);
        approved.RequestedAt.ShouldBe(upload.RequestedAt);
        rejected.Status.ShouldBe(PendingActionStatus.Rejected);
        rejected.DecidedAt.ShouldNotBeNull().ShouldBeInRange(before, after);
        rejected.Reason.ShouldBe("Wrong tone");

        var json = await File.ReadAllTextAsync(queue.FilePath);
        json.ShouldContain("\"status\": \"approved\"");
        json.ShouldContain("\"status\": \"rejected\"");
        json.ShouldContain("\"reason\": \"Wrong tone\"");
    }

    [Fact]
    public async Task Deciding_Twice_Or_On_An_Unknown_Action_Fails()
    {
        using var queue = Queue();
        var upload = await queue.EnqueueAsync("youtube-publish", "videos.insert", "a.mp4", "Upload A", CancellationToken.None);
        await queue.RejectAsync(upload.Id, null, CancellationToken.None);

        var twice = await Should.ThrowAsync<PendingActionAlreadyDecidedException>(() =>
            queue.ApproveAsync(upload.Id, reason: null, CancellationToken.None));
        var unknown = await Should.ThrowAsync<PendingActionNotFoundException>(() =>
            queue.ApproveAsync("no-such-action", reason: null, CancellationToken.None));

        twice.Message.ShouldBe($"Action '{upload.Id}' is already rejected.");
        unknown.Message.ShouldBe("Unknown action 'no-such-action'.");
        (await queue.ListPendingAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Only_An_Approved_Action_Can_Be_Marked_Executed_And_Only_Once()
    {
        using var queue = Queue();
        var approved = await queue.EnqueueAsync("youtube", "videos.insert", "a.mp4", "Upload A", CancellationToken.None);
        var pending = await queue.EnqueueAsync("youtube", "videos.insert", "b.mp4", "Upload B", CancellationToken.None);
        await queue.ApproveAsync(approved.Id, reason: null, CancellationToken.None);

        var executed = await queue.MarkExecutedAsync(approved.Id, CancellationToken.None);
        var twice = await Should.ThrowAsync<PendingActionNotApprovedException>(() =>
            queue.MarkExecutedAsync(approved.Id, CancellationToken.None));
        var notApproved = await Should.ThrowAsync<PendingActionNotApprovedException>(() =>
            queue.MarkExecutedAsync(pending.Id, CancellationToken.None));

        executed.Status.ShouldBe(PendingActionStatus.Executed);
        twice.Message.ShouldBe($"Action '{approved.Id}' is executed, not approved.");
        notApproved.Message.ShouldBe($"Action '{pending.Id}' is pending, not approved.");
        (await File.ReadAllTextAsync(queue.FilePath)).ShouldContain("\"status\": \"executed\"");
        (await queue.ListPendingAsync(CancellationToken.None)).ShouldHaveSingleItem().Id.ShouldBe(pending.Id);
    }

    [Fact]
    public async Task Two_Concurrent_Approvals_Of_One_Action_Let_Only_One_Through()
    {
        using var queue = Queue();
        var upload = await queue.EnqueueAsync("youtube-publish", "videos.insert", "a.mp4", "Upload A", CancellationToken.None);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            try
            {
                await queue.ApproveAsync(upload.Id, reason: null, CancellationToken.None);
                return true;
            }
            catch (PendingActionAlreadyDecidedException)
            {
                return false;
            }
        }));

        attempts.Count(approved => approved).ShouldBe(1);
    }

    [Fact]
    public async Task A_Corrupt_Queue_File_Is_Moved_Aside_Never_Overwritten_And_The_Queue_Starts_Empty()
    {
        using var queue = Queue();
        var corrupt = "[ { broken"u8.ToArray();
        await File.WriteAllBytesAsync(queue.FilePath, corrupt);
        var before = DateTimeOffset.UtcNow;

        var pendingAfterCorruption = await queue.ListPendingAsync(CancellationToken.None);
        await queue.EnqueueAsync("youtube", "videos.insert", "a.mp4", "Upload A", CancellationToken.None);

        pendingAfterCorruption.ShouldBeEmpty();
        var movedAside = Directory.GetFiles(_directory, "actions.json.corrupt-*").ShouldHaveSingleItem();
        var stamp = Path.GetFileName(movedAside)["actions.json.corrupt-".Length..];
        DateTime.ParseExact(stamp, "yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBeInRange(before.UtcDateTime.AddSeconds(-1), DateTime.UtcNow);
        (await File.ReadAllBytesAsync(movedAside)).ShouldBe(corrupt);
        (await queue.ListPendingAsync(CancellationToken.None)).ShouldHaveSingleItem().Target.ShouldBe("a.mp4");
        var warning = _logger.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldBe($"Action queue file at {queue.FilePath} is malformed; moved it to {movedAside} and started an empty queue");
    }

    [Fact]
    public async Task A_Queue_File_That_Cannot_Be_Opened_Propagates_The_Error_And_Is_Never_Moved_Aside()
    {
        using var queue = Queue();
        var content = "[]"u8.ToArray();
        await File.WriteAllBytesAsync(queue.FilePath, content);

        await using (new FileStream(queue.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Should.ThrowAsync<IOException>(() => queue.ListPendingAsync(CancellationToken.None));
        }

        Directory.GetFiles(_directory, "actions.json.corrupt-*").ShouldBeEmpty();
        (await File.ReadAllBytesAsync(queue.FilePath)).ShouldBe(content);
        _logger.Entries.ShouldBeEmpty();
    }
}
