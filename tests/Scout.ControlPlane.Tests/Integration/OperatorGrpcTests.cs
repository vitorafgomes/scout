using System.Text;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Scout.ControlPlane.Contracts;
using Shouldly;
using Xunit;
using GrpcFeatures = Scout.ControlPlane.Contracts.Features;
using GrpcOperator = Scout.ControlPlane.Contracts.Operator;
using OperatorService = Scout.ControlPlane.Operator.OperatorService;
using WireClaim = Scout.ControlPlane.Contracts.Claim;
using WireMandate = Scout.ControlPlane.Contracts.Mandate;
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
        await QueueAsync(host, "Why rivers meander");
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
        };

        foreach (var (method, ex) in denied)
        {
            ex.StatusCode.ShouldBe(StatusCode.PermissionDenied, method);
            ex.Status.Detail.ShouldBe($"This caller may not call /scout.controlplane.v1.WorkerGate/{method}.", method);
        }

        (await host.Services.GetRequiredService<OperatorService>().ListProjectsAsync(0, CancellationToken.None))
            .ShouldHaveSingleItem().Status.ShouldBe("queued");
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
            "Status 'published' is not one the worker may set: researching, scripting, script_ready, failed."));
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
}
