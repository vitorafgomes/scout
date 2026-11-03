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
