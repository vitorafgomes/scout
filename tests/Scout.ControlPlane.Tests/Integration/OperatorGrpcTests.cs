using System.Text;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Scout.ControlPlane.Contracts;
using Shouldly;
using Xunit;
using GrpcFeatures = Scout.ControlPlane.Contracts.Features;
using GrpcOperator = Scout.ControlPlane.Contracts.Operator;
using JsonFileOperatorStore = Scout.ControlPlane.Operator.JsonFileOperatorStore;
using OperatorService = Scout.ControlPlane.Operator.OperatorService;
using WireClaim = Scout.ControlPlane.Contracts.Claim;
using WireMandate = Scout.ControlPlane.Contracts.Mandate;
using WireReadiness = Scout.ControlPlane.Contracts.Readiness;
using WireScene = Scout.ControlPlane.Contracts.Scene;
using WireScript = Scout.ControlPlane.Contracts.Script;

namespace Scout.ControlPlane.Tests.Integration;

/// <summary>
/// The operator over the real UDS host: the desktop token reaches the Operator service only, the
/// worker token reaches the operator only through the write gate.
/// </summary>
[Collection("uds")]
public sealed class OperatorGrpcTests
{
    private static async Task<RpcException> Denied(Func<Task> call) => await Should.ThrowAsync<RpcException>(call);

    private static async Task<Scout.ControlPlane.Operator.OperatorProject> QueueAsync(ControlPlaneTestHost host, string topic)
    {
        using var desktopChannel = host.CreateChannel();
        var features = new GrpcFeatures.FeaturesClient(desktopChannel);
        await features.SetWriteGrantAsync(new SetWriteGrantRequest { Id = "youtube", Granted = true, ConfirmWrites = true });
        var queued = await host.Services.GetRequiredService<OperatorService>().StartProjectAsync(topic, CancellationToken.None);
        await features.SetWriteGrantAsync(new SetWriteGrantRequest { Id = "youtube", Granted = false });
        return queued;
    }

    private static WireMandate Mandate()
    {
        var mandate = new WireMandate { Objective = " Teach geography ", Audience = "Students", Language = "en", Cadence = "weekly" };
        mandate.Pillars.AddRange(["Rivers", "Mountains"]);
        return mandate;
    }

    [Fact]
    public async Task The_Worker_Token_Is_Denied_Every_Operator_Rpc_And_Changes_Nothing()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var queued = await QueueAsync(host, "Why rivers meander");
        using var channel = host.CreateWorkerChannel();
        var client = new GrpcOperator.OperatorClient(channel);

        var denied = new Dictionary<string, RpcException>(StringComparer.Ordinal)
        {
            ["GetMandate"] = await Denied(() => client.GetMandateAsync(new GetMandateRequest()).ResponseAsync),
            ["SaveMandate"] = await Denied(() => client.SaveMandateAsync(Mandate()).ResponseAsync),
            ["StartProject"] = await Denied(() => client.StartProjectAsync(new StartProjectRequest { Topic = "Glaciers explained" }).ResponseAsync),
            ["DecideClaim"] = await Denied(() => client.DecideClaimAsync(new DecideClaimRequest { ProjectId = queued.Id, ClaimId = "c1", Status = "supported" }).ResponseAsync),
            ["ListProjects"] = await Denied(() => client.ListProjectsAsync(new ListProjectsRequest()).ResponseAsync),
            ["GetProject"] = await Denied(() => client.GetProjectAsync(new GetProjectRequest { Id = queued.Id }).ResponseAsync),
            ["ProduceProject"] = await Denied(() => client.ProduceProjectAsync(new GetProjectRequest { Id = queued.Id }).ResponseAsync),
            ["ReorderScenes"] = await Denied(() => client.ReorderScenesAsync(new ReorderScenesRequest { ProjectId = queued.Id }).ResponseAsync),
            ["SetSceneLocked"] = await Denied(() => client.SetSceneLockedAsync(new SetSceneLockedRequest { ProjectId = queued.Id, SceneId = "s1", Locked = true }).ResponseAsync),
            ["RegenerateScene"] = await Denied(() => client.RegenerateSceneAsync(new SceneRequest { ProjectId = queued.Id, SceneId = "s1" }).ResponseAsync),
            ["AttachSceneMedia"] = await Denied(() => client.AttachSceneMediaAsync(new AttachSceneMediaRequest { ProjectId = queued.Id, SceneId = "s1", Kind = "audio", FileName = "a.mp3" }).ResponseAsync),
            ["GetMediaDirectory"] = await Denied(() => client.GetMediaDirectoryAsync(new GetProjectRequest { Id = queued.Id }).ResponseAsync),
            ["GetReadiness"] = await Denied(() => client.GetReadinessAsync(new GetReadinessRequest()).ResponseAsync),
        };

