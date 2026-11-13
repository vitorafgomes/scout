using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;

namespace Scout.Audit.Tests;

public sealed class RepositoryAuditsTests : IDisposable
{
    private const string Sha = "abc1234def5678";
    private const string Taken = "Took a snapshot of acme/shop at main (abc1234): 4 files, 1 KB.";

    private readonly TempFolder _folder = new();

    public RepositoryAuditsTests()
    {
        Options = new AuditOptions { DbPath = Path.Combine(_folder.Path, "audit.db") };
    }

    private AuditOptions Options { get; }

    private FakeSnapshots Snapshots { get; } = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _folder.Dispose();
    }

    private void Snapshot(string sha, string takenAt, params string[] files)
    {
        foreach (var file in files)
        {
            _folder.File($"snapshots/acme__shop/{sha}/{file}");
        }

        Snapshots.Taken.Add(new AuditSnapshot(
            "acme", "shop", sha, Path.Combine(_folder.Path, "snapshots", "acme__shop", sha), DateTimeOffset.Parse(takenAt, CultureInfo.InvariantCulture)));
    }

    private RepositoryAudits Audits(Kernel kernel, SqliteAuditStore store, AuditSignal? signal = null, ILogger<RepositoryAudits>? logger = null) =>
        new(store, signal ?? new AuditSignal(), Options, Snapshots, new FakeEnvironment(), () => kernel, () => "local:session", logger ?? NullLogger<RepositoryAudits>.Instance);

    private SqliteAuditStore Store() => new(Options.DbPath, TimeProvider.System);

    [Fact]
    public async Task Starting_an_audit_answers_at_once_with_the_plan_and_keeps_the_job_with_the_origin_of_the_turn()
    {
        var calls = new List<string>();
        var kernel = Kernels.Audit(arguments =>
        {
            calls.Add($"{arguments["owner"]}/{arguments["repo"]}");
            Snapshot(Sha, "2026-11-07T10:00:00+00:00", "README.md", "src/Orders/Orders.csproj", "src/Orders/Checkout.cs", "tests/CheckoutTests.cs");
            return Taken;
        });
        var store = Store();
        var signal = new AuditSignal();

        var answer = await Audits(kernel, store, signal).StartAsync("acme", "shop", CancellationToken.None);

        answer.ShouldBe("Auditing acme/shop: 3 areas, 4 files; the first batch is running. Findings arrive here as each area is done.");
        calls.ShouldBe(["acme/shop"]);
        Snapshots.Asked.ShouldBe(["acme/shop@abc1234"]);
        var job = (await store.RunningJobsAsync(CancellationToken.None)).ShouldHaveSingleItem();
        (job.Owner, job.Repo, job.Commit, job.Origin).ShouldBe(("acme", "shop", Sha, "local:session"));
        (await store.BatchesAsync(job.Id, CancellationToken.None)).Select(b => b.Area).ShouldBe([AuditPlanner.ProjectFilesArea, "src", "tests"]);
        signal.WaitAsync(CancellationToken.None).IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public async Task A_refused_snapshot_starts_no_job_and_never_falls_back_to_an_older_snapshot()
    {
        const string refusal = "The owner has not allowed github to write; allow it on the Features screen of Scout Desktop.";
        Snapshot("0123456789ab", "2026-11-01T10:00:00+00:00", "src/Old.cs");
        var store = Store();

        var answer = await Audits(Kernels.Audit(_ => refusal), store).StartAsync("acme", "shop", CancellationToken.None);

        answer.ShouldBe(refusal);
        (await store.RunningJobsAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_snapshot_call_that_fails_is_answered_as_a_sentence_and_starts_no_job()
    {
        var store = Store();

        var answer = await Audits(Kernels.Audit(_ => throw new HttpRequestException("reset")), store).StartAsync("acme", "shop", CancellationToken.None);

        answer.ShouldBe("Scout could not take the snapshot of acme/shop (HttpRequestException).");
        (await store.RunningJobsAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true, "IOException")]
    [InlineData(false, "not found")]
    public async Task A_snapshot_the_lookup_cannot_return_starts_no_job_and_says_why(bool throws, string reason)
    {
        var kernel = Kernels.Audit(_ => Taken);
        Snapshots.Failure = throws ? new IOException("unreadable") : null;
        var store = Store();
        var logger = new RecordingLogger<RepositoryAudits>();

        var answer = await Audits(kernel, store, logger: logger).StartAsync("acme", "shop", CancellationToken.None);

        answer.ShouldBe($"The audit of acme/shop did not start: Scout could not read the snapshot it just took ({reason}).");
        Snapshots.Asked.ShouldBe(["acme/shop@abc1234"]);
        (await store.RunningJobsAsync(CancellationToken.None)).ShouldBeEmpty();
        logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task A_second_start_while_the_audit_runs_answers_with_its_progress_and_takes_no_snapshot()
    {
        var snapshots = 0;
        var kernel = Kernels.Audit(_ =>
        {
            snapshots++;
            Snapshot(Sha, "2026-11-07T10:00:00+00:00", "src/Orders/Checkout.cs");
            return Taken;
        });
        var store = Store();
        await Audits(kernel, store).StartAsync("acme", "shop", CancellationToken.None);

        var answer = await Audits(kernel, store).StartAsync("acme", "shop", CancellationToken.None);

        answer.ShouldBe("An audit of acme/shop is already running. 0 of 1 areas done, 0 not covered, 0 findings, 0 files read.");
        snapshots.ShouldBe(1);
    }

    [Fact]
    public async Task Continuing_a_paused_audit_resumes_it_from_the_first_area_not_done_without_a_new_snapshot()
    {
        var snapshots = 0;
        var kernel = Kernels.Audit(_ =>
        {
            snapshots++;
            return Taken;
        });
        var store = Store();
        var job = await store.CreateJobAsync("acme", "shop", null, Sha, "discord:1:2", [new("src", ["src/A.cs"], 10), new("tests", ["tests/T.cs"], 10)], null, CancellationToken.None);
        await store.CompleteBatchAsync(job, 1, ["src/A.cs"], [], CancellationToken.None);
        await store.PauseAsync(job, "the AI provider's usage limit was reached", CancellationToken.None);
        var signal = new AuditSignal();

        var answer = await Audits(kernel, store, signal).StartAsync("acme", "shop", CancellationToken.None);

        answer.ShouldBe("Resuming the audit of acme/shop from the first area not done. 1 of 2 areas done, 0 not covered, 0 findings, 1 files read. Findings arrive here as each area is done.");
        snapshots.ShouldBe(0);
        var running = (await store.RunningJobsAsync(CancellationToken.None)).ShouldHaveSingleItem();
        (running.Id, running.Origin).ShouldBe((job, "local:session"));
        signal.WaitAsync(CancellationToken.None).IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Theory]
    [InlineData("..", "shop")]
    [InlineData("acme", "../etc")]
    [InlineData("acme", "")]
    [InlineData("acme\n", "shop")]
    [InlineData("acme", "shop\n")]
    public async Task A_repository_name_that_could_leave_the_snapshots_folder_is_refused_before_any_call(string owner, string repo)
    {
        var snapshots = 0;
        var kernel = Kernels.Audit(_ =>
        {
            snapshots++;
            return Taken;
        });

        var answer = await Audits(kernel, Store()).StartAsync(owner, repo, CancellationToken.None);

        answer.ShouldBe(RepositoryAudits.InvalidRepository);
        snapshots.ShouldBe(0);
    }

    [Fact]
    public async Task Stopping_the_audit_writes_the_status_and_makes_no_call()
    {
        var store = Store();
        var job = await store.CreateJobAsync("acme", "shop", null, Sha, "local:session", [new("src", ["src/A.cs", "src/B.cs"], 20)], null, CancellationToken.None);
        var snapshots = 0;
        var audits = Audits(Kernels.Audit(_ =>
        {
            snapshots++;
            return Taken;
        }), store);

        var answer = await audits.StopAsync("acme", "shop", CancellationToken.None);

        answer.ShouldBe("Stopped the audit of acme/shop; an area in progress finishes first. The findings so far stay on the cards. 0 of 1 areas done, 0 not covered, 0 findings, 0 files read.");
        (await store.JobAsync(job, CancellationToken.None))!.Status.ShouldBe(AuditStatus.Stopped);
        snapshots.ShouldBe(0);
        (await audits.StopAsync("acme", "shop", CancellationToken.None)).ShouldBe("No audit of acme/shop is running.");
    }

    [Fact]
    public async Task How_the_audit_is_going_is_answered_from_the_jobs_rows()
    {
        var store = Store();
        var job = await store.CreateJobAsync("acme", "shop", null, Sha, "local:session", [new("src", ["src/A.cs"], 10), new("tests", ["tests/T.cs"], 10)], null, CancellationToken.None);
        await store.CompleteBatchAsync(job, 1, ["src/A.cs"], [new("a", "fix", "Title", "src/A.cs", 1, null, "Why.", "Fix.", null)], CancellationToken.None);

        var answer = await Audits(Kernels.Audit(), store).StatusAsync("acme", "shop", CancellationToken.None);

        answer.ShouldBe("The audit of acme/shop at abc1234 is running. 1 of 2 areas done, 0 not covered, 1 findings, 1 files read.");
    }

    [Fact]
    public async Task How_a_paused_audit_is_going_names_why_it_paused_until_it_resumes()
    {
        var store = Store();
        var job = await store.CreateJobAsync("acme", "shop", null, Sha, "local:session", [new("src", ["src/A.cs"], 10), new("tests", ["tests/T.cs"], 10)], null, CancellationToken.None);
        await store.PauseAsync(job, "the AI provider's usage limit was reached", CancellationToken.None);
        var audits = Audits(Kernels.Audit(), store);

        var paused = await audits.StatusAsync("acme", "shop", CancellationToken.None);
        await audits.StartAsync("acme", "shop", CancellationToken.None);
        var resumed = await audits.StatusAsync("acme", "shop", CancellationToken.None);

        paused.ShouldBe("The audit of acme/shop at abc1234 is paused: the AI provider's usage limit was reached. 0 of 2 areas done, 0 not covered, 0 findings, 0 files read.");
        resumed.ShouldBe("The audit of acme/shop at abc1234 is running. 0 of 2 areas done, 0 not covered, 0 findings, 0 files read.");
    }

    private const string AiSourceLimit = "the AI provider's usage limit was reached";
    private const string Progress = "1 of 2 areas done, 0 not covered, 1 findings, 1 files read.";
    private const string Continue = "Ask Scout to continue the audit of acme/shop to resume it from the first area not done.";
    private const string NotRunning = "No audit of acme/shop is running.";
    private const string Planned = "Auditing acme/shop: 2 areas, 2 files; the first batch is running. Findings arrive here as each area is done.";
    private const string Resuming = $"Resuming the audit of acme/shop from the first area not done. {Progress} Findings arrive here as each area is done.";
    private const string Stopped = $"Stopped the audit of acme/shop; an area in progress finishes first. The findings so far stay on the cards. {Progress}";
    private const string Paused = $"Paused the audit of acme/shop; an area in progress finishes first. The findings so far stay on the cards. {Progress} {Continue}";

    /// <summary>
    /// Every action on the repository's latest job in every state: the answer the owner reads, the job's status and pause
    /// reason after, and whether the worker is woken to run the first batch not done.
    /// </summary>
    public static TheoryData<string, string, string, string?, string?, bool> StateByAction => new()
    {
        { "none", "pause", NotRunning, null, null, false },
        { "running, a batch in progress", "pause", Paused, AuditStatus.Paused, "you paused it", false },
        { "running, between batches", "pause", Paused, AuditStatus.Paused, "you paused it", false },
        { "paused by the AI Source", "pause", $"The audit of acme/shop is already paused: {AiSourceLimit}. {Progress} {Continue}", AuditStatus.Paused, AiSourceLimit, false },
        { "paused by the owner", "pause", $"The audit of acme/shop is already paused: you paused it. {Progress} {Continue}", AuditStatus.Paused, "you paused it", false },
        { "stopped", "pause", NotRunning, AuditStatus.Stopped, null, false },
        { "done", "pause", NotRunning, AuditStatus.Done, null, false },

        { "none", "start", Planned, AuditStatus.Running, null, true },
        { "running, a batch in progress", "start", $"An audit of acme/shop is already running. {Progress}", AuditStatus.Running, null, true },
        { "running, between batches", "start", $"An audit of acme/shop is already running. {Progress}", AuditStatus.Running, null, true },
        { "paused by the AI Source", "start", Resuming, AuditStatus.Running, null, true },
        { "paused by the owner", "start", Resuming, AuditStatus.Running, null, true },
        { "stopped", "start", Planned, AuditStatus.Running, null, true },
        { "done", "start", Planned, AuditStatus.Running, null, true },

        { "none", "stop", NotRunning, null, null, false },
        { "running, a batch in progress", "stop", Stopped, AuditStatus.Stopped, null, false },
        { "running, between batches", "stop", Stopped, AuditStatus.Stopped, null, false },
        { "paused by the AI Source", "stop", Stopped, AuditStatus.Stopped, null, false },
        { "paused by the owner", "stop", Stopped, AuditStatus.Stopped, null, false },
        { "stopped", "stop", NotRunning, AuditStatus.Stopped, null, false },
        { "done", "stop", NotRunning, AuditStatus.Done, null, false },

        { "none", "status", "No audit of acme/shop has been started.", null, null, false },
        { "running, a batch in progress", "status", $"The audit of acme/shop at abc1234 is running. {Progress}", AuditStatus.Running, null, false },
        { "running, between batches", "status", $"The audit of acme/shop at abc1234 is running. {Progress}", AuditStatus.Running, null, false },
        { "paused by the AI Source", "status", $"The audit of acme/shop at abc1234 is paused: {AiSourceLimit}. {Progress}", AuditStatus.Paused, AiSourceLimit, false },
        { "paused by the owner", "status", $"The audit of acme/shop at abc1234 is paused: you paused it. {Progress}", AuditStatus.Paused, "you paused it", false },
        { "stopped", "status", $"The audit of acme/shop at abc1234 is stopped. {Progress}", AuditStatus.Stopped, null, false },
        { "done", "status", "The audit of acme/shop at abc1234 is done. 2 of 2 areas done, 0 not covered, 1 findings, 2 files read.", AuditStatus.Done, null, false },
    };

    [Theory]
    [MemberData(nameof(StateByAction))]
    public async Task Each_action_on_the_latest_audit_in_each_state_tells_the_owner_one_sentence_and_leaves_the_status_row_as_the_rules_say(
        string state, string action, string answer, string? status, string? reason, bool wakes)
    {
        var kernel = Kernels.Audit(_ =>
        {
            Snapshot("def5678abc1234", "2026-11-08T10:00:00+00:00", "src/A.cs", "tests/T.cs");
            return "Took a snapshot of acme/shop at main (def5678): 2 files, 1 KB.";
        });
        var store = Store();
        await Seed(store, state);
        var signal = new AuditSignal();
        var audits = Audits(kernel, store, signal);

        var told = action switch
        {
            "pause" => await audits.PauseAsync("acme", "shop", CancellationToken.None),
            "start" => await audits.StartAsync("acme", "shop", CancellationToken.None),
            "stop" => await audits.StopAsync("acme", "shop", CancellationToken.None),
            _ => await audits.StatusAsync("acme", "shop", CancellationToken.None),
        };

        told.ShouldBe(answer);
        var latest = await store.LatestJobAsync("acme", "shop", null, CancellationToken.None);
        (latest?.Status, latest?.PausedReason).ShouldBe((status, reason));
        signal.WaitAsync(CancellationToken.None).IsCompletedSuccessfully.ShouldBe(wakes, "the worker is woken only when a job is there to run");
    }

    private const string NothingToContinue = "No paused audit of acme/shop to continue.";

    /// <summary>
    /// Every control the desktop sends on every state of the repository's latest job, where the caller has no conversation:
    /// what the desktop is told, the status, pause reason and conversation of the job after, whether the worker is woken, and
    /// whether the control changed the job, which the desktop's record carries as its status.
    /// </summary>
    public static TheoryData<string, string, string, string?, string?, string?, bool, bool> DesktopControls => new()
    {
        { "none", "pause", NotRunning, null, null, null, false, false },
        { "running, a batch in progress", "pause", Paused, AuditStatus.Paused, "you paused it", "discord:1:2", false, true },
        { "running, between batches", "pause", Paused, AuditStatus.Paused, "you paused it", "discord:1:2", false, true },
        { "paused by the AI Source", "pause", $"The audit of acme/shop is already paused: {AiSourceLimit}. {Progress} {Continue}", AuditStatus.Paused, AiSourceLimit, "discord:1:2", false, false },
        { "paused by the owner", "pause", $"The audit of acme/shop is already paused: you paused it. {Progress} {Continue}", AuditStatus.Paused, "you paused it", "discord:1:2", false, false },
        { "stopped", "pause", NotRunning, AuditStatus.Stopped, null, "discord:1:2", false, false },
        { "done", "pause", NotRunning, AuditStatus.Done, null, "discord:1:2", false, false },

        { "none", "continue", NothingToContinue, null, null, null, false, false },
        { "running, a batch in progress", "continue", $"The audit of acme/shop is already running. {Progress}", AuditStatus.Running, null, "discord:1:2", false, false },
        { "running, between batches", "continue", $"The audit of acme/shop is already running. {Progress}", AuditStatus.Running, null, "discord:1:2", false, false },
        { "paused by the AI Source", "continue", Resuming, AuditStatus.Running, null, "discord:1:2", true, true },
        { "paused by the owner", "continue", Resuming, AuditStatus.Running, null, "discord:1:2", true, true },
        { "stopped", "continue", NothingToContinue, AuditStatus.Stopped, null, "discord:1:2", false, false },
        { "done", "continue", NothingToContinue, AuditStatus.Done, null, "discord:1:2", false, false },

        { "none", "stop", NotRunning, null, null, null, false, false },
        { "running, a batch in progress", "stop", Stopped, AuditStatus.Stopped, null, "discord:1:2", false, true },
        { "running, between batches", "stop", Stopped, AuditStatus.Stopped, null, "discord:1:2", false, true },
        { "paused by the AI Source", "stop", Stopped, AuditStatus.Stopped, null, "discord:1:2", false, true },
        { "paused by the owner", "stop", Stopped, AuditStatus.Stopped, null, "discord:1:2", false, true },
        { "stopped", "stop", NotRunning, AuditStatus.Stopped, null, "discord:1:2", false, false },
        { "done", "stop", NotRunning, AuditStatus.Done, null, "discord:1:2", false, false },
    };

    [Theory]
    [MemberData(nameof(DesktopControls))]
    public async Task Each_desktop_control_on_the_latest_audit_in_each_state_tells_the_desktop_one_sentence_and_keeps_the_jobs_conversation(
        string state, string action, string answer, string? status, string? reason, string? origin, bool wakes, bool applied)
    {
        var snapshots = 0;
        var store = Store();
        await Seed(store, state);
        var signal = new AuditSignal();
        var audits = new RepositoryAudits(store, signal, Options, Snapshots, new FakeEnvironment(), () => Kernels.Audit(_ =>
        {
            snapshots++;
            return Taken;
        }), () => null, NullLogger<RepositoryAudits>.Instance);

        var told = await audits.ControlAsync("acme", "shop", action, CancellationToken.None);

        told.ShouldBe(new AuditControlAnswer(applied, answer));
        var latest = await store.LatestJobAsync("acme", "shop", null, CancellationToken.None);
        (latest?.Status, latest?.PausedReason, latest?.Origin).ShouldBe((status, reason, origin));
        signal.WaitAsync(CancellationToken.None).IsCompletedSuccessfully.ShouldBe(wakes, "the worker is woken only when a job is there to run");
        snapshots.ShouldBe(0, "a desktop control never takes a snapshot nor starts an audit");
    }

    [Fact]
    public async Task A_control_with_an_action_the_audit_does_not_take_changes_nothing()
    {
        var store = Store();
        await Seed(store, "running, between batches");

        var told = await Audits(Kernels.Audit(), store).ControlAsync("acme", "shop", "restart", CancellationToken.None);

        told.ShouldBe(new AuditControlAnswer(false, "Scout does not restart an audit."));
        (await store.LatestJobAsync("acme", "shop", null, CancellationToken.None))!.Status.ShouldBe(AuditStatus.Running);
    }

    [Theory]
    [InlineData("..", "shop")]
    [InlineData("acme", "../etc")]
    [InlineData("acme", "")]
    public async Task Continuing_with_a_name_that_could_leave_the_snapshots_folder_is_refused(string owner, string repo)
    {
        (await Audits(Kernels.Audit(), Store()).ContinueAsync(owner, repo, CancellationToken.None)).ShouldBe(RepositoryAudits.InvalidRepository);
    }

    private static async Task Seed(SqliteAuditStore store, string state)
    {
        if (state == "none")
        {
            return;
        }

        var job = await store.CreateJobAsync("acme", "shop", null, Sha, "discord:1:2", [new("src", ["src/A.cs"], 10), new("tests", ["tests/T.cs"], 10)], null, CancellationToken.None);
        await store.CompleteBatchAsync(job, 1, ["src/A.cs"], [new("a", "fix", "Title", "src/A.cs", 1, null, "Why.", "Fix.", null)], CancellationToken.None);
        var settled = state switch
        {
            "running, a batch in progress" => store.BeginBatchAsync(job, 2, CancellationToken.None),
            "paused by the AI Source" => store.PauseAsync(job, AiSourceLimit, CancellationToken.None),
            "paused by the owner" => store.PauseAsync(job, "you paused it", CancellationToken.None),
            "stopped" => store.StopAsync("acme", "shop", null, CancellationToken.None),
            "done" => FinishAsync(store, job),
            _ => Task.CompletedTask,
        };
        await settled;
    }

    private static async Task FinishAsync(SqliteAuditStore store, string job)
    {
        await store.CompleteBatchAsync(job, 2, ["tests/T.cs"], [], CancellationToken.None);
        await store.FinishAsync(job, CancellationToken.None);
    }

    [Fact]
    public async Task Pausing_an_audit_the_AI_Source_paused_after_it_was_read_says_it_is_already_paused_and_keeps_the_AI_Sources_reason()
    {
        var store = Store();
        await Seed(store, "running, between batches");
        var racing = new RacingStore(store)
        {
            BeforePause = async () => await store.PauseAsync((await store.LatestJobAsync("acme", "shop", null, CancellationToken.None))!.Id, AiSourceLimit, CancellationToken.None),
        };

        var answer = await new RepositoryAudits(racing, new AuditSignal(), Options, Snapshots, new FakeEnvironment(), () => Kernels.Audit(), () => "local:session", NullLogger<RepositoryAudits>.Instance)
            .PauseAsync("acme", "shop", CancellationToken.None);

        answer.ShouldBe($"The audit of acme/shop is already paused: {AiSourceLimit}. {Progress} {Continue}");
        var latest = (await store.LatestJobAsync("acme", "shop", null, CancellationToken.None))!;
        (latest.Status, latest.PausedReason).ShouldBe((AuditStatus.Paused, AiSourceLimit));
    }

    [Fact]
    public async Task A_pause_the_database_refuses_to_save_is_told_as_a_sentence_and_leaves_the_audit_running_as_it_was()
    {
        var store = Store();
        await Seed(store, "running, between batches");
        var audits = Audits(Kernels.Audit(), store);
        await audits.StatusAsync("acme", "shop", CancellationToken.None);
        SqliteConnection.ClearAllPools();
        File.SetAttributes(Options.DbPath, FileAttributes.ReadOnly);

        var answer = await audits.PauseAsync("acme", "shop", CancellationToken.None);

        answer.ShouldBe("Scout could not save the audit database (SQLite error 8).");
        SqliteConnection.ClearAllPools();
        File.SetAttributes(Options.DbPath, FileAttributes.Normal);
        var latest = (await Store().LatestJobAsync("acme", "shop", null, CancellationToken.None))!;
        (latest.Status, latest.PausedReason).ShouldBe((AuditStatus.Running, null));
        (await Store().BatchesAsync(latest.Id, CancellationToken.None)).Select(b => b.Status).ShouldBe([AuditStatus.Done, AuditStatus.Pending]);
    }

    [Fact]
    public async Task A_database_that_cannot_be_opened_is_answered_as_a_sentence_and_takes_no_snapshot()
    {
        Directory.CreateDirectory(Options.DbPath);
        var snapshots = 0;
        var kernel = Kernels.Audit(_ =>
        {
            snapshots++;
            return Taken;
        });

        var answer = await Audits(kernel, Store()).StartAsync("acme", "shop", CancellationToken.None);

        answer.ShouldBe("Scout could not read the audit database (SQLite error 14).");
        snapshots.ShouldBe(0);
    }
}
