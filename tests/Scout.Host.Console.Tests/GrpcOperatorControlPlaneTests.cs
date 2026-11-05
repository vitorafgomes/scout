using System.Threading.Channels;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using NSubstitute;
using Scout.ControlPlane.Contracts;
using Scout.Conversation.Abstractions;
using Scout.Host.ControlPlane;
using Scout.Host.ControlPlane.Security;
using Scout.Operator;
using Scout.Plugin.YouTube.Abstractions;
using Shouldly;
using Xunit;
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
        var stock = new OperatorScene("s3", 3, "Slice it.", "A loaf", false, "ready", null, "none", "stock", null, "scene-s3.jpg", 0)
        {
            VisualQuery = "bread loaf knife",
            VisualCredit = "\"Loaf\" by Ana is licensed under CC BY 2.0.",
        };
        var service = new FakeWorkerGate();
        await using var server = await WorkerGateServer.StartAsync(service);
        using var gate = new GrpcWriteGate(server.SocketPath, Token);

        var saved = await new GrpcOperatorControlPlane(gate).SaveProjectAsync(
            Ready() with { Status = "producing", Scenes = [withMedia, failed, stock] }, null, CancellationToken.None);

        var wire = service.Saved.ShouldNotBeNull().Project.Scenes;
        wire.Select(w => w.Id).ShouldBe(["s1", "s2", "s3"]);
        wire.Select(w => (w.VisualQuery, w.HasVisualCredit, w.VisualCredit)).ShouldBe([
            (string.Empty, false, string.Empty),
            (string.Empty, false, string.Empty),
            ("bread loaf knife", true, "\"Loaf\" by Ana is licensed under CC BY 2.0."),
        ]);
        (wire[0].HasError, wire[0].HasAudioFile, wire[0].HasVisualFile).ShouldBe((false, true, false));
        (wire[1].Locked, wire[1].Error, wire[1].AudioSource, wire[1].VisualFile).ShouldBe(
            (true, "The AI Source could not make the picture of scene 2.", "owner", "mine.png"));
        saved.Scenes.ShouldBe([withMedia, failed, stock]);
    }

    [Fact]
    public async Task The_Owners_Background_Travels_To_The_Wire_And_Back_And_An_Absent_One_Stays_Absent()
    {
        var service = new FakeWorkerGate();
        await using var server = await WorkerGateServer.StartAsync(service);
        using var gate = new GrpcWriteGate(server.SocketPath, Token);
        var operatorPlane = new GrpcOperatorControlPlane(gate);

        var withBackground = await operatorPlane.SaveProjectAsync(Ready() with { BackgroundFile = "studio wall.jpg" }, null, CancellationToken.None);
        var sentWith = service.Saved.ShouldNotBeNull().Project;
        var without = await operatorPlane.SaveProjectAsync(Ready(), null, CancellationToken.None);
        var sentWithout = service.Saved.ShouldNotBeNull().Project;

        (sentWith.HasBackgroundFile, sentWith.BackgroundFile).ShouldBe((true, "studio wall.jpg"));
        withBackground.BackgroundFile.ShouldBe("studio wall.jpg");
        sentWithout.HasBackgroundFile.ShouldBeFalse();
        without.BackgroundFile.ShouldBeNull();
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
            new OperatorReadiness(true, false, "AzureOpenAI", false, true, ["AzureOpenAI has no image service in Scout."])
            {
                Voices = [new("AzureOpenAI:nova", "nova (AzureOpenAI)", "AzureOpenAI"), new("piper:en_GB-cori-medium", "Cori (medium)", "piper")],
                StockPictures = true,
            },
            CancellationToken.None);

        directory.ShouldBe("/data/scout/operator/media/p1");
        service.Got.ShouldNotBeNull().Id.ShouldBe("p1");
        var reported = service.Reported.ShouldNotBeNull();
        (reported.SpeechAvailable, reported.ImagesAvailable, reported.Provider, reported.FfmpegAvailable, reported.YoutubeSignedIn)
            .ShouldBe((true, false, "AzureOpenAI", false, true));
        reported.Notes.ShouldBe(["AzureOpenAI has no image service in Scout."]);
        reported.Voices.Select(v => (v.Id, v.Label, v.Source)).ShouldBe([
            ("AzureOpenAI:nova", "nova (AzureOpenAI)", "AzureOpenAI"), ("piper:en_GB-cori-medium", "Cori (medium)", "piper"),
        ]);
        reported.HasReportedAt.ShouldBeFalse();
        reported.StockPictures.ShouldBeTrue();
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
        var samplesRefused = await Should.ThrowAsync<InvalidOperationException>(
            () => new GrpcOperatorControlPlane(refusingGate).GetVoiceSamplesDirectoryAsync(CancellationToken.None));
        var samplesReset = await Should.ThrowAsync<ControlPlaneUnavailableException>(
            () => new GrpcOperatorControlPlane(brokenGate).GetVoiceSamplesDirectoryAsync(CancellationToken.None));

        refused.Message.ShouldBe("The owner has not granted YouTube writes.");
        reset.Message.ShouldBe("Scout could not reach its control plane.");
        missing.Message.ShouldBe("Scout could not reach its control plane.");
        samplesRefused.Message.ShouldBe("The owner has not granted YouTube writes.");
        samplesReset.Message.ShouldBe("Scout could not reach its control plane.");
    }

    [Fact]
    public async Task The_Voice_Samples_Directory_Is_Asked_For_With_The_Worker_Token_And_A_Sample_File_Is_Reported_With_Its_Voice()
    {
        var service = new FakeWorkerGate();
        await using var server = await WorkerGateServer.StartAsync(service);
        using var gate = new GrpcWriteGate(server.SocketPath, Token);
        var controlPlane = new GrpcOperatorControlPlane(gate);

        var directory = await controlPlane.GetVoiceSamplesDirectoryAsync(CancellationToken.None);
        await controlPlane.ReportReadinessAsync(
            new OperatorReadiness(true, false, "OpenAI", true, true, [])
            {
                Voices = [new("OpenAI:nova", "nova (OpenAI)", "OpenAI"), new("piper:en_GB-cori-medium", "Cori (medium)", "piper", "piper-en_GB-cori-medium.wav")],
            },
            CancellationToken.None);

        directory.ShouldBe("/data/scout/operator/voice-samples");
        service.Reported.ShouldNotBeNull().Voices.Select(v => (v.Id, v.HasSampleFile, v.SampleFile)).ShouldBe([
            ("OpenAI:nova", false, string.Empty),
            ("piper:en_GB-cori-medium", true, "piper-en_GB-cori-medium.wav"),
        ]);
        service.Authorizations.ShouldBe(["Bearer worker-session-token", "Bearer worker-session-token"]);
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
            await Should.ThrowAsync<InvalidOperationException>(() => closed.GetCommentsAsync(CancellationToken.None)),
            await Should.ThrowAsync<InvalidOperationException>(
                () => closed.SaveCommentsAsync(new OperatorComments([], [], null, null, null), CancellationToken.None)),
            await Should.ThrowAsync<InvalidOperationException>(() => closed.EditSceneTextAsync("p1", "s2", "Shape it.", CancellationToken.None)),
            await Should.ThrowAsync<InvalidOperationException>(() => closed.RegenerateNarrationAsync("p1", "s2", CancellationToken.None)),
            await Should.ThrowAsync<InvalidOperationException>(() => closed.RegenerateSceneAsync("p1", "s2", CancellationToken.None)),
            await Should.ThrowAsync<InvalidOperationException>(() => closed.SetSceneLockedAsync("p1", "s2", true, CancellationToken.None)),
            await Should.ThrowAsync<InvalidOperationException>(() => closed.ReorderScenesAsync("p1", ["s2", "s1"], CancellationToken.None)),
            await Should.ThrowAsync<InvalidOperationException>(() => closed.ProduceProjectAsync("p1", "", "", "", CancellationToken.None)),
            await Should.ThrowAsync<InvalidOperationException>(() => closed.DelegateProjectAsync("p1", "", "", "", CancellationToken.None)),
        };
        var mediaDirectory = await Should.ThrowAsync<InvalidOperationException>(() => closed.GetMediaDirectoryAsync("p1", CancellationToken.None));
        var readiness = await Should.ThrowAsync<InvalidOperationException>(
            () => closed.ReportReadinessAsync(new OperatorReadiness(false, false, "Gemini", false, false, []), CancellationToken.None));
        var samples = await Should.ThrowAsync<InvalidOperationException>(() => closed.GetVoiceSamplesDirectoryAsync(CancellationToken.None));

        failures.Select(f => f.Message).ShouldAllBe(m => m == "Scout Desktop's control plane is not available, so the video operator is off.");
        mediaDirectory.Message.ShouldBe("Scout Desktop's control plane is not available, so the video operator is off.");
        readiness.Message.ShouldBe("Scout Desktop's control plane is not available, so the video operator is off.");
        samples.Message.ShouldBe("Scout Desktop's control plane is not available, so the video operator is off.");
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

    [Fact]
    public async Task Comments_Travel_Field_By_Field_And_Go_Back_With_Every_Time_Byte_For_Byte_And_Absent_Values_Absent()
    {
        var view = new CommentsView
        {
            SyncedAt = "2026-11-03T08:00:00.5000000Z",
            SyncRequestedAt = "2026-11-03T12:00:00.1234567+00:00",
        };
        view.Comments.Add(new AudienceComment
        {
            Id = "UgComment001",
            ThreadId = "UgThread001",
            VideoId = "abcDEF12345",
            VideoTitle = "Sourdough basics",
            Author = "@breadfan",
            Text = "How long do I feed it?",
            PublishedAt = "2026-11-02T09:30:00+00:00",
            LikeCount = 3,
            Kind = "question",
            Theme = "feeding",
            Status = "reply_approved",
            Draft = "Daily.",
            ApprovedText = "Feed it daily for a week.",
            DecidedAt = "2026-11-03T11:59:59.9876543+00:00",
            FetchedAt = "2026-11-02T10:00:00.0000001+00:00",
        });
        view.Comments.Add(new AudienceComment
        {
            Id = "UgComment002",
            ThreadId = "UgThread002",
            VideoId = "abcDEF12345",
            Author = "Anonymous",
            Text = "Great tip",
            PublishedAt = "2026-11-01T18:00:00+00:00",
            Kind = "praise",
            Status = "new",
            FetchedAt = "2026-11-02T10:00:00+00:00",
        });
        view.Ideas.Add(new AudienceIdea { Id = "idea-UgComment003", Text = "Rye starters", CommentId = "UgComment003", Status = "used", ProjectId = "p7" });
        var service = new FakeWorkerGate { CommentsReply = view };
        await using var server = await WorkerGateServer.StartAsync(service);
        using var gate = new GrpcWriteGate(server.SocketPath, Token);
        var controlPlane = new GrpcOperatorControlPlane(gate);

        var read = await controlPlane.GetCommentsAsync(CancellationToken.None);
        var saved = await controlPlane.SaveCommentsAsync(read, CancellationToken.None);

        read.Comments[0].ShouldBe(new OperatorComment(
            "UgComment001", "UgThread001", "abcDEF12345", "Sourdough basics", "@breadfan", "How long do I feed it?", "2026-11-02T09:30:00+00:00", 3,
            "question", "feeding", "reply_approved", "Daily.", "Feed it daily for a week.", null, null, "2026-11-03T11:59:59.9876543+00:00",
            "2026-11-02T10:00:00.0000001+00:00"));
        (read.Comments[1].ApprovedText, read.Comments[1].ReplyId, read.Comments[1].Error, read.Comments[1].DecidedAt).ShouldBe((null, null, null, null));
        read.Ideas.ShouldBe([new OperatorIdea("idea-UgComment003", "Rye starters", "UgComment003", "used", "p7")]);
        (read.SyncedAt, read.SyncRequestedAt, read.SyncError).ShouldBe(("2026-11-03T08:00:00.5000000Z", "2026-11-03T12:00:00.1234567+00:00", null));
        service.SavedComments.ShouldBe(view);
        saved.ShouldBe(read, new OperatorCommentsComparer());
        service.Authorizations.ShouldBe(["Bearer worker-session-token", "Bearer worker-session-token"]);
    }

    [Fact]
    public async Task A_Comment_Read_Or_Save_Keeps_The_Control_Planes_Words_And_An_Unreachable_One_Reads_Unreachable()
    {
        await using var refusing = await WorkerGateServer.StartAsync(
            new FakeWorkerGate { OperatorFailure = new RpcException(new Status(StatusCode.FailedPrecondition, "The worker may not drop comment 'c1'.")) });
        using var refusingGate = new GrpcWriteGate(refusing.SocketPath, Token);
        using var missing = new GrpcWriteGate(Path.Combine(Path.GetTempPath(), $"scout-missing-{Guid.NewGuid():N}.sock"), Token);
        var empty = new OperatorComments([], [], null, null, null);

        var refused = await Should.ThrowAsync<InvalidOperationException>(
            () => new GrpcOperatorControlPlane(refusingGate).SaveCommentsAsync(empty, CancellationToken.None));
        var unreachable = await Should.ThrowAsync<ControlPlaneUnavailableException>(
            () => new GrpcOperatorControlPlane(missing).GetCommentsAsync(CancellationToken.None));

        refused.ShouldNotBeOfType<ControlPlaneUnavailableException>();
        refused.Message.ShouldBe("The worker may not drop comment 'c1'.");
        unreachable.Message.ShouldBe(GrpcOperatorControlPlane.Unreachable);
    }

    [Fact]
    public async Task A_Comments_View_Over_4_MB_Goes_Both_Ways_Between_The_Worker_And_The_Real_Control_Plane()
    {
        var directory = Directory.CreateTempSubdirectory("scout-large-comments-").FullName;
        var socket = Path.Combine(directory, "controlplane.sock");
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls([]);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenUnixSocket(socket, listen => listen.Protocols = HttpProtocols.Http2));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Features:StateStore:FilePath"] = Path.Combine(directory, "features.json"),
                ["Scout:LocalRuntime:Config:FilePath"] = Path.Combine(directory, "local-runtimes.json"),
            })
            .Build();
        builder.Services.AddControlPlaneHost(configuration, SessionToken.FromValue("desktop-session-token"), Token);
        var app = builder.Build();
        app.MapControlPlaneServices();
        await app.StartAsync();
        try
        {
            await app.Services.GetRequiredService<Scout.ControlPlane.Features.FeaturesService>()
                .SetWriteGrantAsync("youtube", granted: true, confirmWrites: true, CancellationToken.None);
            var text = new string('\u65e5', 2000);
            await app.Services.GetRequiredService<Scout.ControlPlane.Operator.JsonFileOperatorStore>().UpdateCommentsAsync(
                _ => new Scout.ControlPlane.Operator.CommentsState(
                    [.. Enumerable.Range(0, 1000).Select(i => new Scout.ControlPlane.Operator.AudienceComment(
                        $"c{i:0000}", $"t{i}", "v1", "Rivers", "Viewer", text, new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero).AddMinutes(-i), 0,
                        "praise", "thanks", "new", string.Empty, null, null, null, null, new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero)))],
                    [],
                    null,
                    null,
                    null),
                CancellationToken.None);
            using var gate = new GrpcWriteGate(socket, Token);
            var controlPlane = new GrpcOperatorControlPlane(gate);

            var read = new OperatorComments([], [], null, null, null);
            var saved = read;
            await Should.NotThrowAsync(async () => read = await controlPlane.GetCommentsAsync(CancellationToken.None));
            await Should.NotThrowAsync(async () => saved = await controlPlane.SaveCommentsAsync(read, CancellationToken.None));

            System.Text.Encoding.UTF8.GetByteCount(string.Concat(read.Comments.Select(c => c.Text))).ShouldBeGreaterThan(4 * 1024 * 1024);
            (read.Comments.Count, saved.Comments.Count).ShouldBe((1000, 1000));
            saved.Comments.ShouldBe(read.Comments);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class OperatorCommentsComparer : IEqualityComparer<OperatorComments>
    {
        public bool Equals(OperatorComments? x, OperatorComments? y)
            => x is not null && y is not null && x.Comments.SequenceEqual(y.Comments) && x.Ideas.SequenceEqual(y.Ideas)
                && (x.SyncedAt, x.SyncRequestedAt, x.SyncError) == (y.SyncedAt, y.SyncRequestedAt, y.SyncError);

        public int GetHashCode(OperatorComments obj) => obj.Comments.Count;
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

    [Fact]
    public async Task The_Rhubarb_And_Mouths_Paths_Bind_From_Scout_Operator_And_Reach_The_Readiness_Of_Lip_Sync()
    {
        var mouths = Directory.CreateTempSubdirectory("scout-mouths-").FullName;
        try
        {
            foreach (var shape in new[] { "A", "B", "C", "D", "E", "F", "G", "H", "X" })
            {
                File.WriteAllBytes(Path.Combine(mouths, $"{shape}.png"), []);
            }

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Scout:Plugins:YouTube:ApiKey"] = "youtube-key",
                    ["Scout:Operator:RhubarbPath"] = "/opt/scout/rhubarb/rhubarb",
                    ["Scout:Operator:MouthsPath"] = mouths,
                })
                .Build();
            var runner = new ListingRunner();
            var services = new ServiceCollection().AddLogging().AddScoutHost(configuration);
            services.AddSingleton<IProcessRunner>(runner);
            using var provider = services.BuildServiceProvider();

            var readiness = await provider.GetRequiredService<ReadinessReporter>().CurrentAsync(CancellationToken.None);

            readiness.LipSyncAvailable.ShouldBeTrue();
            readiness.Notes.ShouldNotContain(n => n.StartsWith("Lip sync", StringComparison.Ordinal));
            runner.Programs.ShouldContain("/opt/scout/rhubarb/rhubarb");
        }
        finally
        {
            Directory.Delete(mouths, recursive: true);
        }
    }

    private sealed class ListingRunner : IProcessRunner
    {
        public List<string> Programs { get; } = [];

        public Task<ProcessResult> RunAsync(
            string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct, string? input = null, byte[]? inputBytes = null)
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

    [Fact]
    public async Task Delegated_is_read_and_sent_back_to_the_control_plane_as_read()
    {
        var list = new ListProjectsResponse();
        list.Projects.Add(new Project { Id = "p1", Topic = "sourdough", Status = "producing", Origin = "desktop", Delegated = true });
        list.Projects.Add(new Project { Id = "p2", Topic = "rye", Status = "producing", Origin = "desktop" });
        var service = new FakeWorkerGate { ListReply = list };
        await using var server = await WorkerGateServer.StartAsync(service);
        using var gate = new GrpcWriteGate(server.SocketPath, Token);
        var controlPlane = new GrpcOperatorControlPlane(gate);

        var delegated = await controlPlane.GetProjectAsync("p1", CancellationToken.None);
        await controlPlane.SaveProjectAsync(delegated with { Status = "media_ready" }, null, CancellationToken.None);
        var sentDelegated = service.Saved.ShouldNotBeNull().Project.Delegated;
        var manual = await controlPlane.GetProjectAsync("p2", CancellationToken.None);
        await controlPlane.SaveProjectAsync(manual with { Status = "media_ready" }, null, CancellationToken.None);

        (delegated.Delegated, sentDelegated).ShouldBe((true, true));
        (manual.Delegated, service.Saved.Project.Delegated).ShouldBe((false, false));
    }

    [Fact]
    public async Task The_Projects_Narration_Voice_Is_Read_And_Sent_Back_To_The_Control_Plane_As_Read()
    {
        var list = new ListProjectsResponse();
        list.Projects.Add(new Project { Id = "p1", Topic = "sourdough", Status = "producing", Origin = "desktop", NarrationVoice = "piper:en_GB-aru-medium#0" });
        var service = new FakeWorkerGate { ListReply = list };
        await using var server = await WorkerGateServer.StartAsync(service);
        using var gate = new GrpcWriteGate(server.SocketPath, Token);
        var controlPlane = new GrpcOperatorControlPlane(gate);

        var read = await controlPlane.GetProjectAsync("p1", CancellationToken.None);
        await controlPlane.SaveProjectAsync(read with { Status = "media_ready" }, null, CancellationToken.None);

        read.NarrationVoice.ShouldBe("piper:en_GB-aru-medium#0");
        service.Saved.ShouldNotBeNull().Project.NarrationVoice.ShouldBe("piper:en_GB-aru-medium#0");
    }

    [Fact]
    public async Task The_Projects_Format_Direction_And_Motion_Are_Read_And_Sent_Back_As_Read()
    {
        var list = new ListProjectsResponse();
        list.Projects.Add(new Project
        {
            Id = "p1",
            Topic = "sourdough",
            Status = "producing",
            Origin = "desktop",
            Direction = "Watercolour, it's: calm.\nSoft light.",
            Motion = "dynamic",
            Format = "own_pictures",
        });
        var service = new FakeWorkerGate { ListReply = list };
        await using var server = await WorkerGateServer.StartAsync(service);
        using var gate = new GrpcWriteGate(server.SocketPath, Token);
        var controlPlane = new GrpcOperatorControlPlane(gate);

        var read = await controlPlane.GetProjectAsync("p1", CancellationToken.None);
        await controlPlane.SaveProjectAsync(read with { Status = "media_ready" }, null, CancellationToken.None);

        (read.Format, read.Direction, read.Motion).ShouldBe(("own_pictures", "Watercolour, it's: calm.\nSoft light.", "dynamic"));
        var sent = service.Saved.ShouldNotBeNull().Project;
        (sent.Format, sent.Direction, sent.Motion).ShouldBe(("own_pictures", "Watercolour, it's: calm.\nSoft light.", "dynamic"));
    }

    private static Task<OperatorProjectState> ChatEdit(GrpcOperatorControlPlane controlPlane, string rpc) => rpc switch
    {
        nameof(WorkerGate.WorkerGateBase.ChatEditSceneText) => controlPlane.EditSceneTextAsync("p1", "s2", "Shape the loaf gently.", CancellationToken.None),
        nameof(WorkerGate.WorkerGateBase.ChatRegenerateNarration) => controlPlane.RegenerateNarrationAsync("p1", "s2", CancellationToken.None),
        nameof(WorkerGate.WorkerGateBase.ChatRegenerateScene) => controlPlane.RegenerateSceneAsync("p1", "s2", CancellationToken.None),
        nameof(WorkerGate.WorkerGateBase.ChatSetSceneLocked) => controlPlane.SetSceneLockedAsync("p1", "s2", true, CancellationToken.None),
        nameof(WorkerGate.WorkerGateBase.ChatReorderScenes) => controlPlane.ReorderScenesAsync("p1", ["s2", "s1", "s3"], CancellationToken.None),
        nameof(WorkerGate.WorkerGateBase.ChatProduceProject) => controlPlane.ProduceProjectAsync("p1", "piper:en_GB-aru-medium", string.Empty, "still", CancellationToken.None),
        nameof(WorkerGate.WorkerGateBase.ChatDelegateProject) => controlPlane.DelegateProjectAsync("p1", string.Empty, "Calm, close-up shots.", string.Empty, CancellationToken.None),
        _ => throw new ArgumentOutOfRangeException(nameof(rpc)),
    };

    [Theory]
    [InlineData(nameof(WorkerGate.WorkerGateBase.ChatEditSceneText), "{ \"projectId\": \"p1\", \"sceneId\": \"s2\", \"text\": \"Shape the loaf gently.\" }")]
    [InlineData(nameof(WorkerGate.WorkerGateBase.ChatRegenerateNarration), "{ \"projectId\": \"p1\", \"sceneId\": \"s2\" }")]
    [InlineData(nameof(WorkerGate.WorkerGateBase.ChatRegenerateScene), "{ \"projectId\": \"p1\", \"sceneId\": \"s2\" }")]
    [InlineData(nameof(WorkerGate.WorkerGateBase.ChatSetSceneLocked), "{ \"projectId\": \"p1\", \"sceneId\": \"s2\", \"locked\": true }")]
    [InlineData(nameof(WorkerGate.WorkerGateBase.ChatReorderScenes), "{ \"projectId\": \"p1\", \"sceneIds\": [ \"s2\", \"s1\", \"s3\" ] }")]
    [InlineData(nameof(WorkerGate.WorkerGateBase.ChatProduceProject), "{ \"id\": \"p1\", \"narrationVoice\": \"piper:en_GB-aru-medium\", \"motion\": \"still\" }")]
    [InlineData(nameof(WorkerGate.WorkerGateBase.ChatDelegateProject), "{ \"id\": \"p1\", \"direction\": \"Calm, close-up shots.\" }")]
    public async Task A_Chat_Edit_Travels_On_Its_Own_Rpc_With_The_Worker_Token_And_Reads_The_Project_Back(string rpc, string request)
    {
        var service = new FakeWorkerGate();
        await using var server = await WorkerGateServer.StartAsync(service);
        using var gate = new GrpcWriteGate(server.SocketPath, Token);

        var project = await ChatEdit(new GrpcOperatorControlPlane(gate), rpc);

        var (sentOn, sent) = service.ChatRequests.ShouldHaveSingleItem();
        (sentOn, sent.ToString()).ShouldBe((rpc, request));
        service.Authorizations.ShouldBe(["Bearer worker-session-token"]);
        (project.Id, project.Topic, project.Status, project.Format).ShouldBe(("p1", "sourdough", "production_queued", "own_pictures"));
    }

    [Theory]
    [InlineData(nameof(WorkerGate.WorkerGateBase.ChatEditSceneText))]
    [InlineData(nameof(WorkerGate.WorkerGateBase.ChatRegenerateNarration))]
    [InlineData(nameof(WorkerGate.WorkerGateBase.ChatRegenerateScene))]
    [InlineData(nameof(WorkerGate.WorkerGateBase.ChatSetSceneLocked))]
    [InlineData(nameof(WorkerGate.WorkerGateBase.ChatReorderScenes))]
    [InlineData(nameof(WorkerGate.WorkerGateBase.ChatProduceProject))]
    [InlineData(nameof(WorkerGate.WorkerGateBase.ChatDelegateProject))]
    public async Task A_Chat_Edit_That_Fails_Reads_As_The_Control_Planes_Words_Or_Unreachable(string rpc)
    {
        async Task<InvalidOperationException> FailingWith(StatusCode code, string detail)
        {
            await using var server = await WorkerGateServer.StartAsync(new FakeWorkerGate { OperatorFailure = new RpcException(new Status(code, detail)) });
            using var gate = new GrpcWriteGate(server.SocketPath, Token);
            return await Should.ThrowAsync<InvalidOperationException>(() => ChatEdit(new GrpcOperatorControlPlane(gate), rpc));
        }

        var unavailable = await FailingWith(StatusCode.Unavailable, "the control plane is restarting");
        var reset = await FailingWith(StatusCode.Internal, "Connection reset by peer");
        var notFound = await FailingWith(StatusCode.NotFound, "Unknown project 'p1'.");
        var unimplemented = await FailingWith(StatusCode.Unimplemented, "Method is unimplemented.");
        using var missingGate = new GrpcWriteGate(Path.Combine(Path.GetTempPath(), $"scout-no-gate-{Guid.NewGuid():N}.sock"), Token);
        var missing = await Should.ThrowAsync<InvalidOperationException>(() => ChatEdit(new GrpcOperatorControlPlane(missingGate), rpc));

        notFound.Message.ShouldBe("Unknown project 'p1'.");
        notFound.ShouldNotBeOfType<ControlPlaneUnavailableException>();
        new[] { unavailable, reset, unimplemented, missing }.ShouldAllBe(
            ex => ex is ControlPlaneUnavailableException && ex.Message == "Scout could not reach its control plane.");
    }

    [Fact]
    public async Task The_Workers_Transcript_Times_And_Clips_Reach_The_Control_Plane_And_Come_Back()
    {
        var clip = new OperatorScene("s2", 2, "First the starter.", string.Empty, false, "ready", null, "recording", "recording", null, null, 52_200)
        {
            ClipStartMs = 23_000,
            ClipEndMs = 75_200,
        };
        var project = Ready() with
        {
            Status = "producing",
            Format = "own_video",
            RecordingFile = "owner-recording-1a.mp4",
            RecordingMs = 110_000,
            Script = new OperatorScript(
                string.Empty,
                [new OperatorScriptSection("Getting started", "Today we bake. First the starter.") { StartMs = 0, EndMs = 75_200 }],
                string.Empty,
                [],
                string.Empty,
                [],
                []),
            Scenes = [clip],
        };
        var service = new FakeWorkerGate();
        await using var server = await WorkerGateServer.StartAsync(service);
        using var gate = new GrpcWriteGate(server.SocketPath, Token);
        var operatorPlane = new GrpcOperatorControlPlane(gate);

        var saved = await operatorPlane.SaveProjectAsync(project, null, CancellationToken.None);
        var sentWith = service.Saved.ShouldNotBeNull().Project;
        var without = await operatorPlane.SaveProjectAsync(Ready(), null, CancellationToken.None);
        var sentWithout = service.Saved.ShouldNotBeNull().Project;

        (sentWith.HasRecordingFile, sentWith.RecordingFile, sentWith.RecordingMs).ShouldBe((true, "owner-recording-1a.mp4", 110_000));
        sentWith.Script.Sections.Select(s => (s.Heading, s.StartMs, s.EndMs)).ShouldBe([("Getting started", 0, 75_200)]);
        sentWith.Scenes.Select(s => (s.ClipStartMs, s.ClipEndMs, s.AudioSource, s.VisualSource)).ShouldBe([(23_000, 75_200, "recording", "recording")]);
        (saved.RecordingFile, saved.RecordingMs).ShouldBe(("owner-recording-1a.mp4", 110_000));
        saved.Script.ShouldBe(project.Script, new ScriptComparer());
        saved.Scenes.ShouldBe([clip]);
        (sentWithout.HasRecordingFile, sentWithout.RecordingMs).ShouldBe((false, 0));
        (without.RecordingFile, without.RecordingMs).ShouldBe(((string?)null, 0));
    }

    [Fact]
    public async Task Whether_This_Machine_Can_Transcribe_Is_Reported_To_The_Control_Plane()
    {
        var service = new FakeWorkerGate();
        await using var server = await WorkerGateServer.StartAsync(service);
        using var gate = new GrpcWriteGate(server.SocketPath, Token);
        var controlPlane = new GrpcOperatorControlPlane(gate);

        await controlPlane.ReportReadinessAsync(new OperatorReadiness(true, true, "OpenAI", true, true, []) { TranscriptionAvailable = true }, CancellationToken.None);
        var can = service.Reported.ShouldNotBeNull().TranscriptionAvailable;
        await controlPlane.ReportReadinessAsync(new OperatorReadiness(true, true, "OpenAI", true, true, []), CancellationToken.None);
        var cannot = service.Reported.ShouldNotBeNull().TranscriptionAvailable;

        (can, cannot).ShouldBe((true, false));
    }

    [Fact]
    public async Task The_Presenter_Its_Picture_And_Its_Mouth_Box_Travel_To_The_Wire_And_Back_And_Absent_Ones_Stay_Absent()
    {
        var service = new FakeWorkerGate();
        await using var server = await WorkerGateServer.StartAsync(service);
        using var gate = new GrpcWriteGate(server.SocketPath, Token);
        var operatorPlane = new GrpcOperatorControlPlane(gate);
        var project = Ready() with
        {
            Format = "animated_character",
            Presenter = "A cheerful baker in a flour-dusted apron, in a small kitchen.",
            PresenterFile = "presenter-0a1b2c3d4e5f.png",
            MouthBox = new OperatorMouthBox(0.41, 0.62, 0.18, 0.07),
            Scenes = [new OperatorScene("s1", 1, "Feed it daily.", string.Empty, false, "ready", null, "generated", "presenter", "scene-s1.wav", null, 1600)],
        };

        var saved = await operatorPlane.SaveProjectAsync(project, null, CancellationToken.None);
        var sentWith = service.Saved.ShouldNotBeNull().Project;
        var without = await operatorPlane.SaveProjectAsync(Ready(), null, CancellationToken.None);
        var sentWithout = service.Saved.ShouldNotBeNull().Project;

        (sentWith.Format, sentWith.Presenter, sentWith.HasPresenterFile, sentWith.PresenterFile).ShouldBe(
            ("animated_character", "A cheerful baker in a flour-dusted apron, in a small kitchen.", true, "presenter-0a1b2c3d4e5f.png"));
        (sentWith.MouthBox.X, sentWith.MouthBox.Y, sentWith.MouthBox.Width, sentWith.MouthBox.Height).ShouldBe((0.41, 0.62, 0.18, 0.07));
        sentWith.Scenes.ShouldHaveSingleItem().VisualSource.ShouldBe("presenter");
        (saved.Format, saved.Presenter, saved.PresenterFile, saved.MouthBox).ShouldBe(
            ("animated_character", project.Presenter, "presenter-0a1b2c3d4e5f.png", new OperatorMouthBox(0.41, 0.62, 0.18, 0.07)));
        saved.Scenes.ShouldBe(project.Scenes);
        (sentWithout.Presenter, sentWithout.HasPresenterFile, sentWithout.MouthBox).ShouldBe((string.Empty, false, (MouthBox?)null));
        (without.Presenter, without.PresenterFile, without.MouthBox).ShouldBe((string.Empty, (string?)null, (OperatorMouthBox?)null));
    }

    [Fact]
    public async Task Whether_This_Machine_Can_Move_A_Characters_Mouth_Is_Reported_To_The_Control_Plane()
    {
        var service = new FakeWorkerGate();
        await using var server = await WorkerGateServer.StartAsync(service);
        using var gate = new GrpcWriteGate(server.SocketPath, Token);
        var controlPlane = new GrpcOperatorControlPlane(gate);

        await controlPlane.ReportReadinessAsync(new OperatorReadiness(true, true, "OpenAI", true, true, []) { LipSyncAvailable = true }, CancellationToken.None);
        var can = service.Reported.ShouldNotBeNull().LipSyncAvailable;
        await controlPlane.ReportReadinessAsync(new OperatorReadiness(true, true, "OpenAI", true, true, []), CancellationToken.None);
        var cannot = service.Reported.ShouldNotBeNull().LipSyncAvailable;

        (can, cannot).ShouldBe((true, false));
    }
}