        foreach (var (method, ex) in denied)
        {
            ex.StatusCode.ShouldBe(StatusCode.PermissionDenied, method);
            ex.Status.Detail.ShouldBe($"This caller may not call /scout.controlplane.v1.Operator/{method}.", method);
        }

        var operatorService = host.Services.GetRequiredService<OperatorService>();
        (await operatorService.GetMandateAsync(CancellationToken.None)).ShouldBe(Scout.ControlPlane.Operator.Mandate.Empty);
        (await operatorService.ListProjectsAsync(0, CancellationToken.None)).Select(p => (p.Id, p.Status)).ShouldBe([(queued.Id, "queued")]);
    }

    [Fact]
    public async Task The_Desktop_Token_Is_Denied_The_Workers_Operator_Rpcs()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var queued = await QueueAsync(host, "Why rivers meander");
        using var channel = host.CreateChannel();
        var gate = new WorkerGate.WorkerGateClient(channel);

        var denied = new Dictionary<string, RpcException>(StringComparer.Ordinal)
        {
            ["GetOperatorMandate"] = await Denied(() => gate.GetOperatorMandateAsync(new GetMandateRequest()).ResponseAsync),
            ["SaveOperatorProject"] = await Denied(() => gate.SaveOperatorProjectAsync(new SaveOperatorProjectRequest
            {
                Project = new Project { Topic = "Glaciers explained", Status = "researching" },
            }).ResponseAsync),
            ["NextQueuedProject"] = await Denied(() => gate.NextQueuedProjectAsync(new NextQueuedProjectRequest()).ResponseAsync),
            ["GetOperatorMediaDirectory"] = await Denied(() => gate.GetOperatorMediaDirectoryAsync(new GetProjectRequest { Id = queued.Id }).ResponseAsync),
            ["ReportReadiness"] = await Denied(() => gate.ReportReadinessAsync(new WireReadiness { Provider = "openai" }).ResponseAsync),
        };

        foreach (var (method, ex) in denied)
        {
            ex.StatusCode.ShouldBe(StatusCode.PermissionDenied, method);
            ex.Status.Detail.ShouldBe($"This caller may not call /scout.controlplane.v1.WorkerGate/{method}.", method);
        }

        (await host.Services.GetRequiredService<OperatorService>().ListProjectsAsync(0, CancellationToken.None))
            .ShouldHaveSingleItem().Status.ShouldBe("queued");
        (await host.Services.GetRequiredService<OperatorService>().GetReadinessAsync(CancellationToken.None)).ReportedAt.ShouldBeNull();
    }

    [Fact]
    public async Task The_Mandate_The_Desktop_Saves_Is_The_One_The_Worker_Reads()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var desktopChannel = host.CreateChannel();
        using var workerChannel = host.CreateWorkerChannel();
        var desktop = new GrpcOperator.OperatorClient(desktopChannel);
        var gate = new WorkerGate.WorkerGateClient(workerChannel);

        var before = await gate.GetOperatorMandateAsync(new GetMandateRequest());
        var saved = await desktop.SaveMandateAsync(Mandate());
        var read = await gate.GetOperatorMandateAsync(new GetMandateRequest());
        var invalid = await Denied(() => desktop.SaveMandateAsync(new WireMandate { Objective = "Teach" }).ResponseAsync);

        (before.Objective, before.HasUpdatedAt).ShouldBe((string.Empty, false));
        read.ShouldBe(saved);
        (read.Objective, string.Join("|", read.Pillars), read.Language).ShouldBe(("Teach geography", "Rivers|Mountains", "en"));
        DateTimeOffset.ParseExact(read.UpdatedAt, "O", System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBeInRange(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow);
        invalid.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        invalid.Status.Detail.ShouldBe("The language is required.");
    }

    [Fact]
    public async Task A_Project_Started_On_The_Desktop_Is_Taken_Scripted_By_The_Worker_And_Its_Claims_Decided_By_The_Owner()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var desktopChannel = host.CreateChannel();
        using var workerChannel = host.CreateWorkerChannel();
        var desktop = new GrpcOperator.OperatorClient(desktopChannel);
        var gate = new WorkerGate.WorkerGateClient(workerChannel);

        var refused = await Denied(() => gate.NextQueuedProjectAsync(new NextQueuedProjectRequest()).ResponseAsync);
        var startRefused = await Denied(() => desktop.StartProjectAsync(new StartProjectRequest { Topic = "Why rivers meander" }).ResponseAsync);
        var listedBeforeGrant = await desktop.ListProjectsAsync(new ListProjectsRequest());
        await new GrpcFeatures.FeaturesClient(desktopChannel).SetWriteGrantAsync(new SetWriteGrantRequest { Id = "youtube", Granted = true, ConfirmWrites = true });
        var started = await desktop.StartProjectAsync(new StartProjectRequest { Topic = "Why rivers meander" });
        var next = await gate.NextQueuedProjectAsync(new NextQueuedProjectRequest());
        var draft = next.Project.Clone();
        draft.Status = "script_ready";
        draft.Script = new WireScript { Hook = "Rivers never run straight.", CallToAction = "Subscribe." };
        draft.Script.TitleVariants.Add("Why rivers bend");
        draft.Claims.Add(new WireClaim { Id = "c1", Text = "Most rivers meander.", Risk = "low", Status = "pending" });
        draft.Claims.Add(new WireClaim { Id = "c2", Text = "The Amazon is the longest river.", Risk = "high", Status = "unsupported" });
        await gate.SaveOperatorProjectAsync(new SaveOperatorProjectRequest { Project = draft });
        var shown = await desktop.GetProjectAsync(new GetProjectRequest { Id = started.Id });
        var decided = await desktop.DecideClaimAsync(new DecideClaimRequest { ProjectId = started.Id, ClaimId = "c2", Status = "waived", Note = "Disputed; said as disputed" });
        var waiveWithoutNote = await Denied(() => desktop.DecideClaimAsync(new DecideClaimRequest { ProjectId = started.Id, ClaimId = "c1", Status = "waived" }).ResponseAsync);
        var unknown = await Denied(() => desktop.GetProjectAsync(new GetProjectRequest { Id = "nope" }).ResponseAsync);
        var overwrite = draft.Clone();
        overwrite.Claims[1].Status = "pending";
        var tampered = await Denied(() => gate.SaveOperatorProjectAsync(new SaveOperatorProjectRequest { Project = overwrite }).ResponseAsync);

        refused.StatusCode.ShouldBe(StatusCode.PermissionDenied);
        refused.Status.Detail.ShouldBe("The owner has not allowed youtube to write; allow it on the Features screen of Scout Desktop.");
        (startRefused.StatusCode, startRefused.Status.Detail).ShouldBe((StatusCode.PermissionDenied,
            "The owner has not allowed youtube to write; allow it on the Features screen of Scout Desktop."));
        listedBeforeGrant.Projects.ShouldBeEmpty();
        (started.Status, started.Origin).ShouldBe(("queued", "desktop"));
        (next.Project.Id, next.Project.Status, next.HasOriginReference).ShouldBe((started.Id, "researching", false));
        (shown.Status, shown.Script.Hook, shown.Script.TitleVariants.Single()).ShouldBe(("script_ready", "Rivers never run straight.", "Why rivers bend"));
        shown.Claims.Select(c => (c.Id, c.Status)).ShouldBe([("c1", "pending"), ("c2", "unsupported")]);
        decided.Claims.Select(c => (c.Id, c.Status, c.HasNote ? c.Note : null)).ShouldBe([("c1", "pending", (string?)null), ("c2", "waived", "Disputed; said as disputed")]);
        (waiveWithoutNote.StatusCode, waiveWithoutNote.Status.Detail).ShouldBe((StatusCode.InvalidArgument, "A waived claim needs a note saying why."));
        (unknown.StatusCode, unknown.Status.Detail).ShouldBe((StatusCode.NotFound, "Unknown project 'nope'."));
        (tampered.StatusCode, tampered.Status.Detail).ShouldBe((StatusCode.FailedPrecondition, "Claim 'c2' carries the owner's decision; the worker may not change or drop it."));
        (await desktop.ListProjectsAsync(new ListProjectsRequest())).Projects.ShouldHaveSingleItem().Claims[1].Status.ShouldBe("waived");
    }

    [Fact]
    public async Task A_Chat_Project_Keeps_Its_Origin_Reference_For_The_Worker_And_Never_Sends_It_To_The_Desktop()
    {
        const string reference = "discord:channel/4242/message/99";
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var desktopChannel = host.CreateChannel();
        using var workerChannel = host.CreateWorkerChannel();
        var desktop = new GrpcOperator.OperatorClient(desktopChannel);
        var gate = new WorkerGate.WorkerGateClient(workerChannel);
        await new GrpcFeatures.FeaturesClient(desktopChannel).SetWriteGrantAsync(new SetWriteGrantRequest { Id = "youtube", Granted = true, ConfirmWrites = true });

        var saved = await gate.SaveOperatorProjectAsync(new SaveOperatorProjectRequest
        {
            Project = new Project { Topic = "Glaciers explained", Status = "researching" },
            OriginReference = reference,
        });
        var shown = await desktop.GetProjectAsync(new GetProjectRequest { Id = saved.Id });
        var listed = await desktop.ListProjectsAsync(new ListProjectsRequest());
        await host.Services.GetRequiredService<Scout.ControlPlane.Operator.JsonFileOperatorStore>().UpdateProjectsAsync(
            projects => { projects[0] = projects[0] with { Status = "queued" }; return 0; },
            CancellationToken.None);
        var next = await gate.NextQueuedProjectAsync(new NextQueuedProjectRequest());

        (saved.Origin, shown.Origin).ShouldBe(("chat", "chat"));
        Encoding.UTF8.GetString(shown.ToByteArray()).ShouldNotContain(reference);
        Encoding.UTF8.GetString(listed.ToByteArray()).ShouldNotContain(reference);
        (next.Project.Id, next.OriginReference).ShouldBe((saved.Id, reference));
    }

    [Fact]
    public async Task A_Worker_Save_With_A_Status_It_May_Not_Set_Is_An_Invalid_Argument()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var desktopChannel = host.CreateChannel();
        using var workerChannel = host.CreateWorkerChannel();
        await new GrpcFeatures.FeaturesClient(desktopChannel).SetWriteGrantAsync(new SetWriteGrantRequest { Id = "youtube", Granted = true, ConfirmWrites = true });

        var ex = await Denied(() => new WorkerGate.WorkerGateClient(workerChannel).SaveOperatorProjectAsync(new SaveOperatorProjectRequest
        {
            Project = new Project { Topic = "Glaciers explained", Status = "published" },
        }).ResponseAsync);

        (ex.StatusCode, ex.Status.Detail).ShouldBe((StatusCode.InvalidArgument,
            "Status 'published' is not one the worker may set: researching, scripting, script_ready, producing, media_ready, failed."));
        (await host.Services.GetRequiredService<OperatorService>().ListProjectsAsync(0, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_Desktop_Token_Is_Denied_The_Workers_Project_Reads()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var queued = await QueueAsync(host, "Why rivers meander");
        using var channel = host.CreateChannel();
        var gate = new WorkerGate.WorkerGateClient(channel);

        var get = await Denied(() => gate.GetOperatorProjectAsync(new GetProjectRequest { Id = queued.Id }).ResponseAsync);
        var list = await Denied(() => gate.ListOperatorProjectsAsync(new ListProjectsRequest()).ResponseAsync);

        (get.StatusCode, get.Status.Detail).ShouldBe((StatusCode.PermissionDenied, "This caller may not call /scout.controlplane.v1.WorkerGate/GetOperatorProject."));
        (list.StatusCode, list.Status.Detail).ShouldBe((StatusCode.PermissionDenied, "This caller may not call /scout.controlplane.v1.WorkerGate/ListOperatorProjects."));
    }

    [Fact]
    public async Task The_Worker_Reads_The_Owners_Claim_Decisions_Under_The_Grant_And_Never_The_Origin_Reference()
    {
        const string reference = "slack:C42/1700000000.000100";
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var desktopChannel = host.CreateChannel();
        using var workerChannel = host.CreateWorkerChannel();
        var desktop = new GrpcOperator.OperatorClient(desktopChannel);
        var gate = new WorkerGate.WorkerGateClient(workerChannel);
        var older = await QueueAsync(host, "Why rivers meander");

        var refused = await Denied(() => gate.GetOperatorProjectAsync(new GetProjectRequest { Id = older.Id }).ResponseAsync);
        await new GrpcFeatures.FeaturesClient(desktopChannel).SetWriteGrantAsync(new SetWriteGrantRequest { Id = "youtube", Granted = true, ConfirmWrites = true });
        var draft = (await gate.NextQueuedProjectAsync(new NextQueuedProjectRequest())).Project;
        draft.Status = "script_ready";
        draft.Claims.Add(new WireClaim { Id = "c1", Text = "Most rivers meander.", Risk = "low", Status = "pending" });
        await gate.SaveOperatorProjectAsync(new SaveOperatorProjectRequest { Project = draft });
        await desktop.DecideClaimAsync(new DecideClaimRequest { ProjectId = older.Id, ClaimId = "c1", Status = "supported" });
        var newer = await gate.SaveOperatorProjectAsync(new SaveOperatorProjectRequest
        {
            Project = new Project { Topic = "Glaciers explained", Status = "researching" },
            OriginReference = reference,
        });
        var read = await gate.GetOperatorProjectAsync(new GetProjectRequest { Id = older.Id });
        var listed = await gate.ListOperatorProjectsAsync(new ListProjectsRequest { Limit = 1 });
        var logged = await new GrpcFeatures.FeaturesClient(desktopChannel).ListWritesAsync(new ListWritesRequest());

        (refused.StatusCode, refused.Status.Detail).ShouldBe((StatusCode.PermissionDenied,
            "The owner has not allowed youtube to write; allow it on the Features screen of Scout Desktop."));
        read.Claims.ShouldHaveSingleItem().Status.ShouldBe("supported");
        listed.Projects.ShouldHaveSingleItem().Id.ShouldBe(newer.Id);
        Encoding.UTF8.GetString(listed.ToByteArray()).ShouldNotContain(reference);
        logged.Entries.Select(e => (e.Action, e.Target, e.Outcome, e.HasApprovedBy)).ShouldBe([
            ("Scout's data folder", newer.Id, "saved researching", false),
            ("Scout's data folder", older.Id, "saved script_ready", false),
            ("Scout's data folder", older.Id, "saved researching", false),
        ]);
    }

    [Fact]
    public async Task A_Project_Goes_To_Production_Its_Scenes_Cross_The_Wire_Whole_And_The_Owner_Edits_Them()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var desktopChannel = host.CreateChannel();
        using var workerChannel = host.CreateWorkerChannel();
        var desktop = new GrpcOperator.OperatorClient(desktopChannel);
        var gate = new WorkerGate.WorkerGateClient(workerChannel);
        await new GrpcFeatures.FeaturesClient(desktopChannel).SetWriteGrantAsync(new SetWriteGrantRequest { Id = "youtube", Granted = true, ConfirmWrites = true });
        var started = await desktop.StartProjectAsync(new StartProjectRequest { Topic = "Why rivers meander" });
        var draft = (await gate.NextQueuedProjectAsync(new NextQueuedProjectRequest())).Project;
        draft.Status = "script_ready";
        draft.Claims.Add(new WireClaim { Id = "c1", Text = "Most rivers meander.", Risk = "low", Status = "pending" });
        await gate.SaveOperatorProjectAsync(new SaveOperatorProjectRequest { Project = draft });

        var pending = await Denied(() => desktop.ProduceProjectAsync(new GetProjectRequest { Id = started.Id }).ResponseAsync);
        await desktop.DecideClaimAsync(new DecideClaimRequest { ProjectId = started.Id, ClaimId = "c1", Status = "supported" });
        var produced = await desktop.ProduceProjectAsync(new GetProjectRequest { Id = started.Id });
        var taken = (await gate.NextQueuedProjectAsync(new NextQueuedProjectRequest())).Project;
        var workerMedia = await gate.GetOperatorMediaDirectoryAsync(new GetProjectRequest { Id = started.Id });
        var first = new WireScene
        {
            Id = "s1",
            Order = 1,
            Narration = "Rivers bend.",
            VisualPrompt = "A meander",
            Status = "ready",
            AudioSource = "generated",
            VisualSource = "generated",
            AudioFile = "s1.mp3",
            VisualFile = "s1.png",
            DurationMs = 4200,
        };
        var second = new WireScene
        {
            Id = "s2",
            Order = 2,
            Narration = "Water erodes.",
            VisualPrompt = "A bank",
            Status = "failed",
            Error = "No image service.",
            AudioSource = "none",
            VisualSource = "text_card",
        };
        taken.Status = "media_ready";
        taken.Scenes.AddRange([first, second]);
        await gate.SaveOperatorProjectAsync(new SaveOperatorProjectRequest { Project = taken });
        var shown = await desktop.GetProjectAsync(new GetProjectRequest { Id = started.Id });
        var desktopMedia = await desktop.GetMediaDirectoryAsync(new GetProjectRequest { Id = started.Id });
        await File.WriteAllTextAsync(Path.Combine(desktopMedia.Path, "bank.jpg"), "image");
        var reordered = await desktop.ReorderScenesAsync(new ReorderScenesRequest { ProjectId = started.Id, SceneIds = { "s2", "s1" } });
        var attached = await desktop.AttachSceneMediaAsync(new AttachSceneMediaRequest { ProjectId = started.Id, SceneId = "s2", Kind = "visual", FileName = "bank.jpg" });
        var locked = await desktop.SetSceneLockedAsync(new SetSceneLockedRequest { ProjectId = started.Id, SceneId = "s1", Locked = true });
        var regenerateLocked = await Denied(() => desktop.RegenerateSceneAsync(new SceneRequest { ProjectId = started.Id, SceneId = "s1" }).ResponseAsync);
        var regenerated = await desktop.RegenerateSceneAsync(new SceneRequest { ProjectId = started.Id, SceneId = "s2" });
        var unknownScene = await Denied(() => desktop.SetSceneLockedAsync(new SetSceneLockedRequest { ProjectId = started.Id, SceneId = "s9" }).ResponseAsync);
        var unsafeName = await Denied(() => desktop.AttachSceneMediaAsync(new AttachSceneMediaRequest { ProjectId = started.Id, SceneId = "s2", Kind = "audio", FileName = "../x.mp3" }).ResponseAsync);
        var escape = await Denied(() => desktop.GetMediaDirectoryAsync(new GetProjectRequest { Id = "../x" }).ResponseAsync);
        var tamper = locked.Clone();
        tamper.Scenes[1].Narration = "Rewritten.";
        var tampered = await Denied(() => gate.SaveOperatorProjectAsync(new SaveOperatorProjectRequest { Project = tamper }).ResponseAsync);

        (pending.StatusCode, pending.Status.Detail).ShouldBe((StatusCode.InvalidArgument, "1 claim is still pending; decide it before production."));
        (produced.Status, taken.Id).ShouldBe(("production_queued", started.Id));
        workerMedia.Path.ShouldBe(desktopMedia.Path);
        Directory.Exists(desktopMedia.Path).ShouldBeTrue();
        shown.Scenes.ShouldBe([first, second]);
        (shown.Scenes[0].HasError, shown.Scenes[1].HasAudioFile, shown.Scenes[1].HasVisualFile).ShouldBe((false, false, false));
        reordered.Scenes.Select(s => (s.Id, s.Order)).ShouldBe([("s2", 1), ("s1", 2)]);
        (attached.Scenes[0].VisualSource, attached.Scenes[0].VisualFile, attached.Scenes[0].Status, attached.Scenes[0].Error).ShouldBe(("owner", "bank.jpg", "failed", "No image service."));
        locked.Scenes.Select(s => s.Locked).ShouldBe([false, true]);
        (regenerateLocked.StatusCode, regenerateLocked.Status.Detail).ShouldBe((StatusCode.InvalidArgument, "Scene 's1' is locked; unlock it before regenerating it."));
        (regenerated.Status, regenerated.Scenes[0].Status, regenerated.Scenes[0].VisualFile).ShouldBe(("production_queued", "planned", "bank.jpg"));
        (unknownScene.StatusCode, unknownScene.Status.Detail).ShouldBe((StatusCode.NotFound, $"Unknown scene 's9' in project '{started.Id}'."));
        unsafeName.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        (escape.StatusCode, escape.Status.Detail).ShouldBe((StatusCode.InvalidArgument, "'../x' is not a project id."));
        (tampered.StatusCode, tampered.Status.Detail).ShouldBe((StatusCode.FailedPrecondition, "The project is no longer producing."));
    }

    [Fact]
    public async Task A_Readiness_Report_That_Cannot_Be_Written_Answers_Failed_Precondition_With_The_Reason_And_Keeps_The_Earlier_Report()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var desktopChannel = host.CreateChannel();
        using var workerChannel = host.CreateWorkerChannel();
        var desktop = new GrpcOperator.OperatorClient(desktopChannel);
        var gate = new WorkerGate.WorkerGateClient(workerChannel);
        await gate.ReportReadinessAsync(new WireReadiness { Provider = "openai" });
        Directory.CreateDirectory(host.Services.GetRequiredService<JsonFileOperatorStore>().ReadinessPath + ".tmp");

        var refused = await Denied(() => gate.ReportReadinessAsync(new WireReadiness { Provider = "gemini" }).ResponseAsync);
        var read = await desktop.GetReadinessAsync(new GetReadinessRequest());

        (refused.StatusCode, refused.Status.Detail).ShouldBe(
            (StatusCode.FailedPrecondition, "Scout could not save the worker's readiness report in its data folder."));
        read.Provider.ShouldBe("openai");
    }

    [Fact]
    public async Task The_Readiness_The_Worker_Reports_Is_The_One_The_Desktop_Reads_Stamped_By_The_Control_Plane()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var desktopChannel = host.CreateChannel();
        using var workerChannel = host.CreateWorkerChannel();
        var desktop = new GrpcOperator.OperatorClient(desktopChannel);
        var gate = new WorkerGate.WorkerGateClient(workerChannel);
        var report = new WireReadiness { SpeechAvailable = true, Provider = "openai", FfmpegAvailable = true, YoutubeSignedIn = true, ReportedAt = "1970-01-01T00:00:00.0000000+00:00" };
        report.Notes.Add("No image model is configured.");

        var before = await desktop.GetReadinessAsync(new GetReadinessRequest());
        var stored = await gate.ReportReadinessAsync(report);
        var read = await desktop.GetReadinessAsync(new GetReadinessRequest());

        (before.Provider, before.HasReportedAt, before.Notes.Single()).ShouldBe((string.Empty, false, "The Scout worker has not reported yet."));
        read.ShouldBe(stored);
        (read.SpeechAvailable, read.ImagesAvailable, read.Provider, read.FfmpegAvailable, read.YoutubeSignedIn, read.Notes.Single())
            .ShouldBe((true, false, "openai", true, true, "No image model is configured."));
        DateTimeOffset.ParseExact(read.ReportedAt, "O", System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBeInRange(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Scenes_Map_To_The_Wire_And_Back_Field_For_Field_With_Unset_Optionals_For_Absent_Values()
    {
        var full = new Scout.ControlPlane.Operator.Scene("s1", 2, "Rivers bend.", "A meander", true, "failed", "Speech timed out.", "owner", "generated", "mine.mp3", "s1.png", 4200);
        var bare = new Scout.ControlPlane.Operator.Scene("s2", 1, "Water erodes.", "A bank", false, "planned", null, "none", "text_card", null, null, 0);
        var project = new Scout.ControlPlane.Operator.OperatorProject("p1", "Why rivers meander", "media_ready", "desktop", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, [], null, [])
        {
            Scenes = [full, bare],
        };

        var wire = Scout.Host.ControlPlane.Services.OperatorMapping.ToWire(project);
        var back = Scout.Host.ControlPlane.Services.OperatorMapping.ToDomain(wire);

        wire.Scenes.Select(s => (s.Id, s.Order, s.Locked, s.Status, s.AudioSource, s.VisualSource, s.DurationMs)).ShouldBe([
            ("s1", 2, true, "failed", "owner", "generated", 4200),
            ("s2", 1, false, "planned", "none", "text_card", 0),
        ]);
        wire.Scenes.Select(s => (s.HasError, s.HasAudioFile, s.HasVisualFile)).ShouldBe([(true, true, true), (false, false, false)]);
        back.Scenes.ShouldBe([full, bare]);
    }

    [Fact]
    public void Readiness_Maps_To_The_Wire_With_Its_Stamp_And_Back_Without_The_Senders_Stamp()
    {
        var stamped = new Scout.ControlPlane.Operator.Readiness(true, false, "openai", true, false, ["No image model is configured."], DateTimeOffset.UnixEpoch);

        var wire = Scout.Host.ControlPlane.Services.OperatorMapping.ToWire(stamped);
        var unstamped = Scout.Host.ControlPlane.Services.OperatorMapping.ToWire(stamped with { ReportedAt = null });
        var back = Scout.Host.ControlPlane.Services.OperatorMapping.ToDomain(wire);

        (wire.SpeechAvailable, wire.ImagesAvailable, wire.Provider, wire.FfmpegAvailable, wire.YoutubeSignedIn, wire.ReportedAt)
            .ShouldBe((true, false, "openai", true, false, "1970-01-01T00:00:00.0000000+00:00"));
        wire.Notes.ShouldBe(["No image model is configured."]);
        unstamped.HasReportedAt.ShouldBeFalse();
        (back.SpeechAvailable, back.ImagesAvailable, back.Provider, back.FfmpegAvailable, back.YouTubeSignedIn, back.ReportedAt)
            .ShouldBe((true, false, "openai", true, false, (DateTimeOffset?)null));
        back.Notes.ShouldBe(["No image model is configured."]);
    }
}
