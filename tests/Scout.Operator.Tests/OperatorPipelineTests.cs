using System.Net;
using Scout.Conversation.Abstractions;
using Scout.Operator.Tests.Fakes;
using Scout.Plugin.YouTube;

namespace Scout.Operator.Tests;

public sealed class OperatorPipelineTests
{
    private const string Origin = "discord:3333:42";

    private const string Quota = "YouTube's daily quota for this key is spent; it resets at midnight Pacific time.";

    private static ScriptedChat Model()
        => ScriptedChat.Answering(
            Given.Candidates("Sourdough basics"),
            Given.Script("""[{"text":"Starters need daily feeding","risk":"standard","sourceUrls":[]}]"""));

    private static RecordingControlPlane Holding(OperatorProjectState project)
    {
        var controlPlane = new RecordingControlPlane();
        controlPlane.Store(project);
        return controlPlane;
    }

    private static List<string> Posts(IOriginNotifier notifier)
        => notifier.ReceivedCalls().Select(c => $"{c.GetArguments()[0]} | {c.GetArguments()[1]}").ToList();

    [Fact]
    public async Task A_Chat_Project_Is_Saved_Scripting_Then_Script_Ready_And_The_Chat_Hears_Only_That_It_Started_And_Is_Ready()
    {
        var controlPlane = Holding(Given.Project());
        var notifier = Substitute.For<IOriginNotifier>();
        notifier.PostAsync(default!, default!, default).ReturnsForAnyArgs(true);

        await Given.Pipeline(controlPlane, Given.YouTube(), Model(), notifier)
            .RunAsync(new QueuedProject(Given.Project(), Origin), CancellationToken.None);

        controlPlane.Saves.Select(s => s.Project.Status).ShouldBe(["scripting", "script_ready"]);
        controlPlane.Saves[0].Project.Candidates.Select(c => c.Title).ShouldBe(["Sourdough basics"]);
        var ready = controlPlane.Saves[1].Project;
        ready.Script.ShouldNotBeNull().Hook.ShouldBe("Bread in ten minutes?");
        ready.Claims.ShouldHaveSingleItem().Status.ShouldBe("pending");
        Posts(notifier).ShouldBe([
            "discord:3333:42 | Researching sourdough...",
            "discord:3333:42 | Script ready for sourdough: open Scout Desktop, Production, to review it.",
        ]);
    }

    [Fact]
    public async Task A_Queued_Project_Is_Scripted_With_Its_Format_And_Direction()
    {
        var project = Given.Project(status: "researching") with { Origin = "desktop", Format = "own_pictures", Direction = "Walk along the river, it's: calm." };
        var controlPlane = Holding(project);
        var chat = Model();

        await Given.Pipeline(controlPlane, Given.YouTube(), chat, Substitute.For<IOriginNotifier>())
            .RunAsync(new QueuedProject(project, null), CancellationToken.None);

        controlPlane.Saves.Select(s => s.Project.Status).ShouldBe(["scripting", "script_ready"]);
        var script = chat.Requests.Last().Single(m => m.Role == Microsoft.SemanticKernel.ChatCompletion.AuthorRole.System).Text;
        script.ShouldContain("The video is narration over pictures the owner takes or already has, one per scene; write narration around things the owner can photograph.", Case.Sensitive);
        script.ShouldContain("Direction the owner gave for this video, which the script follows for its angle, tone and structure: Walk along the river, it's: calm.", Case.Sensitive);
        controlPlane.Saves.ShouldAllBe(s => s.Project.Format == "own_pictures" && s.Project.Direction == "Walk along the river, it's: calm.");
    }

