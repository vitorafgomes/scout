using Microsoft.Extensions.Options;
using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Controls;
using Scout.ControlPlane.Features;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Controls;

public sealed class OwnerControlServiceTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Directory.CreateTempSubdirectory("scout-controls-").FullName;
    private readonly MovingTime _time = new(T0);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private IOptions<FeatureStateStoreOptions> Options() =>
        Microsoft.Extensions.Options.Options.Create(new FeatureStateStoreOptions { FilePath = Path.Combine(_directory, "features.json") });

    private JsonFileOwnerControlQueue Queue() => new(Options(), new RecordingLogger<JsonFileOwnerControlQueue>(), _time);

    private JsonLinesWriteLog Log() => new(Options(), new RecordingLogger<JsonLinesWriteLog>());

    private OwnerControlService Service(IOwnerControlQueue? queue = null, IWriteLog? log = null) =>
        new(queue ?? Queue(), log ?? Log(), new RecordingLogger<OwnerControlService>(), _time);

    public static TheoryData<string?, string?, string?, int?, string?, string> Refused => new()
    {
        { "build", "acme", "shop", null, "stop", OwnerControlService.UnknownKind },
        { null, "acme", "shop", null, "stop", OwnerControlService.UnknownKind },
        { "audit", "acme", "shop", null, "restart", OwnerControlService.UnknownAction },
        { "audit", "acme", "shop", null, null, OwnerControlService.UnknownAction },
        { "audit", "..", "shop", null, "pause", OwnerControlService.InvalidRepository },
        { "audit", "acme", "../etc", null, "pause", OwnerControlService.InvalidRepository },
        { "audit", "acme", "", null, "pause", OwnerControlService.InvalidRepository },
        { "audit", null, "shop", null, "pause", OwnerControlService.InvalidRepository },
        { "audit", "acme\n", "shop", null, "pause", OwnerControlService.InvalidRepository },
        { "audit", "acme", "shop\n", null, "pause", OwnerControlService.InvalidRepository },
        { "tests", "acme", "shop", 42, "pause", OwnerControlService.TestsStopOnly },
        { "tests", "acme", "shop", 42, "continue", OwnerControlService.TestsStopOnly },
        { "tests", "acme", "shop", null, "stop", OwnerControlService.TestsNeedNumber },
        { "tests", "acme", "shop", 0, "stop", OwnerControlService.TestsNeedNumber },
        { "audit", "acme", "shop", 42, "stop", OwnerControlService.AuditHasNoNumber },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task A_control_that_does_not_name_a_job_or_an_action_that_applies_is_refused_with_its_reason_and_nothing_is_queued(
        string? kind, string? owner, string? repo, int? number, string? action, string reason)
    {
        var service = Service();

        var answer = await service.RequestAsync(kind, owner, repo, number, action, CancellationToken.None);

        answer.ShouldBe(new OwnerControlAnswer(false, reason));
        (await service.TakeNextAsync(CancellationToken.None)).ShouldBeNull();
    }

    [Theory]
    [InlineData("audit", null, "pause", "acme/shop")]
    [InlineData("audit", null, "continue", "acme/shop")]
    [InlineData("audit", null, "stop", "acme/shop")]
    [InlineData("tests", 42, "stop", "acme/shop#42")]
    public async Task A_control_that_applies_is_queued_and_handed_to_the_worker_once(string kind, int? number, string action, string target)
    {
        var service = Service();

        var answer = await service.RequestAsync(kind, "acme", "shop", number, action, CancellationToken.None);
        var taken = await service.TakeNextAsync(CancellationToken.None);
        var again = await service.TakeNextAsync(CancellationToken.None);

        (answer.Accepted, answer.Reason).ShouldBe((true, OwnerControlService.Queued));
        taken.ShouldNotBeNull();
        (taken.Id, taken.Kind, taken.Target, taken.Action).ShouldBe((answer.ControlId, kind, target, action));
        again.ShouldBeNull();
    }

    [Fact]
    public async Task After_a_restart_a_taken_control_is_never_handed_out_again_and_one_not_taken_still_is()
    {
        var first = await Service().RequestAsync("audit", "acme", "shop", null, "pause", CancellationToken.None);
        var second = await Service().RequestAsync("tests", "acme", "shop", 7, "stop", CancellationToken.None);
        (await Service().TakeNextAsync(CancellationToken.None))!.Id.ShouldBe(first.ControlId);

        var afterRestart = Service();

        (await afterRestart.TakeNextAsync(CancellationToken.None))!.Id.ShouldBe(second.ControlId);
        (await afterRestart.TakeNextAsync(CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task A_control_older_than_two_minutes_when_the_worker_asks_is_not_handed_out_and_is_recorded_as_not_applied()
    {
        var log = Log();
        var service = Service(log: log);
        var stale = await service.RequestAsync("tests", "acme", "shop", 42, "stop", CancellationToken.None);
        _time.Advance(OwnerControlService.MaxAge + TimeSpan.FromSeconds(1));
        var fresh = await service.RequestAsync("audit", "acme", "shop", null, "pause", CancellationToken.None);

        var taken = await service.TakeNextAsync(CancellationToken.None);

        taken!.Id.ShouldBe(fresh.ControlId);
        (await log.ListAsync(10, CancellationToken.None)).ShouldHaveSingleItem().ShouldBe(
            new WriteLogEntry(_time.GetUtcNow(), "github", "Stop tests", "acme/shop#42", OwnerControlService.Expired, "owner", stale.ControlId, ControlStatus: "expired"));
    }

    [Fact]
    public async Task A_settled_control_is_kept_once_in_the_write_log_as_the_owners_with_the_workers_sentence()
    {
        var log = Log();
        var service = Service(log: log);
        var asked = await service.RequestAsync("audit", "acme", "shop", null, "pause", CancellationToken.None);
        await service.TakeNextAsync(CancellationToken.None);

        var recorded = await service.SettleAsync(asked.ControlId!, "applied", "Paused the audit of acme/shop; an area in progress finishes first.", CancellationToken.None);
        var again = await service.SettleAsync(asked.ControlId!, "applied", "Paused twice.", CancellationToken.None);
        var unknown = await service.SettleAsync("nope", "applied", "Paused.", CancellationToken.None);

        (recorded, again, unknown).ShouldBe((true, false, false));
        (await log.ListAsync(10, CancellationToken.None)).ShouldHaveSingleItem().ShouldBe(
            new WriteLogEntry(_time.GetUtcNow(), "github", "Pause audit", "acme/shop", "Paused the audit of acme/shop; an area in progress finishes first.", "owner", asked.ControlId, ControlStatus: "applied"));
    }

    [Fact]
    public async Task A_control_the_worker_took_and_never_settled_is_recorded_as_not_confirmed_and_a_late_settle_records_nothing_more()
    {
        var log = Log();
        var service = Service(log: log);
        var asked = await service.RequestAsync("tests", "acme", "shop", 42, "stop", CancellationToken.None);
        await service.TakeNextAsync(CancellationToken.None);
        _time.Advance(OwnerControlService.MaxAge + TimeSpan.FromSeconds(1));

        var next = await service.TakeNextAsync(CancellationToken.None);
        var late = await service.SettleAsync(asked.ControlId!, "applied", "Stopped the tests of acme/shop#42; no result was kept.", CancellationToken.None);

        next.ShouldBeNull();
        late.ShouldBeFalse();
        (await log.ListAsync(10, CancellationToken.None)).ShouldHaveSingleItem().ShouldBe(
            new WriteLogEntry(_time.GetUtcNow(), "github", "Stop tests", "acme/shop#42", OwnerControlService.NotConfirmed, "owner", asked.ControlId, ControlStatus: "not confirmed"));
    }

    [Fact]
    public async Task A_control_settled_in_time_is_not_swept()
    {
        var log = Log();
        var service = Service(log: log);
        var asked = await service.RequestAsync("audit", "acme", "shop", null, "stop", CancellationToken.None);
        await service.TakeNextAsync(CancellationToken.None);
        await service.SettleAsync(asked.ControlId!, "applied", "Stopped the audit of acme/shop; an area in progress finishes first.", CancellationToken.None);
        _time.Advance(OwnerControlService.MaxAge + TimeSpan.FromSeconds(1));

        await service.TakeNextAsync(CancellationToken.None);

        (await log.ListAsync(10, CancellationToken.None)).ShouldHaveSingleItem().Outcome.ShouldBe("Stopped the audit of acme/shop; an area in progress finishes first.");
    }

    [Fact]
    public async Task A_control_not_taken_yet_cannot_be_settled()
    {
        var log = Log();
        var service = Service(log: log);
        var asked = await service.RequestAsync("audit", "acme", "shop", null, "stop", CancellationToken.None);

        (await service.SettleAsync(asked.ControlId!, "applied", "Stopped.", CancellationToken.None)).ShouldBeFalse();
        (await log.ListAsync(10, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_store_that_cannot_keep_the_control_is_answered_with_a_reason_and_nothing_is_queued()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "controls.json"));
        var service = Service();

        var answer = await service.RequestAsync("audit", "acme", "shop", null, "pause", CancellationToken.None);

        answer.Accepted.ShouldBeFalse();
        answer.Reason.ShouldStartWith("Scout could not keep this control (");
        answer.Reason.ShouldEndWith("); nothing was queued, try again.");
        answer.ControlId.ShouldBeNull();
        Directory.Exists(Path.Combine(_directory, "controls.json")).ShouldBeTrue("the folder in the file's place was left as it was");
    }

    public static TheoryData<string, string?, string> StatusByEnding => new()
    {
        { "the worker applied it", "applied", "applied" },
        { "it changed nothing", "not applied", "not applied" },
        { "the worker could not tell", "not confirmed", "not confirmed" },
        { "it waited past two minutes", null, "expired" },
        { "the worker took it and never answered", null, "not confirmed" },
    };

    [Theory]
    [MemberData(nameof(StatusByEnding))]
    public async Task Each_control_record_carries_one_closed_status_the_desktop_can_colour(string ending, string? workerStatus, string status)
    {
        var log = Log();
        var service = Service(log: log);
        var asked = await service.RequestAsync("audit", "acme", "shop", null, "pause", CancellationToken.None);
        if (ending == "it waited past two minutes")
        {
            _time.Advance(OwnerControlService.MaxAge + TimeSpan.FromSeconds(1));
        }

        await service.TakeNextAsync(CancellationToken.None);
        if (workerStatus is not null)
        {
            await service.SettleAsync(asked.ControlId!, workerStatus, "The sentence.", CancellationToken.None);
        }
        else if (ending == "the worker took it and never answered")
        {
            _time.Advance(OwnerControlService.MaxAge + TimeSpan.FromSeconds(1));
            await service.TakeNextAsync(CancellationToken.None);
        }

        (await log.ListAsync(10, CancellationToken.None)).ShouldHaveSingleItem().ControlStatus.ShouldBe(status, ending);
    }

    [Theory]
    [InlineData("")]
    [InlineData("expired")]
    [InlineData("done")]
    public async Task A_settle_with_a_status_the_worker_does_not_give_is_refused_and_records_nothing(string status)
    {
        var log = Log();
        var service = Service(log: log);
        var asked = await service.RequestAsync("audit", "acme", "shop", null, "pause", CancellationToken.None);
        await service.TakeNextAsync(CancellationToken.None);

        await Should.ThrowAsync<ArgumentException>(() => service.SettleAsync(asked.ControlId!, status, "Paused.", CancellationToken.None));

        (await log.ListAsync(10, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_record_the_log_refuses_leaves_the_control_unsettled_and_a_retry_ends_with_exactly_one_record()
    {
        var log = new FlakyLog(Log()) { AppendFailures = 1 };
        var queue = Queue();
        var service = Service(queue, log);
        var asked = await service.RequestAsync("audit", "acme", "shop", null, "stop", CancellationToken.None);
        await service.TakeNextAsync(CancellationToken.None);

        await Should.ThrowAsync<IOException>(() => service.SettleAsync(asked.ControlId!, "applied", "Stopped the audit of acme/shop.", CancellationToken.None));
        var unsettled = await queue.UnsettledAsync(DateTimeOffset.MaxValue, CancellationToken.None);
        var retried = await service.SettleAsync(asked.ControlId!, "applied", "Stopped the audit of acme/shop.", CancellationToken.None);

        unsettled.Select(c => c.Id).ShouldBe([asked.ControlId!]);
        retried.ShouldBeTrue();
        (await log.ListAsync(10, CancellationToken.None)).Select(e => (e.ActionId, e.Outcome, e.ControlStatus))
            .ShouldBe([(asked.ControlId, "Stopped the audit of acme/shop.", "applied")]);
        (await queue.UnsettledAsync(DateTimeOffset.MaxValue, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_record_kept_whose_mark_failed_is_recognised_by_its_control_id_and_never_appended_twice()
    {
        var log = Log();
        var queue = new FlakyQueue(Queue()) { SettleFailures = 1 };
        var service = Service(queue, log);
        var asked = await service.RequestAsync("audit", "acme", "shop", null, "stop", CancellationToken.None);
        await service.TakeNextAsync(CancellationToken.None);

        await Should.ThrowAsync<IOException>(() => service.SettleAsync(asked.ControlId!, "applied", "Stopped the audit of acme/shop.", CancellationToken.None));
        var retried = await service.SettleAsync(asked.ControlId!, "applied", "Stopped the audit of acme/shop.", CancellationToken.None);

        retried.ShouldBeTrue();
        (await log.ListAsync(10, CancellationToken.None)).Count(e => e.ActionId == asked.ControlId).ShouldBe(1);
        (await queue.UnsettledAsync(DateTimeOffset.MaxValue, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_sweep_that_cannot_read_the_controls_taken_earlier_never_refuses_a_new_click()
    {
        var queue = new FlakyQueue(Queue()) { UnsettledFailures = 1 };
        var service = Service(queue);

        var answer = await service.RequestAsync("audit", "acme", "shop", null, "pause", CancellationToken.None);

        (answer.Accepted, answer.Reason).ShouldBe((true, OwnerControlService.Queued));
        (await service.TakeNextAsync(CancellationToken.None))!.Id.ShouldBe(answer.ControlId);
    }

    [Fact]
    public async Task An_idle_take_reads_the_queue_once()
    {
        var queue = new FlakyQueue(Queue());
        var service = Service(queue);

        (await service.TakeNextAsync(CancellationToken.None)).ShouldBeNull();

        queue.Reads.ShouldBe(1, "the worker asks every two seconds at most; an idle ask costs one read of controls.json");
    }

    [Fact]
    public async Task An_outcome_longer_than_2000_characters_is_kept_cut_at_2000()
    {
        var log = Log();
        var service = Service(log: log);
        var asked = await service.RequestAsync("audit", "acme", "shop", null, "pause", CancellationToken.None);
        await service.TakeNextAsync(CancellationToken.None);

        await service.SettleAsync(asked.ControlId!, "applied", new string('x', 2_500), CancellationToken.None);

        (await log.ListAsync(10, CancellationToken.None)).ShouldHaveSingleItem().Outcome.ShouldBe(new string('x', 2_000));
    }

    [Fact]
    public async Task A_malformed_controls_file_is_moved_aside_and_the_desktop_is_told_its_click_was_queued()
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, "controls.json"), "{ not json");
        var service = Service();

        var answer = await service.RequestAsync("audit", "acme", "shop", null, "pause", CancellationToken.None);

        (answer.Accepted, answer.Reason).ShouldBe((true, OwnerControlService.Queued));
        Directory.GetFiles(_directory, "controls.json.corrupt-*").ShouldHaveSingleItem();
        (await File.ReadAllTextAsync(Directory.GetFiles(_directory, "controls.json.corrupt-*")[0])).ShouldBe("{ not json");
        (await service.TakeNextAsync(CancellationToken.None))!.Id.ShouldBe(answer.ControlId);
    }

    [Fact]
    public async Task A_control_settled_more_than_a_day_ago_is_dropped_from_the_file_and_one_never_settled_is_kept()
    {
        var queue = Queue();
        var service = Service(queue);
        var settled = await service.RequestAsync("audit", "acme", "shop", null, "pause", CancellationToken.None);
        await service.TakeNextAsync(CancellationToken.None);
        await service.SettleAsync(settled.ControlId!, "applied", "Paused.", CancellationToken.None);
        var waiting = await service.RequestAsync("tests", "acme", "shop", 7, "stop", CancellationToken.None);
        _time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));

        await queue.EnqueueAsync(new OwnerControl("later", "audit", "acme", "shop", null, "stop", _time.GetUtcNow()), CancellationToken.None);

        var kept = await File.ReadAllTextAsync(queue.FilePath);
        kept.ShouldNotContain(settled.ControlId!);
        kept.ShouldContain(waiting.ControlId!);
        kept.ShouldContain("later");
    }

    private sealed class FlakyLog(IWriteLog inner) : IWriteLog
    {
        public int AppendFailures { get; set; }

        public Task AppendAsync(WriteLogEntry entry, CancellationToken ct) =>
            AppendFailures-- > 0 ? throw new IOException("the log is locked") : inner.AppendAsync(entry, ct);

        public Task<IReadOnlyList<WriteLogEntry>> ListAsync(int limit, CancellationToken ct) => inner.ListAsync(limit, ct);

        public Task<IReadOnlyList<WriteLogEntry>> ListSinceAsync(DateTimeOffset since, CancellationToken ct) => inner.ListSinceAsync(since, ct);
    }

    private sealed class FlakyQueue(IOwnerControlQueue inner) : IOwnerControlQueue
    {
        public int SettleFailures { get; set; }

        public int UnsettledFailures { get; set; }

        public int Reads { get; private set; }

        public Task EnqueueAsync(OwnerControl control, CancellationToken ct) => inner.EnqueueAsync(control, ct);

        public Task<(OwnerControl? Taken, IReadOnlyList<OwnerControl> Lapsed)> TakeNextAsync(DateTimeOffset now, DateTimeOffset lapsedBefore, CancellationToken ct)
        {
            Reads++;
            return inner.TakeNextAsync(now, lapsedBefore, ct);
        }

        public Task<OwnerControl?> SettleAsync(string id, DateTimeOffset now, CancellationToken ct) =>
            SettleFailures-- > 0 ? throw new IOException("the queue is locked") : inner.SettleAsync(id, now, ct);

        public Task<IReadOnlyList<OwnerControl>> UnsettledAsync(DateTimeOffset before, CancellationToken ct)
        {
            Reads++;
            return UnsettledFailures-- > 0 ? throw new IOException("the queue is locked") : inner.UnsettledAsync(before, ct);
        }
    }

    private sealed class MovingTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public async Task A_Control_File_Holding_Null_Is_Moved_Aside_Like_A_Malformed_One_And_The_Queue_Starts_Empty()
    {
        var logger = new RecordingLogger<JsonFileOwnerControlQueue>();
        using var queue = new JsonFileOwnerControlQueue(Options(), logger, _time);
        var nullDocument = "null"u8.ToArray();
        await File.WriteAllBytesAsync(queue.FilePath, nullDocument);

        var unsettled = await queue.UnsettledAsync(T0.AddDays(1), CancellationToken.None);

        unsettled.ShouldBeEmpty();
        var movedAside = Directory.GetFiles(_directory, "controls.json.corrupt-*").ShouldHaveSingleItem();
        (await File.ReadAllBytesAsync(movedAside)).ShouldBe(nullDocument);
        logger.Entries.ShouldHaveSingleItem().Message
            .ShouldBe($"Owner control file at {queue.FilePath} is malformed; moved it to {movedAside} and started an empty queue");
    }
}
