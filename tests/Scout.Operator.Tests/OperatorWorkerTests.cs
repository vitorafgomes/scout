using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Scout.Conversation.Abstractions;
using Scout.Operator.Tests.Fakes;
using Scout.Plugin.YouTube;
using Scout.Plugin.YouTube.Abstractions;

namespace Scout.Operator.Tests;

public sealed class OperatorWorkerTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    private static ScriptedChat Model()
        => ScriptedChat.Answering(Given.Candidates("Sourdough basics"), Given.Script());

    private static OperatorWorker Worker(
        RecordingControlPlane controlPlane,
        Channel<QueuedProject> started,
        FakeTimeProvider clock,
        ILogger<OperatorWorker> logger,
        ScriptedChat? model = null,
        IYouTubeClient? youtube = null)
        => new(
            controlPlane,
            Given.Pipeline(controlPlane, youtube ?? Given.YouTube(), model ?? Model(), Substitute.For<IOriginNotifier>()),
            started,
            clock,
            logger);

    private static async Task Until(Func<bool> condition, FakeTimeProvider? clock = null)
    {
        var deadline = DateTime.UtcNow + Budget;
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "the worker did not get there in time");
            clock?.Advance(OperatorWorker.PollInterval);
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task A_Project_The_Desktop_Queued_Is_Run_On_The_Next_Poll()
    {
        var controlPlane = new RecordingControlPlane();
        var clock = new FakeTimeProvider(Given.Now);
        using var worker = Worker(controlPlane, Channel.CreateUnbounded<QueuedProject>(), clock, new RecordingLogger<OperatorWorker>());
        await worker.StartAsync(CancellationToken.None);

        controlPlane.Queue(new QueuedProject(Given.Project(id: "desk-1"), null));
        await Until(() => controlPlane.Saves.Any(s => s.Project.Status == "script_ready"), clock);
        await worker.StopAsync(CancellationToken.None);

        controlPlane.Saves.Select(s => (s.Project.Id, s.Project.Status)).ShouldBe([("desk-1", "scripting"), ("desk-1", "script_ready")]);
    }

    [Fact]
    public async Task A_Project_A_Chat_Started_Runs_At_Once_Without_Waiting_For_The_Poll()
    {
        var controlPlane = new RecordingControlPlane();
        var started = Channel.CreateUnbounded<QueuedProject>();
        using var worker = Worker(controlPlane, started, new FakeTimeProvider(Given.Now), new RecordingLogger<OperatorWorker>());
        await worker.StartAsync(CancellationToken.None);
        await Until(() => controlPlane.NextQueuedCalls > 0);

        controlPlane.Store(Given.Project(id: "chat-1"));
        await started.Writer.WriteAsync(new QueuedProject(Given.Project(id: "chat-1"), "discord:3333:42"));
        await Until(() => controlPlane.Saves.Any(s => s.Project.Status == "script_ready"));
        await worker.StopAsync(CancellationToken.None);

        controlPlane.Saves.Select(s => (s.Project.Id, s.Project.Status)).ShouldBe([("chat-1", "scripting"), ("chat-1", "script_ready")]);
    }

    [Fact]
    public async Task A_Control_Plane_That_Cannot_Answer_Is_Logged_Once_While_The_Worker_Keeps_Polling_And_Then_Stops_Cleanly()
    {
        var controlPlane = new RecordingControlPlane
        {
            NextQueuedFailure = new InvalidOperationException("Scout Desktop's control plane is not available, so the operator is off."),
        };
        var clock = new FakeTimeProvider(Given.Now);
        var logger = new RecordingLogger<OperatorWorker>();
        using var worker = Worker(controlPlane, Channel.CreateUnbounded<QueuedProject>(), clock, logger);
        await worker.StartAsync(CancellationToken.None);

        await Until(() => controlPlane.NextQueuedCalls >= 4, clock);
        await worker.StopAsync(CancellationToken.None);

        logger.Entries.ShouldHaveSingleItem().ShouldBe((
            LogLevel.Warning,
            "The operator cannot read queued video projects: Scout Desktop's control plane is not available, so the operator is off."));
        worker.ExecuteTask.ShouldNotBeNull().IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public async Task A_Project_Whose_Saves_All_Fail_Is_Logged_And_Saved_As_Failed_Once_More_By_The_Worker()
    {
        var controlPlane = new RecordingControlPlane { FailOn = _ => true };
        var started = Channel.CreateUnbounded<QueuedProject>();
        var logger = new RecordingLogger<OperatorWorker>();
        using var worker = Worker(controlPlane, started, new FakeTimeProvider(Given.Now), logger);
        await worker.StartAsync(CancellationToken.None);

        controlPlane.Store(Given.Project(id: "chat-2"));
        await started.Writer.WriteAsync(new QueuedProject(Given.Project(id: "chat-2"), null));
        await Until(() => logger.Entries.Count >= 2);
        await worker.StopAsync(CancellationToken.None);

        controlPlane.Saves.Select(s => s.Project.Status).ShouldBe(["scripting", "failed", "failed"]);
        controlPlane.Saves[^1].Project.Error.ShouldBe("Scout could not reach its control plane.");
        logger.Entries.Select(e => e.Message).ShouldBe([
            "Video project chat-2 could not be run (InvalidOperationException).",
            "Video project chat-2 could not be saved as failed (InvalidOperationException).",
        ]);
    }

    [Fact]
    public async Task When_Saving_The_Failure_Fails_The_Worker_Saves_The_Original_Reason_Once_The_Control_Plane_Answers()
    {
        const string quota = "YouTube's daily quota for this key is spent; it resets at midnight Pacific time.";
        var failedSaves = 0;
        var controlPlane = new RecordingControlPlane
        {
            FailOn = status => status == "failed" && Interlocked.Increment(ref failedSaves) == 1,
        };
        var youtube = Given.YouTube();
        youtube.SearchVideosAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<YouTubeSearchResult>>>(_ => throw new YouTubeApiException(quota, System.Net.HttpStatusCode.Forbidden));
        var started = Channel.CreateUnbounded<QueuedProject>();
        using var worker = Worker(controlPlane, started, new FakeTimeProvider(Given.Now), new RecordingLogger<OperatorWorker>(), youtube: youtube);
        await worker.StartAsync(CancellationToken.None);

        controlPlane.Store(Given.Project(id: "chat-3"));
        await started.Writer.WriteAsync(new QueuedProject(Given.Project(id: "chat-3"), null));
        await Until(() => controlPlane.Saves.Count >= 2);
        await worker.StopAsync(CancellationToken.None);

        controlPlane.Saves.Select(s => (s.Project.Status, s.Project.Error)).ShouldBe([("failed", quota), ("failed", quota)]);
        (await controlPlane.GetProjectAsync("chat-3", CancellationToken.None)).Error.ShouldBe(quota);
    }
}
