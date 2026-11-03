using System.Threading.Channels;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.ControlPlane.Contracts;
using Scout.Conversation.Abstractions;
using Scout.Operator;
using Scout.Plugin.YouTube.Abstractions;
using Shouldly;
using Xunit;
using NSubstitute;
using static Scout.Host.Console.Tests.GrpcWriteGateTests;

namespace Scout.Host.Console.Tests;

public sealed class GrpcOperatorControlPlaneTests
{
    private const string Token = "worker-session-token";

    private static OperatorProjectState Ready() => new(
        "p1",
        "sourdough",
        "script_ready",
        "chat",
        "2026-11-03T12:00:00Z",
        "2026-11-03T12:05:00Z",
        [new OperatorTopicCandidate("Sourdough basics", "fits", 0.8)],
        new OperatorScript(
            "Bread in ten minutes?",
            [new OperatorScriptSection("Starter", "Feed it daily.")],
            "Subscribe",
            ["A", "B", "C"],
            "How to bake.",
            ["bread"],
            [new OperatorChapter("0:00", "Intro")]),
        [new OperatorClaim("c1", "Starters need daily feeding", "high", ["https://www.youtube.com/watch?v=found000001"], "pending")]);

    [Fact]
    public async Task A_Saved_Project_Travels_Whole_With_Its_Origin_And_The_Worker_Token_And_Comes_Back_Unchanged()
    {
        var service = new FakeWorkerGate();
        await using var server = await WorkerGateServer.StartAsync(service);
        using var gate = new GrpcWriteGate(server.SocketPath, Token);

        var saved = await new GrpcOperatorControlPlane(gate).SaveProjectAsync(Ready(), "discord:3333:42", CancellationToken.None);

        service.Saved.ShouldNotBeNull().OriginReference.ShouldBe("discord:3333:42");
        service.Saved.Project.Script.Hook.ShouldBe("Bread in ten minutes?");
        service.Saved.Project.HasError.ShouldBeFalse();
        service.Authorizations.ShouldBe(["Bearer worker-session-token"]);
        saved.Script.ShouldBe(Ready().Script, new ScriptComparer());
        saved.Claims.ShouldHaveSingleItem().SourceUrls.ShouldBe(["https://www.youtube.com/watch?v=found000001"]);
        (saved.Id, saved.Status, saved.Error, saved.Candidates.ShouldHaveSingleItem()).ShouldBe(
            ("p1", "script_ready", null, new OperatorTopicCandidate("Sourdough basics", "fits", 0.8)));
    }

    [Fact]
    public async Task A_Queued_Project_Comes_With_Its_Origin_And_An_Empty_Reply_Means_None()
    {
        var queued = new FakeWorkerGate
        {
            NextReply = new NextQueuedProjectResponse
            {
                Project = new Project { Id = "desk-1", Topic = "sourdough", Status = "researching", Origin = "desktop" },
                OriginReference = "slack:C1:1.2",
            },
        };
        await using var withProject = await WorkerGateServer.StartAsync(queued);
        await using var empty = await WorkerGateServer.StartAsync(new FakeWorkerGate());
        using var gate = new GrpcWriteGate(withProject.SocketPath, Token);
        using var emptyGate = new GrpcWriteGate(empty.SocketPath, Token);

        var next = await new GrpcOperatorControlPlane(gate).NextQueuedAsync(CancellationToken.None);
        var none = await new GrpcOperatorControlPlane(emptyGate).NextQueuedAsync(CancellationToken.None);

        next.ShouldNotBeNull().OriginReference.ShouldBe("slack:C1:1.2");
        (next.Project.Id, next.Project.Topic, next.Project.Script).ShouldBe(("desk-1", "sourdough", null));
        none.ShouldBeNull();
    }

