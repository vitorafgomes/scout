using System.Text.Json;
using System.Text.Json.Serialization;
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

    [Fact]
    public async Task Writing_The_Queue_Drops_Rejected_Actions_After_30_Days_And_Executed_Ones_After_90_And_Keeps_Pending_Approved_And_Executing()
    {
        using var queue = Queue();
        var now = DateTimeOffset.UtcNow;
        PendingAction Seed(string id, PendingActionStatus status, int daysAgo) =>
            new(id, "youtube", "videos.insert", $"{id}.mp4", "Upload", now.AddDays(-daysAgo - 1), status, now.AddDays(-daysAgo));
        var seeded = new List<PendingAction>
        {
            Seed("pending-400", PendingActionStatus.Pending, 400) with { DecidedAt = null },
            Seed("approved-400", PendingActionStatus.Approved, 400),
            Seed("rejected-31", PendingActionStatus.Rejected, 31),
            Seed("rejected-29", PendingActionStatus.Rejected, 29),
            Seed("executed-91", PendingActionStatus.Executed, 91),
            Seed("executed-89", PendingActionStatus.Executed, 89),
            Seed("executing-400", PendingActionStatus.Executing, 400),
        };
        await File.WriteAllTextAsync(queue.FilePath, JsonSerializer.Serialize(seeded, FileJson));

        var added = await queue.EnqueueAsync("youtube", "videos.insert", "new.mp4", "Upload new", CancellationToken.None);

        var kept = JsonSerializer.Deserialize<List<PendingAction>>(await File.ReadAllTextAsync(queue.FilePath), FileJson);
        kept.ShouldNotBeNull().Select(a => a.Id).ShouldBe(["pending-400", "approved-400", "rejected-29", "executed-89", "executing-400", added.Id]);
    }

    [Fact]
    public async Task An_Approved_Batch_Is_Written_Approved_In_Its_Order()
    {
        using var queue = Queue();

        var batch = await queue.EnqueueApprovedAsync(
            "youtube", [("videos.insert", "p1", "Upload"), ("thumbnails.set", "p1", "Thumbnail")], CancellationToken.None);

        using var reopened = Queue();
        var read = await Task.WhenAll(batch.Select(a => reopened.FindAsync(a.Id, CancellationToken.None)));
        read.Select(a => (a!.Action, a.Status, a.DecidedAt is not null)).ShouldBe([
            ("videos.insert", PendingActionStatus.Approved, true),
            ("thumbnails.set", PendingActionStatus.Approved, true),
        ]);
        (await reopened.ListPendingAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Two_Concurrent_Claims_Never_Get_The_Same_Action()
    {
        using var queue = Queue();
        var approved = (await queue.EnqueueApprovedAsync("youtube", [("videos.insert", "p1", "Upload")], CancellationToken.None)).Single();
        using var start = new ManualResetEventSlim();

        var claims = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            start.Wait();
            return queue.ClaimNextAsync("youtube", TimeSpan.FromMinutes(30), (_, _) => true, CancellationToken.None);
        })).Append(Task.Run(() => { start.Set(); return (PendingAction?)null; })));

        claims.Take(2).Count(c => c is not null).ShouldBe(1);
        claims.Take(2).Single(c => c is not null)!.Id.ShouldBe(approved.Id);
        (await queue.FindAsync(approved.Id, CancellationToken.None))!.Status.ShouldBe(PendingActionStatus.Executing);
    }

    [Fact]
    public async Task An_Executing_Action_Is_Listed_With_The_Pending_Ones_And_Cannot_Be_Decided()
    {
        using var queue = Queue();
        var pending = await queue.EnqueueAsync("youtube", "comments.insert", "c1", "Reply", CancellationToken.None);
        var claimed = (await queue.EnqueueApprovedAsync("youtube", [("videos.insert", "p1", "Upload")], CancellationToken.None)).Single();
        var waiting = (await queue.EnqueueApprovedAsync("youtube", [("thumbnails.set", "p2", "Thumbnail")], CancellationToken.None)).Single();
        await queue.ClaimNextAsync("youtube", TimeSpan.FromMinutes(30), (a, _) => a.Id == claimed.Id, CancellationToken.None);

        var listed = await queue.ListPendingAsync(CancellationToken.None);
        var approve = await Should.ThrowAsync<PendingActionAlreadyDecidedException>(() => queue.ApproveAsync(claimed.Id, null, CancellationToken.None));
        var reject = await Should.ThrowAsync<PendingActionAlreadyDecidedException>(() => queue.RejectAsync(claimed.Id, "No", CancellationToken.None));

        listed.Select(a => (a.Id, a.Status)).ShouldBe([(pending.Id, PendingActionStatus.Pending), (claimed.Id, PendingActionStatus.Executing)]);
        listed.ShouldNotContain(a => a.Id == waiting.Id);
        (approve.Message, reject.Message).ShouldBe(($"Action '{claimed.Id}' is already executing.", $"Action '{claimed.Id}' is already executing."));
        (await queue.FindAsync(claimed.Id, CancellationToken.None))!.Status.ShouldBe(PendingActionStatus.Executing);
    }

    private static readonly JsonSerializerOptions FileJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
