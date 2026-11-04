using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Conversation.Abstractions;
using Scout.Llm.Abstractions;
using Scout.Operator.Tests.Fakes;
using Scout.Plugin.YouTube;
using Scout.Plugin.YouTube.Abstractions;

namespace Scout.Operator.Tests;

public sealed class OperatorSchedulerTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("scout-schedule-").FullName;
    private readonly List<string> _ran = [];

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private ScheduledJob[] Jobs() =>
    [
        ScheduledJob.Daily("analytics", 9, true, _ => Run("analytics")),
        ScheduledJob.Daily("optimisation", 22, true, _ => Run("optimisation")),
        ScheduledJob.Weekly("strategy review", DayOfWeek.Sunday, 8, true, _ => Run("strategy")),
        ScheduledJob.Every("experiments", TimeSpan.FromHours(4), true, _ => Run("experiments")),
        ScheduledJob.Daily("local tidy", 3, false, _ => Run("local")),
    ];

    private Task Run(string job)
    {
        _ran.Add(job);
        return Task.CompletedTask;
    }

    private string StateFile => Path.Combine(_folder, "data", "operator-schedule.json");

    private void Stored(params (string Job, string At)[] runs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        File.WriteAllText(StateFile, "{" + string.Join(",", runs.Select(r => $"\"{r.Job}\":\"{r.At}\"")) + "}");
    }

    private string Media() => Directory.CreateDirectory(Path.Combine(_folder, "data", "operator", "p1")).FullName;

    private void StoredLongAgo()
        => Stored(Jobs().Select(j => (j.Name, "2026-10-01T00:00:00Z")).ToArray());

    private (OperatorScheduler Scheduler, ScheduleNotes Notes) Build(
        FakeTimeProvider time, bool signedIn = true, IEnumerable<ScheduledJob>? jobs = null, ILogger<OperatorScheduler>? logger = null, RecordingControlPlane? controlPlane = null)
    {
        var token = Path.Combine(_folder, "youtube-token.json");
        if (signedIn)
        {
            File.WriteAllText(token, "{}");
        }

        if (controlPlane is null)
        {
            controlPlane = new RecordingControlPlane { MediaDirectory = Media() };
            controlPlane.Store(Given.Project());
        }

        var notes = new ScheduleNotes();
        return (new OperatorScheduler(jobs ?? Jobs(), notes, Options.Create(new YouTubeOptions { TokenFile = token }), controlPlane, time, logger ?? new RecordingLogger<OperatorScheduler>()), notes);
    }

    [Fact]
    public async Task The_First_Start_Ever_Runs_Nothing_Retroactively_And_Reports_Nothing_Missed()
    {
        var time = new FakeTimeProvider(Given.Now);
        var (scheduler, notes) = Build(time);

        await scheduler.RunDueAsync(CancellationToken.None);

        _ran.ShouldBeEmpty();
        notes.Missed.ShouldBeNull();
    }

    [Fact]
    public async Task At_Start_Only_The_Slots_Missed_Since_The_Stored_Last_Run_Run_Once_And_The_Clock_Ones_Are_Reported()
    {
        Stored(
            ("analytics 09:00", "2026-11-02T09:00:00Z"),
            ("optimisation 22:00", "2026-11-02T22:00:00Z"),
            ("strategy review Sunday 08:00", "2026-11-01T08:00:00Z"),
            ("experiments", "2026-11-03T04:00:00Z"),
            ("local tidy 03:00", "2026-11-03T03:00:00Z"));
        var (scheduler, notes) = Build(new FakeTimeProvider(Given.Now));

        await scheduler.RunDueAsync(CancellationToken.None);
        await scheduler.RunDueAsync(CancellationToken.None);

        _ran.ShouldBe(["analytics", "experiments"]);
        notes.Missed.ShouldBe("Scout was not running for these scheduled jobs and ran them when it started: analytics 09:00.");
    }

    [Fact]
    public async Task Until_The_State_File_Can_Be_Found_Nothing_Runs_And_Then_The_Stored_Slots_Are_Honoured()
    {
        Stored(("analytics 09:00", "2026-11-02T09:00:00Z"), ("optimisation 22:00", "2026-11-02T22:00:00Z"));
        var controlPlane = new RecordingControlPlane { MediaDirectory = Media() };
        var (scheduler, notes) = Build(new FakeTimeProvider(Given.Now), controlPlane: controlPlane);

        await scheduler.RunDueAsync(CancellationToken.None);
        _ran.ShouldBeEmpty();
        controlPlane.Store(Given.Project());
        await scheduler.RunDueAsync(CancellationToken.None);

        _ran.ShouldBe(["analytics"]);
        notes.Missed.ShouldBe("Scout was not running for these scheduled jobs and ran them when it started: analytics 09:00.");
    }

    [Fact]
    public async Task Each_Run_Is_Stored_So_A_Restart_Does_Not_Run_It_Again()
    {
        StoredLongAgo();
        var time = new FakeTimeProvider(Given.Now);
        var (first, _) = Build(time);
        await first.RunDueAsync(CancellationToken.None);
        _ran.Clear();

        var (restarted, notes) = Build(time);
        await restarted.RunDueAsync(CancellationToken.None);

        _ran.ShouldBeEmpty();
        notes.Missed.ShouldBeNull();
        File.ReadAllText(StateFile).ShouldContain("\"analytics 09:00\":\"2026-11-03T09:00:00");
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"analytics 09:00\":12}")]
    public async Task An_Unreadable_State_File_Is_Treated_As_Empty_And_Logged(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        File.WriteAllText(StateFile, content);
        var logger = new RecordingLogger<OperatorScheduler>();
        var (scheduler, notes) = Build(new FakeTimeProvider(Given.Now), logger: logger);

        await scheduler.RunDueAsync(CancellationToken.None);

        _ran.ShouldBeEmpty();
        notes.Missed.ShouldBeNull();
        logger.Entries.ShouldContain((LogLevel.Warning, "The operator's schedule could not be read, so no missed slot runs at start."));
    }

    [Fact]
    public async Task Each_Job_Runs_Again_At_Its_Next_Slot_Only()
    {
        var time = new FakeTimeProvider(Given.Now);
        var (scheduler, _) = Build(time);
        await scheduler.RunDueAsync(CancellationToken.None);

        time.Advance(TimeSpan.FromHours(4));
        await scheduler.RunDueAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromHours(6));
        await scheduler.RunDueAsync(CancellationToken.None);

        _ran.ShouldBe(["experiments", "optimisation", "experiments"]);
    }

    [Fact]
    public async Task Without_The_YouTube_Sign_In_Only_A_Job_That_Needs_No_YouTube_Runs()
    {
        StoredLongAgo();
        var (scheduler, _) = Build(new FakeTimeProvider(Given.Now), signedIn: false);

        await scheduler.RunDueAsync(CancellationToken.None);

        _ran.ShouldBe(["local"]);
    }

    [Fact]
    public async Task A_Slot_Skipped_For_A_Missing_Sign_In_Runs_Once_When_The_Token_Appears_Within_The_Same_Slot()
    {
        Stored(("analytics 09:00", "2026-10-01T00:00:00Z"));
        var time = new FakeTimeProvider(Given.Now);
        var (scheduler, _) = Build(time, signedIn: false, jobs: [ScheduledJob.Daily("analytics", 9, true, _ => Run("analytics"))]);

        await scheduler.RunDueAsync(CancellationToken.None);
        _ran.ShouldBeEmpty();

        File.WriteAllText(Path.Combine(_folder, "youtube-token.json"), "{}");
        time.Advance(TimeSpan.FromMinutes(2));
        await scheduler.RunDueAsync(CancellationToken.None);
        await scheduler.RunDueAsync(CancellationToken.None);

        _ran.ShouldBe(["analytics"]);
    }

    [Fact]
    public async Task A_Job_That_Throws_Is_Not_Run_Again_In_The_Same_Slot()
    {
        Stored(("analytics 09:00", "2026-10-01T00:00:00Z"));
        var time = new FakeTimeProvider(Given.Now);
        var (scheduler, _) = Build(time, jobs: [ScheduledJob.Daily("analytics", 9, true, _ =>
        {
            _ran.Add("analytics");
            throw new InvalidOperationException("Scout could not reach its control plane.");
        })]);

        await scheduler.RunDueAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(2));
        await scheduler.RunDueAsync(CancellationToken.None);

        _ran.ShouldBe(["analytics"]);
    }

    [Fact]
    public async Task A_Job_That_Fails_Is_Logged_And_The_Others_Still_Run()
    {
        StoredLongAgo();
        var logger = new RecordingLogger<OperatorScheduler>();
        var (scheduler, _) = Build(
            new FakeTimeProvider(Given.Now),
            jobs: [ScheduledJob.Daily("analytics", 9, true, _ => throw new InvalidOperationException("Scout could not reach its control plane.")), .. Jobs().Skip(1)],
            logger: logger);

        await scheduler.RunDueAsync(CancellationToken.None);

        _ran.ShouldBe(["optimisation", "strategy", "experiments", "local"]);
        logger.Entries.ShouldHaveSingleItem().ShouldBe((LogLevel.Warning, "The operator's analytics 09:00 job failed: Scout could not reach its control plane."));
    }

    [Fact]
    public void The_Scheduled_Videos_Are_Checked_Every_15_Minutes_Off_The_Executor_Tick()
    {
        var media = Given.Media();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IOperatorControlPlane>(new RecordingControlPlane());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Substitute.For<IYouTubeWriteClient>());
        services.AddSingleton(Substitute.For<IYouTubeAnalyticsClient>());
        services.AddSingleton(Substitute.For<IYouTubeReportingClient>());
        services.AddSingleton(Substitute.For<IWriteGate>());
        services.AddSingleton(Substitute.For<IChatCompletionService>());
        services.AddSingleton(Substitute.For<IOriginNotifier>());
        services.AddSingleton(new VisualGenerator(media, Given.Capabilities(media), TimeProvider.System));
        services.AddYouTubePublishing();
        using var provider = services.BuildServiceProvider();

        var job = provider.GetServices<ScheduledJob>().Single(j => j.Name == "published check");

        (job.OnTheClock, job.NeedsYouTube).ShouldBe((false, true));
        job.LatestSlot(Given.Now.AddMinutes(14)).ShouldBe(Given.Now);
        job.LatestSlot(Given.Now.AddMinutes(15)).ShouldBe(Given.Now.AddMinutes(15));
    }

    [Fact]
    public async Task Comments_Job_Runs_Every_Four_Hours()
    {
        var media = Given.Media();
        var controlPlane = new RecordingControlPlane { MediaDirectory = Media() };
        controlPlane.Store(Given.Project());
        var youtube = Substitute.For<IYouTubeClient>();
        youtube.GetMyChannelAsync(Arg.Any<CancellationToken>()).Returns(Given.Channel());
        youtube.ListCommentThreadsAsync("UCowner", null, Arg.Any<CancellationToken>()).Returns(new YouTubeCommentThreadPage([], null));
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IOperatorControlPlane>(controlPlane);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(youtube);
        services.AddSingleton(Substitute.For<IYouTubeWriteClient>());
        services.AddSingleton(Substitute.For<IYouTubeAnalyticsClient>());
        services.AddSingleton(Substitute.For<IYouTubeReportingClient>());
        services.AddSingleton(Substitute.For<IWriteGate>());
        services.AddSingleton(Substitute.For<IChatCompletionService>());
        services.AddSingleton(Substitute.For<IOriginNotifier>());
        services.AddYouTubeOperator();
        services.AddSingleton(new VisualGenerator(media, Given.Capabilities(media), TimeProvider.System));
        services.AddYouTubePublishing();
        await using var provider = services.BuildServiceProvider();

        var job = provider.GetServices<ScheduledJob>().Single(j => j.Name == "comments");
        await job.Run(CancellationToken.None);

        (job.OnTheClock, job.NeedsYouTube).ShouldBe((false, true));
        job.LatestSlot(Given.Now.AddHours(3).AddMinutes(59)).ShouldBe(Given.Now);
        job.LatestSlot(Given.Now.AddHours(4)).ShouldBe(Given.Now.AddHours(4));
        controlPlane.CommentSaves.ShouldHaveSingleItem().SyncedAt.ShouldNotBeNull();
        await youtube.Received(1).ListCommentThreadsAsync("UCowner", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_Daily_Slot_Follows_The_London_Clock_In_Summer_Time()
    {
        var slot = ScheduledJob.At(new DateTimeOffset(2026, 7, 10, 8, 30, 0, TimeSpan.Zero), 9, _ => true);

        slot.ShouldBe(new DateTimeOffset(2026, 7, 10, 8, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_Weekly_Slot_Is_The_Last_Sunday_Morning()
    {
        var slot = ScheduledJob.At(Given.Now, 8, d => d.DayOfWeek == DayOfWeek.Sunday);

        slot.ShouldBe(new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void London_Falls_Back_To_Utc_On_A_Host_Without_Time_Zone_Data()
        => London.Find(_ => throw new TimeZoneNotFoundException()).ShouldBe(TimeZoneInfo.Utc);
}
