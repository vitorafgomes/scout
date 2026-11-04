using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Scout.Conversation.Abstractions;
using Scout.Operator.Tests.Fakes;
using Scout.Plugin.YouTube;
using Scout.Plugin.YouTube.Abstractions;

namespace Scout.Operator.Tests;

public sealed class ActionExecutorTests : IDisposable
{
    private const string Uploaded = "newVideo001";

    private readonly string _media = Directory.CreateTempSubdirectory("scout-publish-").FullName;
    private readonly FakeTimeProvider _time = new(Given.Now);
    private readonly IYouTubeWriteClient _youtube = Substitute.For<IYouTubeWriteClient>();
    private readonly RecordingLogger<ActionExecutor> _logs = new();
    private readonly IOriginNotifier _notifier = Substitute.For<IOriginNotifier>();

    public ActionExecutorTests()
    {
        File.WriteAllBytes(Path.Combine(_media, "video.mp4"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(_media, "thumbnail.png"), [4, 5]);
        File.WriteAllBytes(Path.Combine(_media, "thumbnail-proposal-24h-1.png"), [6, 7]);
        _youtube.UploadVideoAsync(Arg.Any<string>(), Arg.Any<YouTubeUpload>(), Arg.Any<UploadSession>(), Arg.Any<CancellationToken>()).Returns(Uploaded);
    }

    public void Dispose() => Directory.Delete(_media, recursive: true);

    private static OperatorPublication Publish(string privacy = "unlisted", string? publishAt = null)
        => new("publish_approved", null, privacy, publishAt, MadeForKids: false, SyntheticMedia: true, null, null, [], [], null);

    private static OperatorProjectState Approved(OperatorPublication? publication)
        => Given.Project(status: "approved") with
        {
            Package = new OperatorPackage(
                "approved", "video.mp4", [], "thumbnail.png", [], true, "2026-11-03T13:00:00Z",
                "Bread in ten minutes", "How to bake.", ["bread", "sourdough"], 95_000, null, false),
            Publication = publication,
        };

    private static OperatorExperiment TitleTest(string status = "running", params OperatorExperimentArm[] arms)
        => new("e1", "title", status, arms.Length > 0 ? arms : [
            new OperatorExperimentArm("control", "Original", "Bread in ten minutes", "2026-11-02T12:00:00Z", null, 0, 0, 0),
            new OperatorExperimentArm("b", "Question", "Can you bake bread in ten minutes?", null, null, 0, 0, 0),
        ], null, null);

    private (ActionExecutor Executor, RecordingControlPlane ControlPlane, RecordedActions Gate) Build(
        OperatorProjectState project, string action, string target, string? language = "English", Func<OperatorProjectState, bool>? failOn = null, string? origin = null)
    {
        var controlPlane = new RecordingControlPlane
        {
            MediaDirectory = _media,
            Mandate = Given.Mandate(language: language ?? string.Empty),
            FailOnProject = failOn ?? (_ => false),
        };
        controlPlane.Store(project);
        controlPlane.Approved.Enqueue(new OperatorAction("a1", "youtube", action, target, $"{action} {target}", origin));
        var executor = new ActionExecutor(
            controlPlane,
            _youtube,
            Microsoft.Extensions.Options.Options.Create(new OperatorMediaOptions { CategoryId = "26" }),
            _time,
            _logs,
            _notifier,
            Desk(controlPlane));
        return (executor, controlPlane, new RecordedActions(controlPlane));
    }

    private CommentDesk Desk(IOperatorControlPlane controlPlane)
        => new(controlPlane, Substitute.For<IYouTubeClient>(), ScriptedChat.Answering("{}"), _time, new RecordingLogger<CommentDesk>());

    private sealed class RecordedActions(RecordingControlPlane controlPlane)
    {
        public IReadOnlyList<(string Feature, string Scope, string Target, string Outcome, string ActionId)> Records
            => [.. controlPlane.Recorded.Select(r => (r.Action.FeatureId, r.Action.Action, r.Action.Target, r.Outcome, r.Action.Id))];
    }

    [Fact]
    public async Task An_Approved_Upload_Sends_The_Package_With_The_Owners_Choices_And_Saves_The_Video_As_Uploaded()
    {
        var (executor, controlPlane, gate) = Build(Approved(Publish("unlisted")), "videos.insert", "p1");

        await executor.RunOnceAsync(CancellationToken.None);

        await _youtube.Received(1).UploadVideoAsync(
            Path.Combine(_media, "video.mp4"),
            Arg.Is<YouTubeUpload>(u => u.Title == "Bread in ten minutes" && u.Description == "How to bake."
                && u.Tags.SequenceEqual(new[] { "bread", "sourdough" }) && u.CategoryId == "26" && u.DefaultLanguage == "en"
                && u.PrivacyStatus == "unlisted" && u.PublishAt == null && !u.MadeForKids && u.SyntheticMedia), Arg.Any<UploadSession>(),
            Arg.Any<CancellationToken>());
        controlPlane.Saves.Select(s => s.Project.Publication!.Status).ShouldBe(["uploading", "uploaded"]);
        var saved = controlPlane.Saves[^1].Project.Publication!;
        (saved.VideoId, saved.UploadedAt, saved.Error).ShouldBe((Uploaded, "2026-11-03T12:00:00Z", null));
        (saved with { Status = "publish_approved", VideoId = null, UploadedAt = null }).ShouldBe(Publish("unlisted") with { Snapshots = saved.Snapshots, Proposals = saved.Proposals });
        gate.Records.ShouldBe([("youtube", "videos.insert", "p1", "succeeded", "a1")]);
    }

    [Fact]
    public async Task A_Video_With_A_Publish_Time_Goes_Up_Private_With_That_Time_And_Is_Saved_Scheduled()
    {
        var (executor, controlPlane, _) = Build(Approved(Publish("public", "2026-11-10T17:00:00Z")), "videos.insert", "p1");

        await executor.RunOnceAsync(CancellationToken.None);

        await _youtube.Received(1).UploadVideoAsync(
            Arg.Any<string>(),
            Arg.Is<YouTubeUpload>(u => u.PrivacyStatus == "private" && u.PublishAt == new DateTimeOffset(2026, 11, 10, 17, 0, 0, TimeSpan.Zero)), Arg.Any<UploadSession>(),
            Arg.Any<CancellationToken>());
        var saved = controlPlane.Saves[^1].Project.Publication!;
        (saved.Status, saved.PrivacyStatus, saved.PublishAt).ShouldBe(("scheduled", "public", "2026-11-10T17:00:00Z"));
    }

    [Fact]
    public async Task A_Public_Upload_Is_Saved_Published()
    {
        var (executor, controlPlane, _) = Build(Approved(Publish("public")), "videos.insert", "p1");

        await executor.RunOnceAsync(CancellationToken.None);

        controlPlane.Saves[^1].Project.Publication!.Status.ShouldBe("published");
    }

    [Theory]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("Portuguese", "pt")]
    [InlineData("Klingonese", null)]
    [InlineData("", null)]
    public async Task The_Mandate_Language_Is_Sent_Only_As_A_Language_Code(string language, string? expected)
    {
        var (executor, _, _) = Build(Approved(Publish()), "videos.insert", "p1", language);

        await executor.RunOnceAsync(CancellationToken.None);

        await _youtube.Received(1).UploadVideoAsync(
            Arg.Any<string>(), Arg.Is<YouTubeUpload>(u => u.DefaultLanguage == expected), Arg.Any<UploadSession>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task When_The_Control_Plane_Is_Down_After_The_Upload_The_Next_Run_Saves_The_Video_Without_Uploading_It_Again()
    {
        var down = true;
        var (executor, controlPlane, gate) = Build(
            Approved(Publish()), "videos.insert", "p1", failOn: saved => down && saved.Publication?.Status == "uploaded");

        await executor.RunOnceAsync(CancellationToken.None);
        gate.Records.ShouldBeEmpty();
        down = false;
        await executor.RunOnceAsync(CancellationToken.None);

        await _youtube.Received(1).UploadVideoAsync(Arg.Any<string>(), Arg.Any<YouTubeUpload>(), Arg.Any<UploadSession>(), Arg.Any<CancellationToken>());
        (controlPlane.Saves[^1].Project.Publication!.Status, controlPlane.Saves[^1].Project.Publication!.VideoId).ShouldBe(("uploaded", Uploaded));
        gate.Records.ShouldBe([("youtube", "videos.insert", "p1", "succeeded", "a1")]);
        controlPlane.Approved.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Project_YouTube_Already_Has_Is_Only_Recorded_Never_Uploaded_Again()
    {
        var (executor, _, gate) = Build(Approved(Publish() with { Status = "uploaded", VideoId = Uploaded }), "videos.insert", "p1");

        await executor.RunOnceAsync(CancellationToken.None);

        await _youtube.DidNotReceiveWithAnyArgs().UploadVideoAsync(default!, default!, default!, default);
        gate.Records.ShouldBe([("youtube", "videos.insert", "p1", "succeeded", "a1")]);
    }

    [Fact]
    public async Task A_Failure_YouTube_May_Recover_From_Saves_The_Error_And_Leaves_The_Action_Approved_For_The_Next_Run()
    {
        _youtube.UploadVideoAsync(Arg.Any<string>(), Arg.Any<YouTubeUpload>(), Arg.Any<UploadSession>(), Arg.Any<CancellationToken>())
            .Returns<string>(_ => throw new YouTubeApiException("YouTube is currently unavailable. Try again later.", HttpStatusCode.ServiceUnavailable) { Retryable = true });
        var (executor, controlPlane, gate) = Build(Approved(Publish()), "videos.insert", "p1");

        await executor.RunOnceAsync(CancellationToken.None);

        var saved = controlPlane.Saves[^1].Project.Publication!;
        (saved.Status, saved.Error).ShouldBe(("uploading", "YouTube is currently unavailable. Try again later."));
        gate.Records.ShouldBeEmpty();
        controlPlane.Approved.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_Lost_Connection_During_The_Upload_Leaves_The_Action_Approved()
    {
        _youtube.UploadVideoAsync(Arg.Any<string>(), Arg.Any<YouTubeUpload>(), Arg.Any<UploadSession>(), Arg.Any<CancellationToken>())
            .Returns<string>(_ => throw new HttpRequestException("Connection reset by peer"));
        var (executor, controlPlane, gate) = Build(Approved(Publish()), "videos.insert", "p1");

        await executor.RunOnceAsync(CancellationToken.None);

        controlPlane.Saves[^1].Project.Publication!.Error.ShouldBe(TopicResearcher.YouTubeUnreachable);
        gate.Records.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_Upload_YouTube_Rejects_Fails_The_Publication_And_Is_Recorded_As_Failed_So_The_Queue_Moves_On()
    {
        _youtube.UploadVideoAsync(Arg.Any<string>(), Arg.Any<YouTubeUpload>(), Arg.Any<UploadSession>(), Arg.Any<CancellationToken>())
            .Returns<string>(_ => throw new YouTubeApiException("YouTube rejected the request (invalidTitle).", HttpStatusCode.BadRequest));
        var (executor, controlPlane, gate) = Build(Approved(Publish()), "videos.insert", "p1");

        await executor.RunOnceAsync(CancellationToken.None);

        var saved = controlPlane.Saves[^1].Project.Publication!;
        (saved.Status, saved.Error).ShouldBe(("failed", "YouTube rejected the request (invalidTitle)."));
        gate.Records.ShouldBe([("youtube", "videos.insert", "p1", "failed: YouTube rejected the request (invalidTitle).", "a1")]);
        controlPlane.Approved.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Project_Without_The_Owners_Publish_Settings_Is_Never_Uploaded()
    {
        var (executor, _, gate) = Build(Approved(publication: null), "videos.insert", "p1");

        await executor.RunOnceAsync(CancellationToken.None);

        await _youtube.DidNotReceiveWithAnyArgs().UploadVideoAsync(default!, default!, default!, default);
        gate.Records.ShouldHaveSingleItem().Outcome.ShouldBe($"failed: {ActionExecutor.NoPublishSettings("sourdough")}");
    }

    [Fact]
    public async Task A_Video_File_That_Is_Gone_Fails_The_Publication_Without_An_Upload()
    {
        File.Delete(Path.Combine(_media, "video.mp4"));
        var (executor, controlPlane, gate) = Build(Approved(Publish()), "videos.insert", "p1");

        await executor.RunOnceAsync(CancellationToken.None);

        await _youtube.DidNotReceiveWithAnyArgs().UploadVideoAsync(default!, default!, default!, default);
        controlPlane.Saves[^1].Project.Publication!.Status.ShouldBe("failed");
        gate.Records.ShouldHaveSingleItem().Outcome.ShouldBe($"failed: {ActionExecutor.MissingFile("video.mp4")}");
    }

    [Fact]
    public async Task The_Package_Thumbnail_Is_Set_On_The_Uploaded_Video()
    {
        var (executor, _, gate) = Build(Approved(Publish() with { Status = "uploaded", VideoId = Uploaded }), "thumbnails.set", "p1");

        await executor.RunOnceAsync(CancellationToken.None);

        await _youtube.Received(1).SetThumbnailAsync(Uploaded, Path.Combine(_media, "thumbnail.png"), Arg.Any<CancellationToken>());
        gate.Records.ShouldBe([("youtube", "thumbnails.set", "p1", "succeeded", "a1")]);
    }

    [Fact]
    public async Task A_Thumbnail_Waiting_For_Its_Upload_Stays_Approved()
    {
        var (executor, _, gate) = Build(Approved(Publish() with { Status = "uploading" }), "thumbnails.set", "p1");

        await executor.RunOnceAsync(CancellationToken.None);

        await _youtube.DidNotReceiveWithAnyArgs().SetThumbnailAsync(default!, default!, default);
        gate.Records.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Thumbnail_For_An_Upload_That_Failed_Is_Recorded_Failed_So_It_Does_Not_Hold_The_Queue()
    {
        var (executor, _, gate) = Build(Approved(Publish() with { Status = "failed", Error = "rejected" }), "thumbnails.set", "p1");

        await executor.RunOnceAsync(CancellationToken.None);

        gate.Records.ShouldHaveSingleItem().Outcome.ShouldBe($"failed: {ActionExecutor.NoVideo("sourdough")}");
    }

    [Fact]
    public async Task A_Thumbnail_YouTube_Forbids_Is_Recorded_Failed_With_The_Verified_Channel_Hint_And_Saved_On_The_Publication()
    {
        const string forbidden = "YouTube refused the thumbnail: custom thumbnails need a verified channel (verify it at youtube.com/verify), or the sign-in does not allow Scout to change this video.";
        _youtube.SetThumbnailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new YouTubeApiException(forbidden, HttpStatusCode.Forbidden));
        var (executor, controlPlane, gate) = Build(Approved(Publish() with { Status = "uploaded", VideoId = Uploaded }), "thumbnails.set", "p1");

        await executor.RunOnceAsync(CancellationToken.None);

        gate.Records.ShouldHaveSingleItem().Outcome.ShouldBe($"failed: {forbidden}");
        var saved = controlPlane.Saves.ShouldHaveSingleItem().Project.Publication!;
        (saved.Status, saved.Error).ShouldBe(("uploaded", forbidden));
    }

    [Fact]
    public async Task A_Title_Arm_Goes_Live_And_Starts_Its_Window_While_The_Running_Arm_Ends()
    {
        var (executor, controlPlane, gate) = Build(
            Approved(Publish() with { Status = "published", VideoId = Uploaded, Experiment = TitleTest() }), "videos.update", "p1:e1:b");

        await executor.RunOnceAsync(CancellationToken.None);

        await _youtube.Received(1).UpdateTitleAsync(Uploaded, "Can you bake bread in ten minutes?", Arg.Any<CancellationToken>());
        var arms = controlPlane.Saves.ShouldHaveSingleItem().Project.Publication!.Experiment!.Arms;
        arms.Select(a => (a.Id, a.StartedAt, a.EndedAt)).ShouldBe([
            ("control", "2026-11-02T12:00:00Z", "2026-11-03T12:00:00Z"),
            ("b", "2026-11-03T12:00:00Z", null),
        ]);
        gate.Records.ShouldBe([("youtube", "videos.update", "p1:e1:b", "succeeded", "a1")]);
    }

    [Fact]
    public async Task The_Restore_Puts_The_Control_Back_And_Ends_The_Last_Arm_Without_Reopening_The_Control_Window()
    {
        var experiment = TitleTest("running",
            new OperatorExperimentArm("control", "Original", "Bread in ten minutes", "2026-11-01T12:00:00Z", "2026-11-02T12:00:00Z", 0, 0, 0),
            new OperatorExperimentArm("b", "Question", "Can you bake bread in ten minutes?", "2026-11-02T12:00:00Z", null, 0, 0, 0));
        var (executor, controlPlane, _) = Build(
            Approved(Publish() with { Status = "published", VideoId = Uploaded, Experiment = experiment }), "videos.update", "p1:e1:control");

        await executor.RunOnceAsync(CancellationToken.None);

        await _youtube.Received(1).UpdateTitleAsync(Uploaded, "Bread in ten minutes", Arg.Any<CancellationToken>());
        controlPlane.Saves.ShouldHaveSingleItem().Project.Publication!.Experiment!.Arms.Select(a => (a.Id, a.StartedAt, a.EndedAt)).ShouldBe([
            ("control", "2026-11-01T12:00:00Z", "2026-11-02T12:00:00Z"),
            ("b", "2026-11-02T12:00:00Z", "2026-11-03T12:00:00Z"),
        ]);
    }

    [Fact]
    public async Task The_Adopted_Winner_Goes_Live_Without_Touching_The_Finished_Windows()
    {
        var (executor, controlPlane, gate) = Build(
            Approved(Publish() with { Status = "published", VideoId = Uploaded, Experiment = TitleTest("adopted") with { WinnerArmId = "b" } }), "videos.update", "p1:e1:b");

        await executor.RunOnceAsync(CancellationToken.None);

        await _youtube.Received(1).UpdateTitleAsync(Uploaded, "Can you bake bread in ten minutes?", Arg.Any<CancellationToken>());
        controlPlane.Saves.ShouldBeEmpty();
        gate.Records.ShouldHaveSingleItem().Outcome.ShouldBe("succeeded");
    }

    [Fact]
    public async Task A_Thumbnail_Arm_Sets_The_Arm_Picture_From_The_Media_Folder()
    {
        var experiment = new OperatorExperiment("e1", "thumbnail", "running", [
            new OperatorExperimentArm("control", "Original", "thumbnail.png", "2026-11-02T12:00:00Z", null, 0, 0, 0),
            new OperatorExperimentArm("b", "Dark table", "thumbnail-proposal-24h-1.png", null, null, 0, 0, 0),
        ], null, null);
        var (executor, _, gate) = Build(
            Approved(Publish() with { Status = "published", VideoId = Uploaded, Experiment = experiment }), "thumbnails.set", "p1:e1:b");

        await executor.RunOnceAsync(CancellationToken.None);

        await _youtube.Received(1).SetThumbnailAsync(Uploaded, Path.Combine(_media, "thumbnail-proposal-24h-1.png"), Arg.Any<CancellationToken>());
        gate.Records.ShouldHaveSingleItem().Outcome.ShouldBe("succeeded");
    }

    [Fact]
    public async Task An_Arm_Of_Another_Kind_Than_The_Experiment_Is_Refused_Without_A_YouTube_Call()
    {
        var (executor, _, gate) = Build(
            Approved(Publish() with { Status = "published", VideoId = Uploaded, Experiment = TitleTest() }), "thumbnails.set", "p1:e1:b");

        await executor.RunOnceAsync(CancellationToken.None);

        _youtube.ReceivedCalls().ShouldBeEmpty();
        gate.Records.ShouldHaveSingleItem().Outcome.ShouldStartWith("failed: ");
    }

    [Theory]
    [InlineData("comments.insert", "p1")]
    [InlineData("comments.delete", "comment:c1")]
    [InlineData("comments.setModerationStatus", "p1:e1:b")]
    [InlineData("videos.insert", "p1:e1:b")]
    public async Task An_Action_Scout_Does_Not_Run_Is_Recorded_Failed_So_It_Does_Not_Hold_The_Queue(string action, string target)
    {
        var (executor, _, gate) = Build(Approved(Publish()), action, target);

        await executor.RunOnceAsync(CancellationToken.None);

        _youtube.ReceivedCalls().ShouldBeEmpty();
        gate.Records.ShouldHaveSingleItem().Outcome.ShouldBe($"failed: {ActionExecutor.Unsupported(action, target)}");
    }

    [Fact]
    public async Task A_Control_Plane_That_Cannot_Hand_Out_Actions_Is_Logged_Once_Until_It_Answers()
    {
        var (executor, controlPlane, gate) = Build(Approved(Publish()), "videos.insert", "p1");
        controlPlane.ActionFailure = new ControlPlaneUnavailableException("Scout could not reach its control plane.");

        await executor.RunOnceAsync(CancellationToken.None);
        await executor.RunOnceAsync(CancellationToken.None);

        _logs.Entries.ShouldHaveSingleItem().ShouldBe((LogLevel.Warning, "The operator cannot read approved YouTube actions: Scout could not reach its control plane."));
        _youtube.ReceivedCalls().ShouldBeEmpty();
        gate.Records.ShouldBeEmpty();
    }

    [Fact]
    public async Task After_A_Crash_Between_The_Upload_And_The_Record_The_Action_Handed_Out_Again_Is_Recorded_Without_A_Second_Upload()
    {
        var (_, controlPlane, gate) = Build(Approved(Publish()), "videos.insert", "p1");
        controlPlane.RecordFailure = new ControlPlaneUnavailableException("Scout could not reach its control plane.");
        var first = new ActionExecutor(controlPlane, _youtube, Microsoft.Extensions.Options.Options.Create(new OperatorMediaOptions()), _time, _logs, _notifier, Desk(controlPlane));
        await first.RunOnceAsync(CancellationToken.None);
        controlPlane.Saves[^1].Project.Publication!.VideoId.ShouldBe(Uploaded);
        controlPlane.RecordFailure = null;
        var afterRestart = new ActionExecutor(controlPlane, _youtube, Microsoft.Extensions.Options.Options.Create(new OperatorMediaOptions()), _time, _logs, _notifier, Desk(controlPlane));

        await afterRestart.RunOnceAsync(CancellationToken.None);

        await _youtube.Received(1).UploadVideoAsync(Arg.Any<string>(), Arg.Any<YouTubeUpload>(), Arg.Any<UploadSession>(), Arg.Any<CancellationToken>());
        gate.Records.ShouldBe([("youtube", "videos.insert", "p1", "succeeded", "a1")]);
    }

    [Fact]
    public async Task An_Upload_The_Owner_Revoked_During_Saves_Its_Video_Then_Is_Recorded_After_Revoke_Once_And_Never_Uploaded_Again()
    {
        var (executor, controlPlane, _) = Build(Approved(Publish()), "videos.insert", "p1");
        _youtube.UploadVideoAsync(Arg.Any<string>(), Arg.Any<YouTubeUpload>(), Arg.Any<UploadSession>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                controlPlane.Revoked = true;
                return Uploaded;
            });

        await executor.RunOnceAsync(CancellationToken.None);
        await executor.RunOnceAsync(CancellationToken.None);

        var saved = controlPlane.Saves[^1].Project.Publication!;
        (saved.Status, saved.VideoId).ShouldBe(("uploaded", Uploaded));
        controlPlane.Recorded.Select(r => r.Outcome).ShouldBe(["succeeded after revoke"]);
        controlPlane.Approved.ShouldBeEmpty();
        await _youtube.Received(1).UploadVideoAsync(Arg.Any<string>(), Arg.Any<YouTubeUpload>(), Arg.Any<UploadSession>(), Arg.Any<CancellationToken>());
        _logs.Entries.ShouldContain((LogLevel.Warning, "The owner revoked YouTube writes after YouTube action a1 ran; the change on YouTube stays."));
    }

    [Fact]
    public async Task An_Arm_Switch_The_Owner_Revoked_During_Saves_Its_Timing_Then_Is_Recorded_After_Revoke_Never_Failed()
    {
        var (executor, controlPlane, _) = Build(
            Approved(Publish() with { Status = "published", VideoId = Uploaded, Experiment = TitleTest() }), "videos.update", "p1:e1:b");
        _youtube.UpdateTitleAsync(Uploaded, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                controlPlane.Revoked = true;
                return Task.CompletedTask;
            });

        await executor.RunOnceAsync(CancellationToken.None);

        controlPlane.Saves.ShouldHaveSingleItem().Project.Publication!.Experiment!.Arms.Single(a => a.Id == "b").StartedAt.ShouldBe("2026-11-03T12:00:00Z");
        controlPlane.Recorded.Select(r => r.Outcome).ShouldBe(["succeeded after revoke"]);
        _logs.Entries.ShouldContain((LogLevel.Warning, "The owner revoked YouTube writes after YouTube action a1 ran; the change on YouTube stays."));
    }

    [Fact]
    public async Task An_Upload_Whose_Save_The_Control_Plane_Refuses_Is_Still_Recorded_As_Succeeded_With_Its_Video_Id()
    {
        var (executor, controlPlane, gate) = Build(Approved(Publish()), "videos.insert", "p1");
        var refusing = new RecordingControlPlane { MediaDirectory = _media, RefuseOnProject = p => p.Publication?.VideoId is not null };
        refusing.Store(Approved(Publish()));
        refusing.Approved.Enqueue(controlPlane.Approved.Peek());
        var refused = new ActionExecutor(refusing, _youtube, Microsoft.Extensions.Options.Options.Create(new OperatorMediaOptions()), _time, _logs, _notifier, Desk(refusing));

        await refused.RunOnceAsync(CancellationToken.None);

        refusing.Recorded.Select(r => r.Outcome).ShouldBe([$"succeeded: video {Uploaded}, not saved"]);
        await _youtube.Received(1).UploadVideoAsync(Arg.Any<string>(), Arg.Any<YouTubeUpload>(), Arg.Any<UploadSession>(), Arg.Any<CancellationToken>());
        gate.Records.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Title_Changed_On_YouTube_Whose_Save_Is_Refused_Is_Recorded_As_Succeeded_Never_Failed()
    {
        var refusing = new RecordingControlPlane { MediaDirectory = _media, RefuseOnProject = _ => true };
        refusing.Store(Approved(Publish() with { Status = "published", VideoId = Uploaded, Experiment = TitleTest() }));
        refusing.Approved.Enqueue(new OperatorAction("a1", "youtube", "videos.update", "p1:e1:b", "Set title"));
        var executor = new ActionExecutor(refusing, _youtube, Microsoft.Extensions.Options.Options.Create(new OperatorMediaOptions()), _time, _logs, _notifier, Desk(refusing));

        await executor.RunOnceAsync(CancellationToken.None);

        await _youtube.Received(1).UpdateTitleAsync(Uploaded, "Can you bake bread in ten minutes?", Arg.Any<CancellationToken>());
        refusing.Recorded.Select(r => r.Outcome).ShouldBe(["succeeded: videos.update p1:e1:b, not saved"]);
    }

    [Fact]
    public async Task The_Kept_Upload_Session_Ends_With_Its_Action_When_The_Upload_Fails_For_Good()
    {
        _youtube.UploadVideoAsync(Arg.Any<string>(), Arg.Any<YouTubeUpload>(), Arg.Any<UploadSession>(), Arg.Any<CancellationToken>())
            .Returns<string>(
                _ => throw new YouTubeApiException("YouTube is currently unavailable. Try again later.", HttpStatusCode.ServiceUnavailable) { Retryable = true },
                _ => throw new YouTubeApiException("YouTube rejected the request (invalidTitle).", HttpStatusCode.BadRequest));
        var (executor, _, gate) = Build(Approved(Publish()), "videos.insert", "p1");

        await executor.RunOnceAsync(CancellationToken.None);
        executor.OpenSessions.ShouldBe(1);
        await executor.RunOnceAsync(CancellationToken.None);

        executor.OpenSessions.ShouldBe(0);
        gate.Records.ShouldHaveSingleItem().Outcome.ShouldStartWith("failed: ");
    }

    [Fact]
    public async Task An_Upload_Without_The_Upload_Permission_Keeps_The_Approved_Action_And_Saves_Why()
    {
        _youtube.UploadVideoAsync(Arg.Any<string>(), Arg.Any<YouTubeUpload>(), Arg.Any<UploadSession>(), Arg.Any<CancellationToken>())
            .Returns<string>(_ => throw new YouTubeApiException(
                "YouTube needs the upload permission: open the YouTube card in Scout Desktop and allow writes again.", HttpStatusCode.Forbidden)
            { Retryable = true });
        var (executor, controlPlane, gate) = Build(Approved(Publish()), "videos.insert", "p1");

        await executor.RunOnceAsync(CancellationToken.None);

        controlPlane.Saves[^1].Project.Publication!.Error.ShouldBe("YouTube needs the upload permission: open the YouTube card in Scout Desktop and allow writes again.");
        gate.Records.ShouldBeEmpty();
        controlPlane.Approved.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task An_Upload_Started_From_A_Chat_Posts_There_That_It_Is_Uploaded_With_Its_Link()
    {
        _notifier.PostAsync(default!, default!, default).ReturnsForAnyArgs(true);
        var (executor, _, _) = Build(Approved(Publish("unlisted")), "videos.insert", "p1", origin: "discord:3333:42");

        await executor.RunOnceAsync(CancellationToken.None);

        await _notifier.Received(1).PostAsync("discord:3333:42", "Uploaded sourdough as unlisted: https://youtu.be/newVideo001", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_Scheduled_Upload_Posts_Its_Publish_Time_On_The_London_Clock()
    {
        _notifier.PostAsync(default!, default!, default).ReturnsForAnyArgs(true);
        var (executor, _, _) = Build(Approved(Publish("public", "2026-11-10T17:00:00Z")), "videos.insert", "p1", origin: "discord:3333:42");

        await executor.RunOnceAsync(CancellationToken.None);

        await _notifier.Received(1).PostAsync(
            "discord:3333:42", "Scheduled sourdough for Tuesday 10 November 2026 17:00 London time: https://youtu.be/newVideo001", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_Upload_Started_From_The_Desktop_Posts_Nothing()
    {
        var (executor, _, gate) = Build(Approved(Publish()), "videos.insert", "p1");

        await executor.RunOnceAsync(CancellationToken.None);

        _notifier.ReceivedCalls().ShouldBeEmpty();
        gate.Records.ShouldHaveSingleItem().Outcome.ShouldBe("succeeded");
    }

    [Fact]
    public async Task A_Failed_Upload_Posts_Why_To_The_Chat_And_A_Failure_Scout_Retries_Posts_Nothing()
    {
        _youtube.UploadVideoAsync(Arg.Any<string>(), Arg.Any<YouTubeUpload>(), Arg.Any<UploadSession>(), Arg.Any<CancellationToken>())
            .Returns<string>(
                _ => throw new YouTubeApiException("YouTube is currently unavailable. Try again later.", HttpStatusCode.ServiceUnavailable) { Retryable = true },
                _ => throw new YouTubeApiException("YouTube rejected the request (invalidTitle).", HttpStatusCode.BadRequest));
        var (executor, _, _) = Build(Approved(Publish()), "videos.insert", "p1", origin: "discord:3333:42");

        await executor.RunOnceAsync(CancellationToken.None);
        _notifier.ReceivedCalls().ShouldBeEmpty();
        await executor.RunOnceAsync(CancellationToken.None);

        await _notifier.Received(1).PostAsync(
            "discord:3333:42", "The video project on sourdough stopped: YouTube rejected the request (invalidTitle).", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_Post_The_Chat_Cannot_Take_Is_Logged_And_The_Upload_Is_Still_Recorded()
    {
        _notifier.PostAsync(default!, default!, default).ReturnsForAnyArgs<bool>(_ => throw new HttpRequestException("Connection reset by peer"));
        var (executor, controlPlane, gate) = Build(Approved(Publish()), "videos.insert", "p1", origin: "discord:3333:42");

        await executor.RunOnceAsync(CancellationToken.None);

        gate.Records.ShouldBe([("youtube", "videos.insert", "p1", "succeeded", "a1")]);
        controlPlane.Saves[^1].Project.Publication!.Status.ShouldBe("uploaded");
        _logs.Entries.ShouldContain((LogLevel.Warning, "Posting the progress of YouTube action a1 failed (HttpRequestException)."));
    }

    [Fact]
    public async Task A_Claimed_Upload_Found_Uploading_Without_This_Process_Knowing_Its_Session_Is_Never_Uploaded_Again()
    {
        var (executor, controlPlane, gate) = Build(Approved(Publish() with { Status = "uploading" }), "videos.insert", "p1");

        await executor.RunOnceAsync(CancellationToken.None);

        await _youtube.DidNotReceiveWithAnyArgs().UploadVideoAsync(default!, default!, default!, default);
        var saved = controlPlane.Saves.ShouldHaveSingleItem().Project.Publication!;
        (saved.Status, saved.Error).ShouldBe(("failed", "The upload may already be on YouTube; check YouTube Studio before publishing again."));
        gate.Records.ShouldBe([("youtube", "videos.insert", "p1", "failed: The upload may already be on YouTube; check YouTube Studio before publishing again.", "a1")]);
    }

    [Fact]
    public async Task An_Upload_Interrupted_In_This_Process_Resumes_Its_Own_Session_On_The_Next_Run()
    {
        var sessions = new List<UploadSession>();
        _youtube.UploadVideoAsync(Arg.Any<string>(), Arg.Any<YouTubeUpload>(), Arg.Do<UploadSession>(sessions.Add), Arg.Any<CancellationToken>())
            .Returns(
                call =>
                {
                    call.Arg<UploadSession>().Address = new Uri("https://www.googleapis.com/upload/youtube/v3/videos?upload_id=s1");
                    throw new YouTubeApiException("YouTube's upload rate limit is reached for now; Scout tries again later.", HttpStatusCode.TooManyRequests) { Retryable = true };
                },
                _ => Uploaded);
        var (executor, controlPlane, gate) = Build(Approved(Publish()), "videos.insert", "p1");

        await executor.RunOnceAsync(CancellationToken.None);
        await executor.RunOnceAsync(CancellationToken.None);

        sessions.Count.ShouldBe(2);
        sessions[1].ShouldBeSameAs(sessions[0]);
        sessions[1].Address.ShouldBe(new Uri("https://www.googleapis.com/upload/youtube/v3/videos?upload_id=s1"));
        controlPlane.Saves[^1].Project.Publication!.VideoId.ShouldBe(Uploaded);
        gate.Records.ShouldBe([("youtube", "videos.insert", "p1", "succeeded", "a1")]);
    }


    [Fact]
    public async Task A_Record_Refused_For_Another_Reason_Than_A_Revoke_Saves_No_Revoke_Sentence()
    {
        var (executor, controlPlane, _) = Build(Approved(Publish()), "videos.insert", "p1");
        controlPlane.RecordFailure = new InvalidOperationException("Action 'a1' is not approved by the owner.");

        await executor.RunOnceAsync(CancellationToken.None);

        controlPlane.Saves[^1].Project.Publication!.Error.ShouldBeNull();
        _logs.Entries.ShouldContain((LogLevel.Warning, "YouTube action a1 ran but the control plane refused to record it: Action 'a1' is not approved by the owner."));
    }

    [Theory]
    [InlineData("concluded")]
    [InlineData("inconclusive")]
    public async Task An_Arm_Switch_After_The_Experiment_Ended_Changes_Nothing_On_YouTube_And_Is_Recorded_Skipped(string status)
    {
        var (executor, controlPlane, gate) = Build(
            Approved(Publish() with { Status = "published", VideoId = Uploaded, Experiment = TitleTest(status) }), "videos.update", "p1:e1:b");

        await executor.RunOnceAsync(CancellationToken.None);

        _youtube.ReceivedCalls().ShouldBeEmpty();
        controlPlane.Saves.ShouldBeEmpty();
        gate.Records.ShouldBe([("youtube", "videos.update", "p1:e1:b", "skipped: the experiment ended", "a1")]);
    }

    [Fact]
    public async Task The_Restore_To_The_Control_Still_Runs_After_The_Experiment_Ended()
    {
        var (executor, _, gate) = Build(
            Approved(Publish() with { Status = "published", VideoId = Uploaded, Experiment = TitleTest("concluded") }), "videos.update", "p1:e1:control");

        await executor.RunOnceAsync(CancellationToken.None);

        await _youtube.Received(1).UpdateTitleAsync(Uploaded, "Bread in ten minutes", Arg.Any<CancellationToken>());
        gate.Records.ShouldHaveSingleItem().Outcome.ShouldBe("succeeded");
    }

    private const string ReplyId = "c1.reply0001";

    private static OperatorComment Comment(string status, string? approved = "Feed it daily for a week.", string? replyId = null)
        => new("c1", "thread-c1", "video000001", "Sourdough basics", "@breadfan", "How long do I feed it?", "2026-11-02T09:30:00.0000000Z", 3,
            "question", "feeding", status, "Every day, probably.", approved, replyId, null, "2026-11-03T11:59:59.9876543+00:00", "2026-11-02T10:00:00.0000000+00:00");

    private (ActionExecutor Executor, RecordingControlPlane ControlPlane, RecordedActions Gate) BuildComment(OperatorComment comment, string action = "comments.insert")
    {
        var (executor, controlPlane, gate) = Build(Approved(Publish()), action, "comment:c1");
        controlPlane.Comments = new([comment, Comment("new", approved: null) with { Id = "c2" }], [], "2026-11-03T08:00:00.0000000Z", null, null);
        _youtube.ReplyToCommentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ReplyId);
        return (executor, controlPlane, gate);
    }

    [Fact]
    public async Task Reply_Posts_The_Approved_Text_Once_And_Saves_Its_Id()
    {
        var (executor, controlPlane, gate) = BuildComment(Comment("reply_approved"));

        await executor.RunOnceAsync(CancellationToken.None);

        await _youtube.Received(1).ReplyToCommentAsync("c1", "Feed it daily for a week.", Arg.Any<CancellationToken>());
        controlPlane.CommentSaves.Select(s => (s.Comments[0].Status, s.Comments[0].ReplyId)).ShouldBe([("replying", null), ("replied", ReplyId)]);
        controlPlane.Comments.Comments.ShouldBe([Comment("replied", replyId: ReplyId), Comment("new", approved: null) with { Id = "c2" }]);
        gate.Records.ShouldHaveSingleItem().ShouldBe(("youtube", "comments.insert", "comment:c1", "succeeded", "a1"));
    }

    [Fact]
    public async Task Reply_Found_Replying_After_A_Restart_Is_Failed_Not_Posted_Again()
    {
        var (executor, controlPlane, gate) = BuildComment(Comment("replying"));

        await executor.RunOnceAsync(CancellationToken.None);

        await _youtube.DidNotReceive().ReplyToCommentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        controlPlane.Comments.Comments[0].ShouldBe(Comment("failed") with { Error = ActionExecutor.MayAlreadyBeReplied });
        gate.Records.ShouldHaveSingleItem().Outcome.ShouldBe($"failed: {ActionExecutor.MayAlreadyBeReplied}");
    }

    [Fact]
    public async Task Reply_With_A_Saved_Id_Is_Only_Recorded()
    {
        var (executor, controlPlane, gate) = BuildComment(Comment("replied", replyId: ReplyId));

        await executor.RunOnceAsync(CancellationToken.None);

        _youtube.ReceivedCalls().Where(c => c.GetMethodInfo().Name != nameof(IYouTubeWriteClient.UploadVideoAsync)).ShouldBeEmpty();
        controlPlane.CommentSaves.ShouldBeEmpty();
        gate.Records.ShouldHaveSingleItem().Outcome.ShouldBe("succeeded");
    }

    [Fact]
    public async Task A_Reply_YouTube_Surely_Refused_Is_Posted_Again_Next_Run_But_One_That_May_Have_Landed_Is_Failed()
    {
        var (sure, sureControlPlane, sureGate) = BuildComment(Comment("reply_approved"));
        _youtube.ReplyToCommentAsync("c1", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(
            _ => throw new YouTubeApiException("YouTube's upload rate limit is reached for now; Scout tries again later.", HttpStatusCode.TooManyRequests) { Retryable = true },
            _ => Task.FromResult(ReplyId));

        await sure.RunOnceAsync(CancellationToken.None);
        await sure.RunOnceAsync(CancellationToken.None);
        var (unsure, unsureControlPlane, unsureGate) = BuildComment(Comment("reply_approved"));
        _youtube.ReplyToCommentAsync("c1", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(
            _ => throw new YouTubeApiException("YouTube is currently unavailable. Try again later.", HttpStatusCode.BadGateway) { Retryable = true },
            _ => Task.FromResult(ReplyId));
        await unsure.RunOnceAsync(CancellationToken.None);
        await unsure.RunOnceAsync(CancellationToken.None);

        (sureControlPlane.Comments.Comments[0].Status, sureControlPlane.Comments.Comments[0].ReplyId).ShouldBe(("replied", ReplyId));
        sureGate.Records.ShouldHaveSingleItem().Outcome.ShouldBe("succeeded");
        (unsureControlPlane.Comments.Comments[0].Status, unsureControlPlane.Comments.Comments[0].Error).ShouldBe(("failed", ActionExecutor.MayAlreadyBeReplied));
        unsureGate.Records.ShouldHaveSingleItem().Outcome.ShouldBe($"failed: {ActionExecutor.MayAlreadyBeReplied}");
        await _youtube.Received(3).ReplyToCommentAsync("c1", "Feed it daily for a week.", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_Reply_YouTube_Accepted_With_An_Unreadable_Answer_Is_Never_Posted_Again()
    {
        var (executor, controlPlane, gate) = BuildComment(Comment("reply_approved"));
        _youtube.ReplyToCommentAsync("c1", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(
            _ => throw new YouTubeApiException("YouTube returned an unexpected response.", HttpStatusCode.OK),
            _ => Task.FromResult(ReplyId));

        await executor.RunOnceAsync(CancellationToken.None);
        controlPlane.Approved.Enqueue(new OperatorAction("a1", "youtube", "comments.insert", "comment:c1", "comments.insert comment:c1"));
        await executor.RunOnceAsync(CancellationToken.None);

        await _youtube.Received(1).ReplyToCommentAsync("c1", "Feed it daily for a week.", Arg.Any<CancellationToken>());
        (controlPlane.Comments.Comments[0].Status, controlPlane.Comments.Comments[0].Error).ShouldBe(("failed", ActionExecutor.MayAlreadyBeReplied));
        gate.Records.Select(r => r.Outcome).ShouldBe([$"failed: {ActionExecutor.MayAlreadyBeReplied}", $"failed: {ActionExecutor.NotApprovedReply("c1", "failed")}"]);
    }

    [Fact]
    public async Task A_Reply_YouTube_Took_Whose_Save_Is_Refused_Is_Still_Recorded_Succeeded()
    {
        var (_, controlPlane, _) = BuildComment(Comment("reply_approved"));
        var refusing = new RecordingControlPlane
        {
            CommentSaveFailure = n => n == 2 ? new InvalidOperationException("Only the owner moves comment 'c2' from dismissed to new.") : null,
        };
        refusing.Comments = controlPlane.Comments;
        refusing.Approved.Enqueue(controlPlane.Approved.Peek());
        var refused = new ActionExecutor(refusing, _youtube, Microsoft.Extensions.Options.Options.Create(new OperatorMediaOptions()), _time, _logs, _notifier, Desk(refusing));

        await refused.RunOnceAsync(CancellationToken.None);

        refusing.Recorded.ShouldHaveSingleItem().Outcome.ShouldBe("succeeded: comment c1, not saved");
        await _youtube.Received(1).ReplyToCommentAsync("c1", "Feed it daily for a week.", Arg.Any<CancellationToken>());
        refusing.Comments.Comments[0].Status.ShouldBe("replying");
    }

    [Fact]
    public async Task A_Reply_The_Owner_Revoked_During_Is_Saved_On_The_State_The_Last_Save_Answered_Then_Recorded_After_Revoke()
    {
        var (executor, controlPlane, gate) = BuildComment(Comment("reply_approved"));
        _youtube.ReplyToCommentAsync("c1", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            controlPlane.Revoked = true;
            return Task.FromResult(ReplyId);
        });

        await executor.RunOnceAsync(CancellationToken.None);

        controlPlane.Comments.Comments[0].ShouldBe(Comment("replied", replyId: ReplyId));
        gate.Records.ShouldHaveSingleItem().Outcome.ShouldBe("succeeded after revoke");
    }

    [Fact]
    public async Task Hold_Sets_Held_For_Review_And_Saves_Held()
    {
        var (executor, controlPlane, gate) = BuildComment(Comment("hold_approved", approved: null) with { Kind = "spam" }, "comments.setModerationStatus");

        await executor.RunOnceAsync(CancellationToken.None);

        await _youtube.Received(1).HoldCommentAsync("c1", Arg.Any<CancellationToken>());
        controlPlane.Comments.Comments[0].ShouldBe(Comment("held", approved: null) with { Kind = "spam" });
        gate.Records.ShouldHaveSingleItem().ShouldBe(("youtube", "comments.setModerationStatus", "comment:c1", "succeeded", "a1"));
    }

    [Fact]
    public async Task A_Hold_On_A_Deleted_Comment_Saves_The_Comment_Failed_And_A_5xx_Leaves_The_Action()
    {
        var (gone, goneControlPlane, goneGate) = BuildComment(Comment("hold_approved", approved: null), "comments.setModerationStatus");
        _youtube.HoldCommentAsync("c1", Arg.Any<CancellationToken>()).Returns(
            _ => throw new YouTubeApiException("YouTube refused the change to this comment: it may have been deleted, or the sign-in does not allow Scout to change it.", HttpStatusCode.Forbidden));
        await gone.RunOnceAsync(CancellationToken.None);
        var (down, downControlPlane, downGate) = BuildComment(Comment("hold_approved", approved: null), "comments.setModerationStatus");
        _youtube.HoldCommentAsync("c1", Arg.Any<CancellationToken>()).Returns(
            _ => throw new YouTubeApiException("YouTube is currently unavailable. Try again later.", HttpStatusCode.ServiceUnavailable) { Retryable = true });
        await down.RunOnceAsync(CancellationToken.None);

        goneControlPlane.Comments.Comments[0].ShouldBe(Comment("failed", approved: null) with
        {
            Error = "YouTube refused the change to this comment: it may have been deleted, or the sign-in does not allow Scout to change it.",
        });
        goneGate.Records.ShouldHaveSingleItem().Outcome.ShouldStartWith("failed: YouTube refused the change to this comment");
        downControlPlane.CommentSaves.ShouldBeEmpty();
        downGate.Records.ShouldBeEmpty();
        downControlPlane.Approved.Count.ShouldBe(1);
    }
}