    [Fact]
    public async Task A_Project_Read_Back_Carries_The_Owners_Claim_Decisions_And_The_List_Asks_For_The_Limit()
    {
        var decided = new Claim { Id = "c1", Text = "Rye has no gluten", Risk = "high", Status = "waived", Note = "Owner checked the label" };
        var project = new Project { Id = "p1", Topic = "sourdough", Status = "script_ready", Origin = "chat", Error = "none" };
        project.Claims.Add(decided);
        var list = new ListProjectsResponse();
        list.Projects.Add(project);
        list.Projects.Add(new Project { Id = "p0", Topic = "rye", Status = "failed", Origin = "desktop" });
        var service = new FakeWorkerGate { ListReply = list };
        await using var server = await WorkerGateServer.StartAsync(service);
        using var gate = new GrpcWriteGate(server.SocketPath, Token);
        var controlPlane = new GrpcOperatorControlPlane(gate);

        var read = await controlPlane.GetProjectAsync("p1", CancellationToken.None);
        var latest = await controlPlane.ListRecentAsync(5, CancellationToken.None);

        service.Got.ShouldNotBeNull().Id.ShouldBe("p1");
        var claim = read.Claims.ShouldHaveSingleItem();
        (claim.Id, claim.Text, claim.Risk, claim.Status, claim.Note).ShouldBe(
            ("c1", "Rye has no gluten", "high", "waived", "Owner checked the label"));
        claim.SourceUrls.ShouldBeEmpty();
        read.Error.ShouldBe("none");
        service.Listed.ShouldNotBeNull().Limit.ShouldBe(5);
        latest.Select(p => (p.Id, p.Status)).ShouldBe([("p1", "script_ready"), ("p0", "failed")]);
        service.Authorizations.ShouldBe(["Bearer worker-session-token", "Bearer worker-session-token"]);
    }

    [Fact]
    public async Task The_Mandate_Arrives_With_Its_Pillars_Language_And_Blocked_Topics()
    {
        var mandate = new Mandate { Objective = "Teach baking", Language = "Portuguese" };
        mandate.Pillars.Add("starters");
        mandate.BlockedTopics.Add("diets");
        await using var server = await WorkerGateServer.StartAsync(new FakeWorkerGate { MandateReply = mandate });
        using var gate = new GrpcWriteGate(server.SocketPath, Token);

        var read = await new GrpcOperatorControlPlane(gate).GetMandateAsync(CancellationToken.None);

        (read.Objective, read.Language, read.UpdatedAt).ShouldBe(("Teach baking", "Portuguese", null));
        read.Pillars.ShouldBe(["starters"]);
        read.BlockedTopics.ShouldBe(["diets"]);
    }

    [Theory]
    [InlineData(StatusCode.PermissionDenied, "The owner has not granted YouTube writes.", "The owner has not granted YouTube writes.")]
    [InlineData(StatusCode.InvalidArgument, "The topic must be 3 to 200 characters.", "The topic must be 3 to 200 characters.")]
    [InlineData(StatusCode.Internal, "System.IO.IOException: disk full at /home/owner", "Scout could not reach its control plane.")]
    [InlineData(StatusCode.Unauthenticated, "bad token", "Scout could not reach its control plane.")]
    public async Task A_Refusal_The_Control_Plane_Words_Keeps_Its_Words_And_Any_Other_Failure_Reads_Unreachable(
        StatusCode code, string detail, string expected)
    {
        await using var server = await WorkerGateServer.StartAsync(
            new FakeWorkerGate { OperatorFailure = new RpcException(new Status(code, detail)) });
        using var gate = new GrpcWriteGate(server.SocketPath, Token);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => new GrpcOperatorControlPlane(gate).SaveProjectAsync(Ready(), null, CancellationToken.None));

