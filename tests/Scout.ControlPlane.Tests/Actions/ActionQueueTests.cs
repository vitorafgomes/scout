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

        (await reopened.ListPendingAsync(CancellationToken.None)).Select(a => (a.FeatureId, a.Status)).ShouldBe([
            ("youtube-publish", PendingActionStatus.Approved),
            ("youtube-engage", PendingActionStatus.Pending),
        ]);
    }

    [Fact]
    public async Task Rejecting_The_Unstarted_Actions_Of_A_Feature_Rejects_Its_Waiting_And_Approved_Unclaimed_Ones_Only_And_Survives_A_Reopen()
    {
        using (var queue = Queue())
        {
            var running = await queue.EnqueueAsync("youtube", "thumbnails.set", "intro.mp4", "Thumbnail", CancellationToken.None);
            await queue.ApproveAsync(running.Id, reason: null, CancellationToken.None);
            (await queue.ClaimNextAsync("youtube", TimeSpan.FromMinutes(30), (a, _) => a.Id == running.Id, CancellationToken.None))
                .ShouldNotBeNull();
            var waiting = await queue.EnqueueAsync("youtube", "videos.insert", "intro.mp4", "Upload", CancellationToken.None);
            var approved = await queue.EnqueueAsync("youtube", "videos.update", "intro.mp4", "Retitle", CancellationToken.None);
            await queue.ApproveAsync(approved.Id, reason: null, CancellationToken.None);
            await queue.EnqueueAsync("github", "pulls.reviews.create", "octocat/hello#42", "Approve", CancellationToken.None);

            var rejected = await queue.RejectUnstartedAsync("youtube", "writes were revoked", CancellationToken.None);

            rejected.Select(a => a.Id).ShouldBe([waiting.Id, approved.Id]);
        }

        using var reopened = Queue();
        var all = (await reopened.ListAllAsync("youtube", CancellationToken.None))
            .Concat(await reopened.ListAllAsync("github", CancellationToken.None));
        all.Select(a => (a.FeatureId, a.Action, a.Status, a.Reason)).ShouldBe([
            ("youtube", "thumbnails.set", PendingActionStatus.Executing, (string?)null),
            ("youtube", "videos.insert", PendingActionStatus.Rejected, "writes were revoked"),
            ("youtube", "videos.update", PendingActionStatus.Rejected, "writes were revoked"),
            ("github", "pulls.reviews.create", PendingActionStatus.Pending, null),
        ]);
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
        var (level, message) = _logger.Entries.ShouldHaveSingleItem();
        level.ShouldBe(LogLevel.Warning);
        message.ShouldBe($"Action queue file at {queue.FilePath} is malformed; moved it to {movedAside} and started an empty queue");
    }

    [Fact]
    public async Task A_Corrupt_Queue_File_Is_Moved_Aside_Even_When_A_Backup_Of_The_Same_Second_Exists_And_Never_Over_It()
    {
        using var queue = new JsonFileActionQueue(
            Options.Create(new FeatureStateStoreOptions { FilePath = Path.Combine(_directory, "features.json") }),
            _logger,
            new StubTime(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero)));
        var earlier = "[ { first"u8.ToArray();
        List<string> taken = [queue.FilePath + ".corrupt-20261109120000"];
        foreach (var path in taken)
        {
            await File.WriteAllBytesAsync(path, earlier);
        }
        var corrupt = "[ { second"u8.ToArray();
        await File.WriteAllBytesAsync(queue.FilePath, corrupt);

        await Should.NotThrowAsync(() => queue.ListPendingAsync(CancellationToken.None));

        (await queue.ListPendingAsync(CancellationToken.None)).ShouldBeEmpty();
        foreach (var path in taken)
        {
            (await File.ReadAllBytesAsync(path)).ShouldBe(earlier);
        }
        var movedAside = Directory.GetFiles(_directory, "actions.json.corrupt-*").Except(taken).ShouldHaveSingleItem();
        movedAside.ShouldEndWith("-1");
        (await File.ReadAllBytesAsync(movedAside)).ShouldBe(corrupt);
        File.Exists(queue.FilePath).ShouldBeFalse();
    }

    private static readonly DateTimeOffset Injected = new(2040, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private JsonFileActionQueue QueueAt(DateTimeOffset now) =>
        new(Options.Create(new FeatureStateStoreOptions { FilePath = Path.Combine(_directory, "features.json") }), _logger, new StubTime(now));

    [Theory]
    [InlineData("enqueue")]
    [InlineData("enqueue approved")]
    [InlineData("enqueue approved once")]
    [InlineData("enqueue approved once each")]
    [InlineData("approve")]
    [InlineData("reject")]
    [InlineData("claim")]
    [InlineData("reject approved")]
    [InlineData("reject unstarted")]
    [InlineData("reject executing")]
    [InlineData("close group")]
    [InlineData("decide set")]
    public async Task Every_Operation_Stamps_Its_Time_From_The_Injected_Clock(string operation)
    {
        using var queue = QueueAt(Injected);
        var ct = CancellationToken.None;

        DateTimeOffset? stamped = operation switch
        {
            "enqueue" => (await queue.EnqueueAsync("youtube", "videos.insert", "p1", "Upload", ct)).RequestedAt,
            "enqueue approved" => (await queue.EnqueueApprovedAsync("youtube", [("videos.insert", "p1", "Upload")], ct))[0].DecidedAt,
            "enqueue approved once" => (await queue.EnqueueApprovedOnceAsync("youtube", "videos.insert", "p1", "Upload", "{}", _ => false, ct)).Action.DecidedAt,
            "enqueue approved once each" => (await queue.EnqueueApprovedOnceEachAsync(
                "github", "octocat/hello", [("card-1", "pulls.reviews.create", "octocat/hello#1", "Review", "{}")], (_, _) => false, ct))[0].Action.DecidedAt,
            "approve" => (await queue.ApproveAsync((await queue.EnqueueAsync("youtube", "videos.insert", "p1", "Upload", ct)).Id, null, ct)).DecidedAt,
            "reject" => (await queue.RejectAsync((await queue.EnqueueAsync("youtube", "videos.insert", "p1", "Upload", ct)).Id, null, ct)).DecidedAt,
            "claim" => (await ClaimAsync(queue))!.ClaimedAt,
            "reject approved" => (await RejectApprovedAsync(queue))[0].DecidedAt,
            "reject unstarted" => (await RejectUnstartedAsync(queue))[0].DecidedAt,
            "reject executing" => (await RejectExecutingAsync(queue))[0].DecidedAt,
            "close group" => await CloseGroupAsync(queue),
            _ => (await DecideSetAsync(queue))[0].DecidedAt,
        };

        stamped.ShouldBe(Injected);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(-1, false)]
    public async Task A_Claim_Expires_After_Its_Lease_By_The_Injected_Clock(int secondsPastLease, bool claimedAgain)
    {
        var lease = TimeSpan.FromMinutes(30);
        using (var queue = QueueAt(Injected))
        {
            (await ClaimAsync(queue, lease)).ShouldNotBeNull();
        }

        var now = Injected + lease + TimeSpan.FromSeconds(secondsPastLease);
        using var later = QueueAt(now);
        var again = await later.ClaimNextAsync("youtube", lease, (_, _) => true, CancellationToken.None);

        (again?.ClaimedAt).ShouldBe(claimedAgain ? now : (DateTimeOffset?)null);
    }

    [Theory]
    [InlineData("rejected", 30, 1, false)]
    [InlineData("rejected", 30, -1, true)]
    [InlineData("executed", 90, 1, false)]
    [InlineData("executed", 90, -1, true)]
    public async Task A_Decided_Action_Is_Pruned_After_Its_Retention_By_The_Injected_Clock(string decision, int days, int secondsPast, bool kept)
    {
        string id;
        using (var queue = QueueAt(Injected))
        {
            id = (await queue.EnqueueAsync("youtube", "videos.insert", "p1", "Upload", CancellationToken.None)).Id;
            if (decision == "rejected")
            {
                await queue.RejectAsync(id, null, CancellationToken.None);
            }
            else
            {
                await queue.ApproveAsync(id, null, CancellationToken.None);
                await queue.MarkExecutedAsync(id, CancellationToken.None);
            }
        }

        using var later = QueueAt(Injected + TimeSpan.FromDays(days) + TimeSpan.FromSeconds(secondsPast));
        await later.EnqueueAsync("youtube", "videos.update", "p1", "Retitle", CancellationToken.None);

        (await later.FindAsync(id, CancellationToken.None) is not null).ShouldBe(kept);
    }

    private static async Task<PendingAction?> ClaimAsync(JsonFileActionQueue queue, TimeSpan? lease = null)
    {
        await queue.EnqueueApprovedAsync("youtube", [("videos.insert", "p1", "Upload")], CancellationToken.None);
        return await queue.ClaimNextAsync("youtube", lease ?? TimeSpan.FromMinutes(30), (_, _) => true, CancellationToken.None);
    }

    private static async Task<IReadOnlyList<PendingAction>> RejectApprovedAsync(JsonFileActionQueue queue)
    {
        await queue.EnqueueApprovedAsync("youtube", [("videos.insert", "p1", "Upload")], CancellationToken.None);
        return await queue.RejectApprovedAsync("youtube", (_, _) => true, "writes were revoked", CancellationToken.None);
    }

    private static async Task<IReadOnlyList<PendingAction>> RejectUnstartedAsync(JsonFileActionQueue queue)
    {
        await queue.EnqueueAsync("youtube", "videos.insert", "p1", "Upload", CancellationToken.None);
        return await queue.RejectUnstartedAsync("youtube", "writes were revoked", CancellationToken.None);
    }

    private static async Task<IReadOnlyList<PendingAction>> RejectExecutingAsync(JsonFileActionQueue queue)
    {
        await ClaimAsync(queue);
        return await queue.RejectExecutingAsync("youtube", _ => true, "writes were revoked", CancellationToken.None);
    }

    private static async Task<DateTimeOffset?> CloseGroupAsync(JsonFileActionQueue queue)
    {
        var grouped = await queue.EnqueueAsync("github", "pulls.reviews.create", "octocat/hello#1", "Review", null, "g1", "octocat/hello", CancellationToken.None);
        await queue.CloseGroupAsync("g1", CancellationToken.None);
        return (await queue.FindAsync(grouped.Id, CancellationToken.None))!.GroupClosedAt;
    }

    private static async Task<IReadOnlyList<PendingAction>> DecideSetAsync(JsonFileActionQueue queue)
    {
        await queue.EnqueueAsync("github", "pulls.reviews.create", "octocat/hello#1", "Review", null, "g1", "octocat/hello", CancellationToken.None);
        return await queue.DecideSetAsync(
            "g1",
            set => Task.FromResult<IReadOnlyDictionary<string, (bool Approve, string? Reason)>>(
                new Dictionary<string, (bool Approve, string? Reason)> { [set[0].Id] = (true, null) }),
            CancellationToken.None);
    }

    [Theory]
    [InlineData("", "p1", "Upload", "action")]
    [InlineData(" ", "p1", "Upload", "action")]
    [InlineData(null, "p1", "Upload", "action")]
    [InlineData("videos.insert", null, "Upload", "target")]
    [InlineData("videos.insert", "p1", null, "summary")]
    public async Task EnqueueApproved_Refuses_A_Batch_With_A_Blank_Action_Or_A_Null_Target_Or_Summary_And_Writes_None_Of_It(
        string? action, string? target, string? summary, string refusedArgument)
    {
        using var queue = Queue();
        await queue.EnqueueAsync("youtube", "videos.insert", "p0", "Upload", CancellationToken.None);
        var before = await File.ReadAllBytesAsync(queue.FilePath);

        var refused = await Should.ThrowAsync<ArgumentException>(() => queue.EnqueueApprovedAsync(
            "youtube",
            [("thumbnails.set", "p1", "Thumbnail"), (action!, target!, summary!)],
            CancellationToken.None));

        refused.ParamName.ShouldBe(refusedArgument);
        (await File.ReadAllBytesAsync(queue.FilePath)).ShouldBe(before);
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
        (await reopened.ListPendingAsync(CancellationToken.None)).Select(a => (a.Id, a.Status)).ShouldBe(
            batch.Select(a => (a.Id, PendingActionStatus.Approved)));
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
    public async Task An_Executing_Action_And_An_Approved_One_No_Worker_Claimed_Are_Listed_With_The_Pending_Ones_And_Cannot_Be_Decided()
    {
        using var queue = Queue();
        var pending = await queue.EnqueueAsync("youtube", "comments.insert", "c1", "Reply", CancellationToken.None);
        var claimed = (await queue.EnqueueApprovedAsync("youtube", [("videos.insert", "p1", "Upload")], CancellationToken.None)).Single();
        var waiting = (await queue.EnqueueApprovedAsync("youtube", [("thumbnails.set", "p2", "Thumbnail")], CancellationToken.None)).Single();
        await queue.ClaimNextAsync("youtube", TimeSpan.FromMinutes(30), (a, _) => a.Id == claimed.Id, CancellationToken.None);

        var listed = await queue.ListPendingAsync(CancellationToken.None);
        var approve = await Should.ThrowAsync<PendingActionAlreadyDecidedException>(() => queue.ApproveAsync(claimed.Id, null, CancellationToken.None));
        var reject = await Should.ThrowAsync<PendingActionAlreadyDecidedException>(() => queue.RejectAsync(claimed.Id, "No", CancellationToken.None));

        listed.Select(a => (a.Id, a.Status)).ShouldBe([
            (pending.Id, PendingActionStatus.Pending),
            (claimed.Id, PendingActionStatus.Executing),
            (waiting.Id, PendingActionStatus.Approved),
        ]);
        (approve.Message, reject.Message).ShouldBe(($"Action '{claimed.Id}' is already executing.", $"Action '{claimed.Id}' is already executing."));
        (await queue.FindAsync(claimed.Id, CancellationToken.None))!.Status.ShouldBe(PendingActionStatus.Executing);
    }

    private static readonly JsonSerializerOptions FileJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [Fact]
    public async Task The_Open_Actions_Of_A_Feature_Are_Its_Approved_And_Executing_Ones_Oldest_First()
    {
        using var queue = Queue();
        var approved = await queue.EnqueueApprovedAsync(
            "youtube", [("comments.insert", "comment:c1", "Reply"), ("comments.insert", "comment:c2", "Reply"), ("videos.insert", "p1", "Upload")], CancellationToken.None);
        await queue.EnqueueApprovedAsync("github", [("issues.create", "repo", "Open")], CancellationToken.None);
        await queue.EnqueueAsync("youtube", "videos.update", "p2", "Waiting for the owner", CancellationToken.None);
        var claimed = (await queue.ClaimNextAsync("youtube", TimeSpan.FromMinutes(30), (_, _) => true, CancellationToken.None)).ShouldNotBeNull();
        await queue.ExecuteAsync(approved[2].Id, _ => Task.CompletedTask, CancellationToken.None);

        var open = await queue.ListOpenAsync("youtube", CancellationToken.None);

        open.Select(a => (a.Target, a.Status)).ShouldBe([("comment:c1", PendingActionStatus.Executing), ("comment:c2", PendingActionStatus.Approved)]);
        claimed.Target.ShouldBe("comment:c1");
    }

    [Fact]
    public async Task A_Released_Claim_Is_Approved_Again_Without_Its_Claim_Time_And_Only_A_Claimed_Action_Is_Released()
    {
        using var queue = Queue();
        var approved = await queue.EnqueueApprovedAsync(
            "youtube", [("videos.insert", "p1", "Upload"), ("videos.update", "p2", "Retitle")], CancellationToken.None);
        var claimed = (await queue.ClaimNextAsync("youtube", TimeSpan.FromMinutes(30), (_, _) => true, CancellationToken.None)).ShouldNotBeNull();

        await queue.ReleaseAsync(claimed.Id, CancellationToken.None);
        var released = await File.ReadAllBytesAsync(queue.FilePath);
        await queue.ReleaseAsync(approved[1].Id, CancellationToken.None);
        await queue.ReleaseAsync("no-such-action", CancellationToken.None);

        claimed.Id.ShouldBe(approved[0].Id);
        (await File.ReadAllBytesAsync(queue.FilePath)).ShouldBe(released);
        (await queue.ListAllAsync("youtube", CancellationToken.None)).Select(a => (a.Id, a.Status, a.ClaimedAt, a.DecidedAt)).ShouldBe([
            (approved[0].Id, PendingActionStatus.Approved, null, approved[0].DecidedAt),
            (approved[1].Id, PendingActionStatus.Approved, null, approved[1].DecidedAt),
        ]);
        (await queue.ClaimNextAsync("youtube", TimeSpan.FromMinutes(30), (_, _) => true, CancellationToken.None)).ShouldNotBeNull().Id.ShouldBe(approved[0].Id);
    }

    [Fact]
    public async Task A_Queue_File_Holding_Null_Is_Moved_Aside_Like_A_Corrupt_One_And_Never_Overwritten()
    {
        using var queue = Queue();
        var nullDocument = "null"u8.ToArray();
        await File.WriteAllBytesAsync(queue.FilePath, nullDocument);

        var pending = await queue.ListPendingAsync(CancellationToken.None);
        await queue.EnqueueAsync("youtube", "videos.insert", "a.mp4", "Upload A", CancellationToken.None);

        pending.ShouldBeEmpty();
        var movedAside = Directory.GetFiles(_directory, "actions.json.corrupt-*").ShouldHaveSingleItem();
        (await File.ReadAllBytesAsync(movedAside)).ShouldBe(nullDocument);
        (await queue.ListPendingAsync(CancellationToken.None)).ShouldHaveSingleItem().Target.ShouldBe("a.mp4");
        var (level, message) = _logger.Entries.ShouldHaveSingleItem();
        level.ShouldBe(LogLevel.Warning);
        message.ShouldBe($"Action queue file at {queue.FilePath} is malformed; moved it to {movedAside} and started an empty queue");
    }

    [Fact]
    public async Task An_Action_Keeps_Its_Group_And_Repository_Set_At_Enqueue_And_A_Close_Stamp_Across_A_Reopen()
    {
        string grouped, single;
        using (var queue = Queue())
        {
            grouped = (await queue.EnqueueAsync("github", "New issues", "GitHubActions.create_issue", "s", "{}", "turn-1", "acme/shop", CancellationToken.None)).Id;
            single = (await queue.EnqueueAsync("github", "New issues", "GitHubActions.create_issue", "s", "{}", CancellationToken.None)).Id;
            await queue.CloseGroupAsync("turn-1", CancellationToken.None);
        }

        using var reopened = Queue();
        var rows = await reopened.ListAllAsync("github", CancellationToken.None);

        var kept = rows.Single(a => a.Id == grouped);
        (kept.GroupId, kept.Repository).ShouldBe(("turn-1", "acme/shop"));
        kept.GroupClosedAt.ShouldNotBeNull();
        var ungrouped = rows.Single(a => a.Id == single);
        (ungrouped.GroupId, ungrouped.Repository, ungrouped.GroupClosedAt).ShouldBe((null, null, null));
    }

    [Fact]
    public async Task An_Actions_File_Written_Before_Groups_Existed_Still_Reads_Every_Row()
    {
        using var queue = Queue();
        await File.WriteAllTextAsync(queue.FilePath, """
            [
              {
                "id": "old1",
                "featureId": "github",
                "action": "New issues",
                "target": "GitHubActions.create_issue",
                "summary": "create_issue(owner=acme, repo=shop, title=Broken build, body)",
                "requestedAt": "2026-11-01T09:00:00+00:00",
                "status": "pending",
                "payload": "{\"body\":\"It fails.\",\"owner\":\"acme\",\"repo\":\"shop\",\"title\":\"Broken build\"}"
              }
            ]
            """);

        var old = (await queue.ListPendingAsync(CancellationToken.None)).ShouldHaveSingleItem();

        (old.Id, old.FeatureId, old.Action, old.Target, old.Status, old.RequestedAt).ShouldBe(
            ("old1", "github", "New issues", "GitHubActions.create_issue", PendingActionStatus.Pending, new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero)));
        old.Payload.ShouldBe("""{"body":"It fails.","owner":"acme","repo":"shop","title":"Broken build"}""");
        (old.GroupId, old.Repository, old.GroupClosedAt).ShouldBe((null, null, null));
        _logger.Entries.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Closing_A_Group_Without_Its_Id_Is_Refused_And_Writes_Nothing(string groupId)
    {
        using var queue = Queue();

        await Should.ThrowAsync<ArgumentException>(() => queue.CloseGroupAsync(groupId, CancellationToken.None));

        File.Exists(queue.FilePath).ShouldBeFalse();
    }

    [Theory]
    [InlineData("an action of another group")]
    [InlineData("an action already decided")]
    public async Task A_Set_Decision_Answering_An_Action_Outside_The_Group_Or_Not_Waiting_Decides_None_And_Writes_Nothing(string answered)
    {
        using var queue = Queue();
        var waiting = await queue.EnqueueAsync("github", "New issues", "GitHubActions.create_issue", "s", "{}", "turn-1", "acme/shop", CancellationToken.None);
        var decided = await queue.EnqueueAsync("github", "New issues", "GitHubActions.create_issue", "s", "{}", "turn-1", "acme/shop", CancellationToken.None);
        var other = await queue.EnqueueAsync("github", "New issues", "GitHubActions.create_issue", "s", "{}", "turn-2", "acme/shop", CancellationToken.None);
        await queue.RejectAsync(decided.Id, null, CancellationToken.None);
        var before = await File.ReadAllTextAsync(queue.FilePath);
        var wrong = answered == "an action of another group" ? other.Id : decided.Id;
        IReadOnlyDictionary<string, (bool Approve, string? Reason)> decisions =
            new Dictionary<string, (bool, string?)> { [waiting.Id] = (true, null), [wrong] = (true, null) };

        Task Decide() => queue.DecideSetAsync("turn-1", _ => Task.FromResult(decisions), CancellationToken.None);

        var failure = answered == "an action of another group"
            ? (Exception)await Should.ThrowAsync<PendingActionNotFoundException>(Decide)
            : await Should.ThrowAsync<PendingActionAlreadyDecidedException>(Decide);

        failure.Message.ShouldContain(wrong);
        (await File.ReadAllTextAsync(queue.FilePath)).ShouldBe(before);
    }

    [Fact]
    public async Task A_Set_Decision_Sees_Only_Its_Group_And_Decides_What_It_Answers_In_One_Write()
    {
        using var queue = Queue();
        var first = await queue.EnqueueAsync("github", "New issues", "GitHubActions.create_issue", "s", "{}", "turn-1", "acme/shop", CancellationToken.None);
        var second = await queue.EnqueueAsync("github", "New issues", "GitHubActions.create_issue", "s", "{}", "turn-1", "acme/shop", CancellationToken.None);
        var other = await queue.EnqueueAsync("github", "New issues", "GitHubActions.create_issue", "s", "{}", "turn-2", "acme/shop", CancellationToken.None);
        IReadOnlyList<string> seen = [];

        var after = await queue.DecideSetAsync(
            "turn-1",
            group =>
            {
                seen = [.. group.Select(a => a.Id)];
                return Task.FromResult<IReadOnlyDictionary<string, (bool Approve, string? Reason)>>(
                    new Dictionary<string, (bool, string?)> { [first.Id] = (true, "fine"), [second.Id] = (false, " ") });
            },
            CancellationToken.None);

        seen.ShouldBe([first.Id, second.Id]);
        after.Select(a => (a.Id, a.Status, a.Reason)).ShouldBe([(first.Id, PendingActionStatus.Approved, "fine"), (second.Id, PendingActionStatus.Rejected, (string?)null)]);
        (await queue.FindAsync(other.Id, CancellationToken.None)).ShouldNotBeNull().Status.ShouldBe(PendingActionStatus.Pending);
    }
}