    [Fact]
    public async Task A_Project_Without_An_Origin_Posts_Nothing()
    {
        var notifier = Substitute.For<IOriginNotifier>();

        await Given.Pipeline(Holding(Given.Project()), Given.YouTube(), Model(), notifier)
            .RunAsync(new QueuedProject(Given.Project(), null), CancellationToken.None);

        notifier.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Failing_Stage_Saves_The_Project_Failed_With_The_Reason_And_Posts_The_Failure_To_The_Chat()
    {
        var controlPlane = Holding(Given.Project());
        var youtube = Given.YouTube();
        youtube.SearchVideosAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<Scout.Plugin.YouTube.Abstractions.YouTubeSearchResult>>>(_ =>
                throw new YouTubeApiException(Quota, HttpStatusCode.Forbidden));
        var notifier = Substitute.For<IOriginNotifier>();

        await Given.Pipeline(controlPlane, youtube, Model(), notifier)
            .RunAsync(new QueuedProject(Given.Project(), Origin), CancellationToken.None);

        var failed = controlPlane.Saves.ShouldHaveSingleItem().Project;
        failed.Status.ShouldBe("failed");
        failed.Error.ShouldBe(Quota);
        Posts(notifier).ShouldBe([
            "discord:3333:42 | Researching sourdough...",
            $"discord:3333:42 | The video project on sourdough stopped: {Quota}",
        ]);
    }

    [Fact]
    public async Task A_Topic_The_Mandate_Blocks_Fails_The_Project_Before_Any_Search_Or_Model_Call()
    {
        var controlPlane = new RecordingControlPlane { Mandate = Given.Mandate(blocked: ["sourdough"]) };
        var project = Given.Project(topic: "Quick SOURDOUGH tips", status: "researching");
        controlPlane.Store(project);
        var youtube = Given.YouTube();
        var chat = Model();

        await Given.Pipeline(controlPlane, youtube, chat, Substitute.For<IOriginNotifier>())
            .RunAsync(new QueuedProject(project, null), CancellationToken.None);

        var failed = controlPlane.Saves.ShouldHaveSingleItem().Project;
        (failed.Status, failed.Error).ShouldBe(("failed",
            "The channel mandate blocks the topic \"sourdough\"; choose another topic or change the mandate in Scout Desktop, Production."));
        youtube.ReceivedCalls().ShouldBeEmpty();
        chat.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task With_The_Control_Plane_Down_Mid_Run_The_Chat_Still_Hears_Why_The_Project_Stopped()
    {
        var controlPlane = new RecordingControlPlane { FailOn = _ => true };
        controlPlane.Store(Given.Project());
        var notifier = Substitute.For<IOriginNotifier>();

        await Should.ThrowAsync<InvalidOperationException>(() => Given.Pipeline(controlPlane, Given.YouTube(), Model(), notifier)
            .RunAsync(new QueuedProject(Given.Project(), Origin), CancellationToken.None));

        Posts(notifier).ShouldBe([
            "discord:3333:42 | Researching sourdough...",
            "discord:3333:42 | The video project on sourdough stopped: Scout could not reach its control plane.",
        ]);
    }

    [Fact]
    public async Task A_Progress_Notice_That_Cannot_Be_Posted_Does_Not_Stop_The_Project()
    {
        var controlPlane = Holding(Given.Project());
        var notifier = Substitute.For<IOriginNotifier>();
        notifier.PostAsync(default!, default!, default).ReturnsForAnyArgs<Task<bool>>(_ => throw new HttpRequestException("connection reset"));

        await Given.Pipeline(controlPlane, Given.YouTube(), Model(), notifier)
            .RunAsync(new QueuedProject(Given.Project(), Origin), CancellationToken.None);

        controlPlane.Saves.Select(s => s.Project.Status).ShouldBe(["scripting", "script_ready"]);
    }

    [Fact]
    public async Task Each_Save_Starts_From_The_Control_Planes_Copy_So_Claims_The_Owner_Decided_Go_Back_Exactly_As_Read()
    {
        OperatorClaim[] decided =
        [
            new("c1", "Bread needs flour", "standard", ["https://www.youtube.com/watch?v=found000001"], "supported"),
            new("c3", "Rye has no gluten", "high", [], "waived", "Owner checked the label"),
        ];
        var controlPlane = Holding(Given.Project() with
        {
            Claims = [.. decided, new OperatorClaim("c2", "An old pending claim", "standard", [], "pending")],
        });
        var chat = ScriptedChat.Answering(
            Given.Candidates("Sourdough basics"),
            Given.Script("""[{"text":"Bread needs flour","risk":"standard"},{"text":"Starters need daily feeding","risk":"standard"},{"text":"Ovens vary","risk":"standard"}]"""));

        await Given.Pipeline(controlPlane, Given.YouTube(), chat, Substitute.For<IOriginNotifier>())
            .RunAsync(new QueuedProject(Given.Project(), null), CancellationToken.None);

        controlPlane.Saves[0].Project.Claims.Take(2).ShouldBe(decided);
        controlPlane.Saves[1].Project.Claims.Select(c => (c.Id, c.Text, c.Status)).ShouldBe([
            ("c1", "Bread needs flour", "supported"),
            ("c3", "Rye has no gluten", "waived"),
            ("c2", "Starters need daily feeding", "pending"),
            ("c4", "Ovens vary", "pending"),
        ]);
        controlPlane.Saves[1].Project.Claims.Take(2).ShouldBe(decided);
    }

    [Fact]
    public async Task A_Transcribing_Project_Ends_Script_Ready_Without_Research_Or_A_Script_Request()
    {
        var media = Directory.CreateTempSubdirectory("scout-pipeline-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(media, "owner-recording-1a.mp4"), [1, 2, 3]);
            var project = Given.Project(status: "transcribing") with { Origin = "desktop", Format = "own_video", RecordingFile = "owner-recording-1a.mp4" };
            var controlPlane = new RecordingControlPlane { MediaDirectory = media };
            controlPlane.Store(project);
            var chat = Model();
            var youtube = Given.YouTube();
            var transcriber = new ScriptedTranscriber { Heard = _ => ScriptedTranscriber.Segments((0, 4000, "Today we bake bread.")) };

            await Given.Pipeline(
                    controlPlane, youtube, chat, Substitute.For<IOriginNotifier>(), time: new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Given.Now),
                    runner: new FakeProcessRunner { ProbedMs = 60_000, ReportsPicture = true }, transcriber: transcriber)
                .RunAsync(new QueuedProject(project, null), CancellationToken.None);

            controlPlane.Saves.Select(s => s.Project.Status).ShouldBe(["transcribing", "script_ready"]);
            controlPlane.Saves[^1].Project.Script.ShouldNotBeNull().Sections.ShouldHaveSingleItem().Narration.ShouldBe("Today we bake bread.");
            chat.Requests.ShouldBeEmpty();
            youtube.ReceivedCalls().ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(media, recursive: true);
        }
    }

    [Fact]
    public async Task A_Failed_Transcription_Leaves_No_Partial_Transcript()
    {
        var media = Directory.CreateTempSubdirectory("scout-pipeline-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(media, "owner-recording-1a.mp4"), [1, 2, 3]);
            var project = Given.Project(status: "transcribing") with { Origin = "desktop", Format = "own_video", RecordingFile = "owner-recording-1a.mp4" };
            var controlPlane = new RecordingControlPlane { MediaDirectory = media };
            controlPlane.Store(project);
            var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Given.Now);
            async IAsyncEnumerable<TranscribedSegment> HeardThenFailed()
            {
                yield return new TranscribedSegment(TimeSpan.Zero, TimeSpan.FromSeconds(4), "Today we bake bread.");
                clock.Advance(TimeSpan.FromMinutes(2));
                yield return new TranscribedSegment(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(9), "First the starter.");
                await Task.FromException(new InvalidDataException("The model stopped."));
            }

            var pipeline = Given.Pipeline(
                controlPlane, Given.YouTube(), Model(), Substitute.For<IOriginNotifier>(), time: clock,
                runner: new FakeProcessRunner { ProbedMs = 600_000, ReportsPicture = true }, transcriber: new ScriptedTranscriber { Heard = _ => HeardThenFailed() });

            await pipeline.RunAsync(new QueuedProject(project, null), CancellationToken.None);
            var failed = await controlPlane.GetProjectAsync("p1", CancellationToken.None);
            controlPlane.Store(project with { Script = new OperatorScript(string.Empty, [new OperatorScriptSection(string.Empty, "Today we bake bread.")], string.Empty, [], string.Empty, [], []) });
            await pipeline.SaveFailedAsync("p1", "Scout could not reach its control plane.", CancellationToken.None);
            var lastResort = await controlPlane.GetProjectAsync("p1", CancellationToken.None);

            controlPlane.Saves.Select(s => (s.Project.Status, s.Project.Script?.Sections.Count)).ShouldBe([
                ("transcribing", 0), ("transcribing", 2), ("failed", (int?)null), ("failed", (int?)null),
            ]);
            (failed.Status, failed.Error, failed.Script).ShouldBe(("failed", "Scout could not transcribe the recording (InvalidDataException: The model stopped.)", (OperatorScript?)null));
            (lastResort.Status, lastResort.Script).ShouldBe(("failed", (OperatorScript?)null), "the worker's last-resort save of a transcribing project keeps no partial transcript either");
        }
        finally
        {
            Directory.Delete(media, recursive: true);
        }
    }
}