        ex.Message.ShouldBe(expected);
    }

    [Fact]
    public async Task Scenes_Travel_Field_By_Field_And_An_Absent_Error_Or_File_Stays_Absent_On_The_Wire()
    {
        var withMedia = new OperatorScene("s1", 1, "Feed it daily.", "A jar of starter", false, "ready", null, "generated", "text_card", "scene-s1.mp3", null, 4000);
        var failed = new OperatorScene("s2", 2, "Bake it.", "An oven", true, "failed", "The AI Source could not make the picture of scene 2.", "owner", "owner", "mine.wav", "mine.png", 900);
        var service = new FakeWorkerGate();
        await using var server = await WorkerGateServer.StartAsync(service);
        using var gate = new GrpcWriteGate(server.SocketPath, Token);

        var saved = await new GrpcOperatorControlPlane(gate).SaveProjectAsync(
            Ready() with { Status = "producing", Scenes = [withMedia, failed] }, null, CancellationToken.None);

        var wire = service.Saved.ShouldNotBeNull().Project.Scenes;
        wire.Select(w => w.Id).ShouldBe(["s1", "s2"]);
        (wire[0].HasError, wire[0].HasAudioFile, wire[0].HasVisualFile).ShouldBe((false, true, false));
        (wire[1].Locked, wire[1].Error, wire[1].AudioSource, wire[1].VisualFile).ShouldBe(
            (true, "The AI Source could not make the picture of scene 2.", "owner", "mine.png"));
        saved.Scenes.ShouldBe([withMedia, failed]);
    }

    [Fact]
    public async Task The_Media_Directory_Is_Asked_For_By_Project_Id_And_Readiness_Is_Reported_Whole()
    {
        var service = new FakeWorkerGate();
        await using var server = await WorkerGateServer.StartAsync(service);
        using var gate = new GrpcWriteGate(server.SocketPath, Token);
        var controlPlane = new GrpcOperatorControlPlane(gate);

        var directory = await controlPlane.GetMediaDirectoryAsync("p1", CancellationToken.None);
        await controlPlane.ReportReadinessAsync(
            new OperatorReadiness(true, false, "AzureOpenAI", false, true, ["AzureOpenAI has no image service in Scout."]),
            CancellationToken.None);

        directory.ShouldBe("/data/scout/operator/media/p1");
        service.Got.ShouldNotBeNull().Id.ShouldBe("p1");
        var reported = service.Reported.ShouldNotBeNull();
        (reported.SpeechAvailable, reported.ImagesAvailable, reported.Provider, reported.FfmpegAvailable, reported.YoutubeSignedIn)
            .ShouldBe((true, false, "AzureOpenAI", false, true));
        reported.Notes.ShouldBe(["AzureOpenAI has no image service in Scout."]);
        reported.HasReportedAt.ShouldBeFalse();
        service.Authorizations.ShouldBe(["Bearer worker-session-token", "Bearer worker-session-token"]);
    }

    [Fact]
    public async Task A_Media_Directory_Or_Readiness_Call_That_Fails_Reads_As_The_Control_Planes_Words_Or_Unreachable()
    {
        await using var refusing = await WorkerGateServer.StartAsync(
            new FakeWorkerGate { OperatorFailure = new RpcException(new Status(StatusCode.PermissionDenied, "The owner has not granted YouTube writes.")) });
        await using var broken = await WorkerGateServer.StartAsync(
            new FakeWorkerGate { OperatorFailure = new RpcException(new Status(StatusCode.Unavailable, "connection reset")) });
        using var refusingGate = new GrpcWriteGate(refusing.SocketPath, Token);
        using var brokenGate = new GrpcWriteGate(broken.SocketPath, Token);
        var missingSocket = Path.Combine(Path.GetTempPath(), $"scout-no-gate-{Guid.NewGuid():N}.sock");
        using var missingGate = new GrpcWriteGate(missingSocket, Token);
        var readiness = new OperatorReadiness(false, false, "Gemini", false, false, []);

        var refused = await Should.ThrowAsync<InvalidOperationException>(
            () => new GrpcOperatorControlPlane(refusingGate).GetMediaDirectoryAsync("p1", CancellationToken.None));
        var reset = await Should.ThrowAsync<InvalidOperationException>(
            () => new GrpcOperatorControlPlane(brokenGate).ReportReadinessAsync(readiness, CancellationToken.None));
        var missing = await Should.ThrowAsync<InvalidOperationException>(
            () => new GrpcOperatorControlPlane(missingGate).GetMediaDirectoryAsync("p1", CancellationToken.None));

        refused.Message.ShouldBe("The owner has not granted YouTube writes.");
        reset.Message.ShouldBe("Scout could not reach its control plane.");
        missing.Message.ShouldBe("Scout could not reach its control plane.");
    }

    [Fact]
    public async Task With_The_Control_Plane_Down_Make_Video_Starts_No_Project_And_Says_Why()
    {
        var socketPath = Path.Combine(Path.GetTempPath(), $"scout-no-gate-{Guid.NewGuid():N}.sock");
        using var gate = new GrpcWriteGate(socketPath, Token);
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IOperatorControlPlane>(new GrpcOperatorControlPlane(gate));
        services.AddSingleton(Substitute.For<IYouTubeClient>());
        services.AddSingleton(Substitute.For<IChatCompletionService>());
        services.AddSingleton(Substitute.For<IOriginNotifier>());
        services.AddYouTubeOperator();
        using var provider = services.BuildServiceProvider();
        var kernel = new Kernel(provider, new KernelPluginCollection(provider.GetServices<KernelPlugin>()));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            kernel.InvokeAsync("YouTubeOperator", "youtube_make_video", new KernelArguments { ["topic"] = "sourdough" }));

        ex.Message.ShouldBe("Could not start a video project on sourdough: Scout could not reach its control plane.");
        provider.GetRequiredService<Channel<QueuedProject>>().Reader.TryRead(out _).ShouldBeFalse();
    }

    [Fact]
    public async Task The_Closed_Control_Plane_Refuses_Every_Call_Saying_The_Operator_Is_Off()
    {
        var closed = new ClosedOperatorControlPlane();

        var failures = new[]
        {
            await Should.ThrowAsync<InvalidOperationException>(() => closed.GetMandateAsync(CancellationToken.None)),
            await Should.ThrowAsync<InvalidOperationException>(() => closed.SaveProjectAsync(Ready(), null, CancellationToken.None)),
            await Should.ThrowAsync<InvalidOperationException>(() => closed.NextQueuedAsync(CancellationToken.None)),
            await Should.ThrowAsync<InvalidOperationException>(() => closed.GetProjectAsync("p1", CancellationToken.None)),
            await Should.ThrowAsync<InvalidOperationException>(() => closed.ListRecentAsync(5, CancellationToken.None)),
        };
        var mediaDirectory = await Should.ThrowAsync<InvalidOperationException>(() => closed.GetMediaDirectoryAsync("p1", CancellationToken.None));
        var readiness = await Should.ThrowAsync<InvalidOperationException>(
            () => closed.ReportReadinessAsync(new OperatorReadiness(false, false, "Gemini", false, false, []), CancellationToken.None));

        failures.Select(f => f.Message).ShouldAllBe(m => m == "Scout Desktop's control plane is not available, so the video operator is off.");
        mediaDirectory.Message.ShouldBe("Scout Desktop's control plane is not available, so the video operator is off.");
        readiness.Message.ShouldBe("Scout Desktop's control plane is not available, so the video operator is off.");
    }

    [Theory]
    [InlineData("youtube-key", "/run/scout/cp.sock", "t0ken", typeof(GrpcOperatorControlPlane))]
    [InlineData("youtube-key", null, null, typeof(ClosedOperatorControlPlane))]
    [InlineData(null, "/run/scout/cp.sock", "t0ken", null)]
    public void The_Operator_Is_Composed_Only_With_YouTube_And_Reaches_The_Control_Plane_Only_Through_An_Open_Gate(
        string? youtubeKey, string? socketPath, string? token, Type? expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Plugins:YouTube:ApiKey"] = youtubeKey,
                ["Scout:WorkerGate:SocketPath"] = socketPath,
                ["Scout:WorkerGate:Token"] = token,
            })
            .Build();
        using var provider = new ServiceCollection().AddLogging().AddScoutHost(configuration).BuildServiceProvider();

        (provider.GetService<IOperatorControlPlane>()?.GetType()).ShouldBe(expected);
        if (expected == typeof(GrpcOperatorControlPlane))
        {
            provider.GetRequiredService<Scout.Llm.Abstractions.IWriteGate>().ShouldBeSameAs(provider.GetRequiredService<GrpcWriteGate>());
        }
    }

    [Fact]
    public async Task A_Package_Travels_Field_By_Field_With_The_Owners_Fields_As_Read_And_An_Absent_File_Or_Error_Stays_Absent_On_The_Wire()
    {
        var assembled = new OperatorPackage(
            "assembled",
            "video.mp4",
            [new OperatorShort("short-1.mp4", 2000, 30_000, "Bread in ten minutes?")],
            "thumbnail.png",
            [new OperatorCheck("loudness", "warn", "The video plays at -20.0 LUFS; aim between -18 and -11 LUFS.")],
            true,
            "2026-11-03T13:00:00Z",
            "Bread in ten minutes",
            "How to bake.\n\n0:00 Starter",
            ["bread", "sourdough"],
            95_000,
            null,
            true);
        var failed = assembled with { Status = "failed", VideoFile = null, ThumbnailFile = null, ApprovedAt = null, Error = "Scout could not render scene 2", Shorts = [], Checks = [], RightsConfirmed = false, MetadataEdited = false };
        var service = new FakeWorkerGate();
        await using var server = await WorkerGateServer.StartAsync(service);
        using var gate = new GrpcWriteGate(server.SocketPath, Token);
        var controlPlane = new GrpcOperatorControlPlane(gate);

        var saved = await controlPlane.SaveProjectAsync(Ready() with { Status = "assembled", Package = assembled }, null, CancellationToken.None);
        var wire = service.Saved.ShouldNotBeNull().Project.Package;
        var savedFailed = await controlPlane.SaveProjectAsync(Ready() with { Status = "failed", Package = failed }, null, CancellationToken.None);
        var failedWire = service.Saved.ShouldNotBeNull().Project.Package;
        var none = await controlPlane.SaveProjectAsync(Ready(), null, CancellationToken.None);

        (wire.Status, wire.VideoFile, wire.ThumbnailFile, wire.RightsConfirmed, wire.ApprovedAt, wire.Title, wire.Description, wire.DurationMs, wire.HasError, wire.MetadataEdited)
            .ShouldBe(("assembled", "video.mp4", "thumbnail.png", true, "2026-11-03T13:00:00Z", "Bread in ten minutes", "How to bake.\n\n0:00 Starter", 95_000, false, true));
        wire.Shorts.ShouldHaveSingleItem().ShouldBe(new ShortClip { File = "short-1.mp4", StartMs = 2000, DurationMs = 30_000, Hook = "Bread in ten minutes?" });
        wire.Checks.ShouldHaveSingleItem().ShouldBe(new PackageCheck { Name = "loudness", Result = "warn", Detail = "The video plays at -20.0 LUFS; aim between -18 and -11 LUFS." });
        wire.Tags.ShouldBe(["bread", "sourdough"]);
        (failedWire.HasVideoFile, failedWire.HasThumbnailFile, failedWire.HasApprovedAt, failedWire.Error).ShouldBe((false, false, false, "Scout could not render scene 2"));
        var back = saved.Package.ShouldNotBeNull();
        (back with { Shorts = [], Checks = [], Tags = [] }).ShouldBe(assembled with { Shorts = [], Checks = [], Tags = [] });
        (back.Shorts.ShouldHaveSingleItem(), back.Checks.ShouldHaveSingleItem()).ShouldBe((assembled.Shorts[0], assembled.Checks[0]));
        back.Tags.ShouldBe(["bread", "sourdough"]);
        (savedFailed.Package! with { Shorts = [], Checks = [], Tags = [] }).ShouldBe(failed with { Shorts = [], Checks = [], Tags = [] });
        none.Package.ShouldBeNull();
    }

    [Fact]
    public async Task A_Publication_Travels_Field_By_Field_With_The_Owners_Choices_As_Read_And_Absent_Values_Stay_Absent_On_The_Wire()
    {
        var snapshot = new OperatorSnapshot("24h", "2026-11-07T09:00:00Z", 1234, 2500, 121, 43.7, 12, 1, 40, 9, 5, 20_000, 0.052,
            [new OperatorRetentionPoint(0.5, 0.42)]);
        var arm = new OperatorExperimentArm("b", "Shorter title", "Bread in ten minutes", "2026-11-08T09:00:00Z", null, 1500, 80, 41.5);
        var publication = new OperatorPublication(
            "scheduled", "newVideo001", "private", "2026-11-10T17:00:00Z", false, true, "2026-11-03T14:00:00Z", null,
            [snapshot],
            [new OperatorProposal("24h-1", "thumbnail", "A loaf on a dark table", "Higher contrast", "proposed", "thumbnail-proposal-24h-1.png")],
            new OperatorExperiment("e1", "title", "running", [arm], null, null));
        var bare = new OperatorPublication("publish_approved", null, "unlisted", null, true, false, null, null, [], [], null);
        var service = new FakeWorkerGate();
        await using var server = await WorkerGateServer.StartAsync(service);
        using var gate = new GrpcWriteGate(server.SocketPath, Token);
        var controlPlane = new GrpcOperatorControlPlane(gate);

        var saved = await controlPlane.SaveProjectAsync(Ready() with { Publication = publication }, null, CancellationToken.None);
        var wire = service.Saved.ShouldNotBeNull().Project.Publication;
        var savedBare = await controlPlane.SaveProjectAsync(Ready() with { Publication = bare }, null, CancellationToken.None);
        var bareWire = service.Saved.ShouldNotBeNull().Project.Publication;
        var none = await controlPlane.SaveProjectAsync(Ready(), null, CancellationToken.None);

        (wire.Status, wire.VideoId, wire.PrivacyStatus, wire.PublishAt, wire.MadeForKids, wire.SyntheticMedia, wire.UploadedAt, wire.HasError)
            .ShouldBe(("scheduled", "newVideo001", "private", "2026-11-10T17:00:00Z", false, true, "2026-11-03T14:00:00Z", false));
        var wireSnapshot = wire.Snapshots.ShouldHaveSingleItem();
        (wireSnapshot.Window, wireSnapshot.Views, wireSnapshot.Impressions, wireSnapshot.ImpressionsCtr, wireSnapshot.Retention.ShouldHaveSingleItem().AudienceWatchRatio)
            .ShouldBe(("24h", 1234L, 20_000L, 0.052, 0.42));
        wire.Proposals.ShouldHaveSingleItem().ThumbnailFile.ShouldBe("thumbnail-proposal-24h-1.png");
        var wireArm = wire.Experiment.Arms.ShouldHaveSingleItem();
        (wireArm.StartedAt, wireArm.HasEndedAt, wireArm.Clicks, wire.Experiment.HasWinnerArmId, wire.Experiment.HasResult)
            .ShouldBe(("2026-11-08T09:00:00Z", false, 80L, false, false));
        (bareWire.HasVideoId, bareWire.HasPublishAt, bareWire.HasUploadedAt, bareWire.HasError, bareWire.Experiment, bareWire.MadeForKids)
            .ShouldBe((false, false, false, false, (Experiment?)null, true));
        var back = saved.Publication.ShouldNotBeNull();
        (back with { Snapshots = [], Proposals = [], Experiment = null }).ShouldBe(publication with { Snapshots = [], Proposals = [], Experiment = null });
        (back.Snapshots.ShouldHaveSingleItem() with { Retention = [] }).ShouldBe(snapshot with { Retention = [] });
        back.Snapshots[0].Retention.ShouldBe(snapshot.Retention);
        back.Proposals.ShouldBe(publication.Proposals);
        (back.Experiment.ShouldNotBeNull() with { Arms = [] }).ShouldBe(publication.Experiment! with { Arms = [] });
        back.Experiment.Arms.ShouldBe([arm]);
        (savedBare.Publication.ShouldNotBeNull() with { Snapshots = [], Proposals = [] }).ShouldBe(bare with { Snapshots = [], Proposals = [] });
        none.Publication.ShouldBeNull();
    }

    [Fact]
    public async Task The_Next_Approved_Action_Arrives_Whole_And_An_Empty_Reply_Means_None()
    {
        var pending = new PendingAction
        {
            Id = "a1",
            FeatureId = "youtube",
            Action = "videos.insert",
            Target = "p1",
            Summary = "Upload sourdough as private",
            Status = "approved",
        };
        var service = new FakeWorkerGate { ActionReply = new NextApprovedActionResponse { Action = pending, OriginReference = "discord:3333:42" } };
        await using var server = await WorkerGateServer.StartAsync(service);
        await using var empty = await WorkerGateServer.StartAsync(new FakeWorkerGate());
        using var gate = new GrpcWriteGate(server.SocketPath, Token);
        using var emptyGate = new GrpcWriteGate(empty.SocketPath, Token);

        var action = await new GrpcOperatorControlPlane(gate).NextApprovedActionAsync(CancellationToken.None);
        var none = await new GrpcOperatorControlPlane(emptyGate).NextApprovedActionAsync(CancellationToken.None);

        action.ShouldBe(new OperatorAction("a1", "youtube", "videos.insert", "p1", "Upload sourdough as private", "discord:3333:42"));
        service.Authorizations.ShouldBe(["Bearer worker-session-token"]);
        none.ShouldBeNull();
    }

    [Fact]
    public async Task A_Run_Action_Is_Recorded_With_Its_Id_Scope_Target_And_Outcome_And_A_Refused_Record_Keeps_The_Control_Planes_Words()
    {
        var action = new OperatorAction("a1", "youtube", "videos.update", "p1:e1:b", "Set title");
        var service = new FakeWorkerGate();
        await using var server = await WorkerGateServer.StartAsync(service);
        await using var refusing = await WorkerGateServer.StartAsync(new FakeWorkerGate
        {
            OperatorFailure = new RpcException(new Status(StatusCode.FailedPrecondition, "The owner has not allowed youtube to write.")),
        });
        using var gate = new GrpcWriteGate(server.SocketPath, Token);
        using var refusingGate = new GrpcWriteGate(refusing.SocketPath, Token);

        await new GrpcOperatorControlPlane(gate).RecordActionAsync(action, "succeeded", CancellationToken.None);
        var refused = await Should.ThrowAsync<InvalidOperationException>(
            () => new GrpcOperatorControlPlane(refusingGate).RecordActionAsync(action, "succeeded", CancellationToken.None));

        var record = service.Record.ShouldNotBeNull();
        (record.FeatureId, record.Scope, record.Target, record.Outcome, record.ActionId)
            .ShouldBe(("youtube", "videos.update", "p1:e1:b", "succeeded", "a1"));
        service.Authorizations.ShouldBe(["Bearer worker-session-token"]);
        refused.ShouldNotBeOfType<ControlPlaneUnavailableException>();
        refused.Message.ShouldBe("The owner has not allowed youtube to write.");
    }

    [Theory]
    [InlineData("Action 'a1' is already recorded in the write log.", null)]
    [InlineData("The owner revoked youtube's write permission before this action ran.", typeof(WriteRevokedException))]
    [InlineData("Action 'a1' is not approved by the owner.", typeof(InvalidOperationException))]
    public async Task A_Record_Already_Logged_Is_A_Success_And_A_Record_Refused_After_A_Revoke_Is_Told_Apart(string detail, Type? expected)
    {
        await using var server = await WorkerGateServer.StartAsync(
            new FakeWorkerGate { OperatorFailure = new RpcException(new Status(StatusCode.FailedPrecondition, detail)) });
        using var gate = new GrpcWriteGate(server.SocketPath, Token);
        var action = new OperatorAction("a1", "youtube", "videos.insert", "p1", "Upload");

        var thrown = await Record.ExceptionAsync(() => new GrpcOperatorControlPlane(gate).RecordActionAsync(action, "succeeded", CancellationToken.None));

        thrown?.GetType().ShouldBe(expected);
    }

    [Fact]
    public async Task The_Refusal_Phrases_Match_The_Words_Of_The_Control_Planes_Write_Gate()
    {
        var directory = Directory.CreateTempSubdirectory("scout-record-phrases-").FullName;
        try
        {
            var options = Microsoft.Extensions.Options.Options.Create(new Scout.ControlPlane.Features.FeatureStateStoreOptions { FilePath = Path.Combine(directory, "features.json") });
            var states = new Scout.ControlPlane.Features.JsonFileFeatureStateStore(options, Microsoft.Extensions.Logging.Abstractions.NullLogger<Scout.ControlPlane.Features.JsonFileFeatureStateStore>.Instance);
            var actions = new Scout.ControlPlane.Actions.JsonFileActionQueue(options, Microsoft.Extensions.Logging.Abstractions.NullLogger<Scout.ControlPlane.Actions.JsonFileActionQueue>.Instance);
            var writes = new Scout.ControlPlane.Actions.JsonLinesWriteLog(options, Microsoft.Extensions.Logging.Abstractions.NullLogger<Scout.ControlPlane.Actions.JsonLinesWriteLog>.Instance);
            var gate = new Scout.ControlPlane.WriteGate.WriteGateService(new Scout.ControlPlane.Features.FeatureCatalog(), states, actions, writes);
            var granted = new Scout.ControlPlane.Features.FeatureState(true, null, new Dictionary<string, string?>(), Granted: true);
            await states.SetAsync("youtube", granted, CancellationToken.None);
            var queued = await actions.EnqueueApprovedAsync("youtube", [("videos.insert", "p1", "Upload"), ("videos.insert", "p2", "Upload")], CancellationToken.None);
            var first = (await actions.ClaimNextAsync("youtube", TimeSpan.FromMinutes(30), (a, _) => a.Id == queued[0].Id, CancellationToken.None))!;
            await writes.AppendAsync(
                new Scout.ControlPlane.Actions.WriteLogEntry(DateTimeOffset.UtcNow, "youtube", "videos.insert", "p1", "succeeded", "owner", first.Id),
                CancellationToken.None);

            var again = await Should.ThrowAsync<Scout.ControlPlane.WriteGate.WriteNotRecordableException>(
                () => gate.RecordAsync("youtube", "videos.insert", "p1", "succeeded", first.Id, CancellationToken.None));
            var second = (await actions.ClaimNextAsync("youtube", TimeSpan.FromMinutes(30), (a, _) => a.Id == queued[1].Id, CancellationToken.None))!;
            await states.SetAsync("youtube", granted with { Granted = false }, CancellationToken.None);
            var revoked = await Should.ThrowAsync<Scout.ControlPlane.WriteGate.WriteNotRecordableException>(
                () => gate.RecordAsync("youtube", "videos.insert", "p2", "succeeded", second.Id, CancellationToken.None));

            again.Message.ShouldContain(GrpcOperatorControlPlane.AlreadyRecordedPhrase);
            revoked.Message.ShouldContain(GrpcOperatorControlPlane.RevokedPhrase);
            again.Message.ShouldNotContain(GrpcOperatorControlPlane.RevokedPhrase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task An_Unreachable_Control_Plane_Is_Told_Apart_From_A_Refusal_It_Words()
    {
        await using var refusing = await WorkerGateServer.StartAsync(
            new FakeWorkerGate { OperatorFailure = new RpcException(new Status(StatusCode.NotFound, "No video project has the id p9.")) });
        using var refusingGate = new GrpcWriteGate(refusing.SocketPath, Token);
        using var missing = new GrpcWriteGate(Path.Combine(Path.GetTempPath(), $"scout-missing-{Guid.NewGuid():N}.sock"), Token);

        var refused = await Should.ThrowAsync<InvalidOperationException>(
            () => new GrpcOperatorControlPlane(refusingGate).GetProjectAsync("p9", CancellationToken.None));
        var unreachable = await Should.ThrowAsync<ControlPlaneUnavailableException>(
            () => new GrpcOperatorControlPlane(missing).NextApprovedActionAsync(CancellationToken.None));

        refused.ShouldNotBeOfType<ControlPlaneUnavailableException>();
        refused.Message.ShouldBe("No video project has the id p9.");
        unreachable.Message.ShouldBe(GrpcOperatorControlPlane.Unreachable);
    }

    [Theory]
    [InlineData("youtube-key", null, false)]
    [InlineData(null, "/nonexistent/youtube-token.json", true)]
    public void Publishing_Is_Composed_Only_When_The_Owner_Signed_In_And_Files_Uploads_Under_The_Configured_Category(
        string? youtubeKey, string? tokenFile, bool publishes)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Plugins:YouTube:ApiKey"] = youtubeKey,
                ["Scout:Plugins:YouTube:TokenFile"] = tokenFile,
                ["Scout:Operator:CategoryId"] = "27",
            })
            .Build();
        var services = new ServiceCollection().AddLogging().AddScoutHost(configuration);
        using var provider = services.BuildServiceProvider();

        services.Any(d => d.ImplementationType == typeof(ActionExecutor)).ShouldBe(publishes);
        services.Any(d => d.ImplementationType == typeof(OperatorScheduler)).ShouldBe(publishes);
        provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<OperatorMediaOptions>>().Value.CategoryId.ShouldBe("27");
    }

    [Fact]
    public void Uploads_Are_Filed_Under_People_And_Blogs_Unless_Configured()
        => new OperatorMediaOptions().CategoryId.ShouldBe("22");

    [Fact]
    public async Task The_Ffmpeg_And_Font_Paths_Bind_From_Scout_Operator_And_Reach_The_Toolset_That_Probes_Them()
    {
        var font = typeof(GrpcOperatorControlPlaneTests).Assembly.Location;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Plugins:YouTube:ApiKey"] = "youtube-key",
                ["Scout:Operator:FfmpegPath"] = "/opt/scout/bin/ffmpeg",
                ["Scout:Operator:FontPath"] = font,
            })
            .Build();
        var runner = new ListingRunner();
        var services = new ServiceCollection().AddLogging().AddScoutHost(configuration);
        services.AddSingleton<IProcessRunner>(runner);
        using var provider = services.BuildServiceProvider();

        var probe = await provider.GetRequiredService<FfmpegToolset>().ProbeAsync(CancellationToken.None);

        probe.Tools.ShouldBe(new FfmpegTools("/opt/scout/bin/ffmpeg", font, "libopenh264"));
        runner.Programs.ShouldAllBe(p => p == "/opt/scout/bin/ffmpeg");
        runner.Programs.Count.ShouldBe(4);
    }

    private sealed class ListingRunner : IProcessRunner
    {
        public List<string> Programs { get; } = [];

        public Task<ProcessResult> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
        {
            Programs.Add(program);
            var listing = arguments[^1] switch
            {
                "-filters" => string.Concat(FfmpegToolset.Filters.Select(f => $" ... {f}  V->V  A filter.\n")),
                "-demuxers" => " ---\n D  concat  Virtual concatenation script\n D d lavfi  Libavfilter virtual input device\n",
                _ => " ------\n V....D libopenh264  OpenH264\n A....D aac  AAC\n",
            };
            return Task.FromResult(new ProcessResult(0, listing, string.Empty));
        }
    }

    private sealed class ScriptComparer : IEqualityComparer<OperatorScript?>
    {
        public bool Equals(OperatorScript? x, OperatorScript? y)
            => x is not null && y is not null
                && x.Hook == y.Hook && x.CallToAction == y.CallToAction && x.Description == y.Description
                && x.Sections.SequenceEqual(y.Sections) && x.TitleVariants.SequenceEqual(y.TitleVariants)
                && x.Tags.SequenceEqual(y.Tags) && x.Chapters.SequenceEqual(y.Chapters);

        public int GetHashCode(OperatorScript? obj) => 0;
    }
}
