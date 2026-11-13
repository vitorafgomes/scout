using System.Collections.Concurrent;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Scout.Audit;
using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Contracts;
using Scout.ControlPlane.Controls;
using Scout.ControlPlane.Features;
using Scout.Conversation.Abstractions;
using Scout.Plugin.GitHub;
using Shouldly;
using Xunit;
using DomainControl = Scout.Host.Console.JobControl;

namespace Scout.Host.Console.Tests;

/// <summary>
/// The worker applies the owner's clicks in Scout Desktop on running jobs: an audit control through the audit library, a
/// test run's stop through the registry of runs in progress, each taken once and settled once with its status and sentence.
/// </summary>
public sealed class JobControlPollerTests
{
    private readonly ScriptedControls _controls = new();
    private readonly Scout.Audit.IRepositoryAudits _audits = Substitute.For<Scout.Audit.IRepositoryAudits>();
    private readonly RunningTests _running = new();
    private readonly PostedNotices _notices = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));

    private JobControlPoller Poller(Scout.Audit.IRepositoryAudits? audits = null, IJobControls? controls = null, RunningTests? tests = null) =>
        new(controls ?? _controls, _notices, NullLogger<JobControlPoller>.Instance, audits ?? _audits, tests ?? _running, _time);

    private RunningTests.Run RunThatEnds(bool stopped, string? origin = "local:session")
    {
        var run = _running.Start("acme", "shop", 42, origin, CancellationToken.None);
        run.Token.Register(() =>
        {
            run.End(stopped);
            run.Dispose();
        });
        return run;
    }

    public static TheoryData<string, bool, string> AuditControls => new()
    {
        { "pause", true, "applied" },
        { "pause", false, "not applied" },
        { "continue", true, "applied" },
        { "continue", false, "not applied" },
        { "stop", true, "applied" },
        { "stop", false, "not applied" },
    };

    [Theory]
    [MemberData(nameof(AuditControls))]
    public async Task An_audit_control_is_applied_by_the_audit_library_and_settled_once_with_its_status_and_sentence(string action, bool changed, string status)
    {
        _audits.ControlAsync("acme", "shop", action, Arg.Any<CancellationToken>()).Returns(new AuditControlAnswer(changed, $"The {action} sentence."));
        _controls.Waiting.Enqueue(new DomainControl("c1", "audit", "acme", "shop", null, action));

        var ran = await Poller().RunOnceAsync(CancellationToken.None);
        var again = await Poller().RunOnceAsync(CancellationToken.None);

        (ran, again).ShouldBe((true, false));
        _controls.Settled.ShouldBe([("c1", status, $"The {action} sentence.")]);
    }

    [Fact]
    public async Task Without_audits_an_audit_control_is_settled_as_not_applied()
    {
        _controls.Waiting.Enqueue(new DomainControl("c1", "audit", "acme", "shop", null, "pause"));

        await new JobControlPoller(_controls, _notices, NullLogger<JobControlPoller>.Instance, audits: null, _running, _time).RunOnceAsync(CancellationToken.None);

        _controls.Settled.ShouldBe([("c1", "not applied", JobControlPoller.NoAudits)]);
    }

    /// <summary>A stop on a test run, by how the runs of that pull request ended: the status, the sentence and the notices.</summary>
    public static TheoryData<string, string, string, (string Origin, string Text)[]> TestStops => new()
    {
        { "cut short", "applied", "Stopped the tests of acme/shop#42; no result was kept.", [("local:session", "The tests of acme/shop#42 ended at your request; no result was kept.")] },
        { "finished as the stop came", "not applied", "Not applied: the tests of acme/shop#42 had already finished; their result stands.", [] },
        { "none running", "not applied", "Not applied: no test run of acme/shop#42 is running.", [] },
    };

    [Theory]
    [MemberData(nameof(TestStops))]
    public async Task A_stop_on_a_test_run_is_settled_by_how_the_run_ended_and_only_a_run_cut_short_gets_a_notice(string ending, string status, string sentence, (string Origin, string Text)[] notices)
    {
        if (ending != "none running")
        {
            RunThatEnds(stopped: ending == "cut short");
        }

        _controls.Waiting.Enqueue(new DomainControl("c1", "tests", "acme", "shop", 42, "stop"));

        await Poller().RunOnceAsync(CancellationToken.None);

        _controls.Settled.ShouldBe([("c1", status, sentence)]);
        _notices.Posts.ShouldBe(notices);
    }

    [Fact]
    public async Task Stopping_twice_and_stopping_another_number_settle_as_not_applied_without_a_notice()
    {
        RunThatEnds(stopped: true);
        _controls.Waiting.Enqueue(new DomainControl("c1", "tests", "acme", "shop", 42, "stop"));
        _controls.Waiting.Enqueue(new DomainControl("c2", "tests", "acme", "shop", 42, "stop"));
        _controls.Waiting.Enqueue(new DomainControl("c3", "tests", "acme", "shop", 7, "stop"));

        while (await Poller().RunOnceAsync(CancellationToken.None))
        {
        }

        _notices.Posts.Count.ShouldBe(1);
        _controls.Settled.ShouldBe(
        [
            ("c1", "applied", "Stopped the tests of acme/shop#42; no result was kept."),
            ("c2", "not applied", "Not applied: no test run of acme/shop#42 is running."),
            ("c3", "not applied", "Not applied: no test run of acme/shop#7 is running."),
        ]);
    }

    [Fact]
    public async Task A_run_that_does_not_end_within_90_seconds_of_the_stop_is_settled_as_not_confirmed()
    {
        using var run = _running.Start("acme", "shop", 42, "local:session", CancellationToken.None);
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Token.Register(() => asked.TrySetResult());
        _controls.Waiting.Enqueue(new DomainControl("c1", "tests", "acme", "shop", 42, "stop"));

        var once = Poller().RunOnceAsync(CancellationToken.None);
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _time.Advance(JobControlPoller.StopWait + TimeSpan.FromSeconds(1));
        await once.WaitAsync(TimeSpan.FromSeconds(10));

        _controls.Settled.ShouldBe([("c1", "not confirmed", "Not confirmed: Scout asked the tests of acme/shop#42 to stop and they did not end within 90 seconds.")]);
        _notices.Posts.ShouldBeEmpty();
    }

    [Fact]
    public async Task After_a_worker_restart_a_stop_for_a_run_that_no_longer_exists_is_settled_as_not_applied()
    {
        _controls.Waiting.Enqueue(new DomainControl("c1", "tests", "acme", "shop", 42, "stop"));

        await Poller(tests: new RunningTests()).RunOnceAsync(CancellationToken.None);

        _controls.Settled.ShouldBe([("c1", "not applied", "Not applied: no test run of acme/shop#42 is running.")]);
        _notices.Posts.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_take_that_fails_loses_nothing_and_the_control_is_applied_once_when_the_control_plane_answers_again()
    {
        _audits.ControlAsync("acme", "shop", "pause", Arg.Any<CancellationToken>()).Returns(new AuditControlAnswer(true, "Paused."));
        _controls.Waiting.Enqueue(new DomainControl("c1", "audit", "acme", "shop", null, "pause"));
        _controls.TakeFailure = new RpcException(new Status(StatusCode.Unavailable, "down"));
        var poller = Poller();

        var whileDown = await poller.RunOnceAsync(CancellationToken.None);
        _controls.TakeFailure = null;
        var afterwards = await poller.RunOnceAsync(CancellationToken.None);
        var then = await poller.RunOnceAsync(CancellationToken.None);

        (whileDown, afterwards, then).ShouldBe((false, true, false));
        _controls.Settled.ShouldBe([("c1", "applied", "Paused.")]);
        await _audits.Received(1).ControlAsync("acme", "shop", "pause", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_settle_that_fails_after_the_control_was_applied_is_retried_without_applying_it_again()
    {
        _audits.ControlAsync("acme", "shop", "stop", Arg.Any<CancellationToken>()).Returns(new AuditControlAnswer(true, "Stopped."));
        _controls.Waiting.Enqueue(new DomainControl("c1", "audit", "acme", "shop", null, "stop"));
        _controls.SettleFailures = 1;
        var poller = Poller();

        var first = await poller.RunOnceAsync(CancellationToken.None);
        var retry = await poller.RunOnceAsync(CancellationToken.None);
        var then = await poller.RunOnceAsync(CancellationToken.None);

        (first, retry, then).ShouldBe((true, true, false));
        await _audits.Received(1).ControlAsync("acme", "shop", "stop", Arg.Any<CancellationToken>());
        _controls.Settled.ShouldBe([("c1", "applied", "Stopped.")]);
    }

    [Fact]
    public async Task A_settle_the_control_plane_keeps_refusing_is_dropped_after_two_minutes_and_the_next_click_is_taken()
    {
        _audits.ControlAsync("acme", "shop", "stop", Arg.Any<CancellationToken>()).Returns(new AuditControlAnswer(true, "Stopped."));
        _audits.ControlAsync("acme", "shop", "pause", Arg.Any<CancellationToken>()).Returns(new AuditControlAnswer(false, "No audit of acme/shop is running."));
        _controls.Waiting.Enqueue(new DomainControl("c1", "audit", "acme", "shop", null, "stop"));
        _controls.Waiting.Enqueue(new DomainControl("c2", "audit", "acme", "shop", null, "pause"));
        _controls.SettleFailures = 1_000;
        var poller = Poller();

        await poller.RunOnceAsync(CancellationToken.None);
        await poller.RunOnceAsync(CancellationToken.None);
        _time.Advance(JobControlPoller.PendingSettleLimit + TimeSpan.FromSeconds(1));
        _controls.SettleFailures = 0;
        var dropped = await poller.RunOnceAsync(CancellationToken.None);
        var next = await poller.RunOnceAsync(CancellationToken.None);

        (dropped, next).ShouldBe((false, true));
        _controls.Settled.ShouldBe([("c2", "not applied", "No audit of acme/shop is running.")], "c1's record is the control plane's to keep: its sweep records it as not confirmed");
        await _audits.Received(1).ControlAsync("acme", "shop", "stop", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_poll_is_fast_while_clicks_arrive_slows_after_a_quiet_minute_or_while_the_control_plane_is_down_and_speeds_up_on_a_click()
    {
        _audits.ControlAsync("acme", "shop", "pause", Arg.Any<CancellationToken>()).Returns(new AuditControlAnswer(true, "Paused."));
        var poller = Poller();
        var delays = new List<(string When, TimeSpan Delay)> { ("at the start", poller.NextDelay()) };

        for (var i = 0; i < 31; i++)
        {
            await poller.RunOnceAsync(CancellationToken.None);
            _time.Advance(poller.NextDelay());
        }

        delays.Add(("after a quiet minute", poller.NextDelay()));
        _controls.Waiting.Enqueue(new DomainControl("c1", "audit", "acme", "shop", null, "pause"));
        await poller.RunOnceAsync(CancellationToken.None);
        delays.Add(("right after a click", poller.NextDelay()));
        _controls.TakeFailure = new RpcException(new Status(StatusCode.Unavailable, "down"));
        _time.Advance(JobControlPoller.QuietWindow + TimeSpan.FromSeconds(1));
        await poller.RunOnceAsync(CancellationToken.None);
        delays.Add(("down for over a minute", poller.NextDelay()));

        delays.ShouldBe(
        [
            ("at the start", JobControlPoller.Fast),
            ("after a quiet minute", JobControlPoller.Slow),
            ("right after a click", JobControlPoller.Fast),
            ("down for over a minute", JobControlPoller.Slow),
        ]);
        (JobControlPoller.Fast, JobControlPoller.Slow).ShouldBe((TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Over_the_real_worker_gate_a_desktop_click_reaches_the_poller_once_and_the_write_log_keeps_its_status_and_sentence()
    {
        var directory = Directory.CreateTempSubdirectory("scout-job-controls-").FullName;
        try
        {
            _audits.ControlAsync("acme", "shop", "pause", Arg.Any<CancellationToken>())
                .Returns(new AuditControlAnswer(true, "Paused the audit of acme/shop; an area in progress finishes first."));
            var options = Options.Create(new FeatureStateStoreOptions { FilePath = Path.Combine(directory, "features.json") });
            var log = new JsonLinesWriteLog(options, NullLogger<JsonLinesWriteLog>.Instance);
            using var service = new OwnerControlService(
                new JsonFileOwnerControlQueue(options, NullLogger<JsonFileOwnerControlQueue>.Instance), log, NullLogger<OwnerControlService>.Instance);
            var asked = await service.RequestAsync("audit", "acme", "shop", null, "pause", CancellationToken.None);
            await using var server = await GrpcWriteGateTests.WorkerGateServer.StartAsync(new ControlGate(service));
            using var gate = new GrpcWriteGate(server.SocketPath, "worker-session-token");

            var ran = await Poller(controls: gate).RunOnceAsync(CancellationToken.None);
            var again = await Poller(controls: gate).RunOnceAsync(CancellationToken.None);

            (ran, again).ShouldBe((true, false));
            var entry = (await log.ListAsync(5, CancellationToken.None)).ShouldHaveSingleItem();
            (entry.Action, entry.Target, entry.Outcome, entry.ApprovedBy, entry.ActionId, entry.ControlStatus)
                .ShouldBe(("Pause audit", "acme/shop", "Paused the audit of acme/shop; an area in progress finishes first.", "owner", asked.ControlId, "applied"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void The_poller_runs_only_when_the_worker_reaches_the_control_plane()
    {
        static List<IHostedService> Hosted(bool gate)
        {
            var values = new Dictionary<string, string?> { ["Scout:Plugins:GitHub:Token"] = "github-dummy-not-real" };
            if (gate)
            {
                values["Scout:WorkerGate:SocketPath"] = "/run/scout/cp.sock";
                values["Scout:WorkerGate:Token"] = "t0ken";
            }

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            using var provider = new ServiceCollection()
                .AddLogging()
                .AddSingleton(Substitute.For<IHostApplicationLifetime>())
                .AddScoutHost(configuration)
                .BuildServiceProvider();
            return [.. provider.GetServices<IHostedService>()];
        }

        Hosted(gate: true).OfType<JobControlPoller>().ShouldHaveSingleItem();
        Hosted(gate: false).OfType<JobControlPoller>().ShouldBeEmpty();
    }

    private sealed class ScriptedControls : IJobControls
    {
        public ConcurrentQueue<DomainControl> Waiting { get; } = new();

        public List<(string Id, string Status, string Outcome)> Settled { get; } = [];

        public Exception? TakeFailure { get; set; }

        public int SettleFailures { get; set; }

        public Task<DomainControl?> TakeNextAsync(CancellationToken ct) =>
            TakeFailure is { } failure ? throw failure : Task.FromResult(Waiting.TryDequeue(out var next) ? next : null);

        public Task SettleAsync(string id, string status, string outcome, CancellationToken ct)
        {
            if (SettleFailures-- > 0)
            {
                throw new RpcException(new Status(StatusCode.DeadlineExceeded, "slow"));
            }

            Settled.Add((id, status, outcome));
            return Task.CompletedTask;
        }
    }

    private sealed class PostedNotices : IOriginNotifier
    {
        public List<(string Origin, string Text)> Posts { get; } = [];

        public Task<bool> PostAsync(string originReference, string text, CancellationToken ct)
        {
            Posts.Add((originReference, text));
            return Task.FromResult(true);
        }
    }

    private sealed class ControlGate(OwnerControlService controls) : WorkerGate.WorkerGateBase
    {
        public override async Task<NextJobControlResponse> NextJobControl(NextJobControlRequest request, ServerCallContext context)
        {
            var response = new NextJobControlResponse();
            if (await controls.TakeNextAsync(context.CancellationToken) is { } taken)
            {
                response.Control = new Scout.ControlPlane.Contracts.JobControl { Id = taken.Id, Kind = taken.Kind, Owner = taken.Owner, Repo = taken.Repo, Action = taken.Action };
            }

            return response;
        }

        public override async Task<SettleJobControlResponse> SettleJobControl(SettleJobControlRequest request, ServerCallContext context) =>
            new() { Recorded = await controls.SettleAsync(request.Id, request.Status, request.Outcome, context.CancellationToken) };
    }
}
