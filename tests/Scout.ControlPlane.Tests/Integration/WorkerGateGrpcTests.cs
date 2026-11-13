using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Contracts;
using Scout.ControlPlane.Features;
using Shouldly;
using Xunit;
using GrpcFeatures = Scout.ControlPlane.Contracts.Features;
using GrpcModels = Scout.ControlPlane.Contracts.Models;
using GrpcProviders = Scout.ControlPlane.Contracts.Providers;

namespace Scout.ControlPlane.Tests.Integration;

/// <summary>
/// The two callers of the real UDS host: the desktop token reaches the desktop's
/// services only, the worker token reaches the write gate only.
/// </summary>
[Collection("uds")]
public sealed class WorkerGateGrpcTests
{
    private const string Waiting = "Waiting for the owner's approval on the Review screen of Scout Desktop.";

    private static async Task<RpcException> Denied(Func<Task> call) => await Should.ThrowAsync<RpcException>(call);

    [Fact]
    public async Task The_Worker_Can_Request_But_Never_Approve_Grant_Enable_Or_Read_The_Features()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var queue = host.Services.GetRequiredService<IActionQueue>();
        var upload = await queue.EnqueueAsync("youtube", "videos.insert", "intro.mp4", "Upload", CancellationToken.None);
        using var channel = host.CreateWorkerChannel();
        var features = new GrpcFeatures.FeaturesClient(channel);
        var providers = new GrpcProviders.ProvidersClient(channel);
        var models = new GrpcModels.ModelsClient(channel);
        var youtube = FeatureCatalog.YouTubeId;

        var denied = new Dictionary<string, RpcException>(StringComparer.Ordinal)
        {
            ["Features/ApproveAction"] = await Denied(() => features.ApproveActionAsync(new ActionDecisionRequest { Id = upload.Id }).ResponseAsync),
            ["Features/RejectAction"] = await Denied(() => features.RejectActionAsync(new ActionDecisionRequest { Id = upload.Id }).ResponseAsync),
            ["Features/SetWriteGrant"] = await Denied(() => features.SetWriteGrantAsync(new SetWriteGrantRequest { Id = youtube, Granted = true, ConfirmWrites = true }).ResponseAsync),
            ["Features/Enable"] = await Denied(() => features.EnableAsync(new EnableRequest { Id = youtube, ConfirmWrites = true }).ResponseAsync),
            ["Features/Disable"] = await Denied(() => features.DisableAsync(new DisableRequest { Id = youtube }).ResponseAsync),
            ["Features/DeleteCredentials"] = await Denied(() => features.DeleteCredentialsAsync(new DeleteCredentialsRequest { Id = youtube }).ResponseAsync),
            ["Features/ListFeatures"] = await Denied(() => features.ListFeaturesAsync(new ListFeaturesRequest()).ResponseAsync),
            ["Providers/StartProvider"] = await Denied(() => providers.StartProviderAsync(new StartProviderRequest { Id = ProviderId.Ollama }).ResponseAsync),
            ["Models/DeleteModel"] = await Denied(() => models.DeleteModelAsync(new DeleteModelRequest { Provider = ProviderId.Ollama, Model = "llama3" }).ResponseAsync),
        };

        foreach (var (method, ex) in denied)
        {
            ex.StatusCode.ShouldBe(StatusCode.PermissionDenied, method);
            ex.Status.Detail.ShouldBe($"This caller may not call /scout.controlplane.v1.{method}.", method);
        }

        (await queue.ListPendingAsync(CancellationToken.None)).ShouldHaveSingleItem().Status.ShouldBe(PendingActionStatus.Pending);
        var state = await host.Services.GetRequiredService<IFeatureStateStore>().GetAsync(youtube, CancellationToken.None);
        (state.Granted, state.Enabled).ShouldBe((false, false));
        host.Ollama.DeletedModels.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_Desktop_Token_Cannot_Call_The_Worker_Gate()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var channel = host.CreateChannel();
        var gate = new WorkerGate.WorkerGateClient(channel);

        var request = await Denied(() => gate.RequestWriteAsync(new RequestWriteRequest
        {
            FeatureId = "youtube",
            Scope = "videos.insert",
            Target = "intro.mp4",
            Summary = "Upload",
        }).ResponseAsync);
        var record = await Denied(() => gate.RecordWriteAsync(new RecordWriteRequest
        {
            FeatureId = "youtube",
            Scope = "Scout's data folder",
            Target = "a.md",
            Outcome = "ok",
        }).ResponseAsync);

        request.StatusCode.ShouldBe(StatusCode.PermissionDenied);
        request.Status.Detail.ShouldBe("This caller may not call /scout.controlplane.v1.WorkerGate/RequestWrite.");
        record.StatusCode.ShouldBe(StatusCode.PermissionDenied);
        record.Status.Detail.ShouldBe("This caller may not call /scout.controlplane.v1.WorkerGate/RecordWrite.");
        (await host.Services.GetRequiredService<IActionQueue>().ListPendingAsync(CancellationToken.None)).ShouldBeEmpty();
        (await host.Services.GetRequiredService<IWriteLog>().ListAsync(10, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_Worker_Requests_A_Write_And_Records_It_Once_The_Owner_Approved_Over_Grpc()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var desktopChannel = host.CreateChannel();
        using var workerChannel = host.CreateWorkerChannel();
        var desktop = new GrpcFeatures.FeaturesClient(desktopChannel);
        var gate = new WorkerGate.WorkerGateClient(workerChannel);
        RequestWriteRequest Upload() => new() { FeatureId = "youtube", Scope = "videos.insert", Target = "intro.mp4", Summary = "Upload 'Intro' as private" };

        var beforeGrant = await gate.RequestWriteAsync(Upload());
        await desktop.SetWriteGrantAsync(new SetWriteGrantRequest { Id = "youtube", Granted = true, ConfirmWrites = true });
        var queued = await gate.RequestWriteAsync(Upload());
        var allowed = await gate.RequestWriteAsync(new RequestWriteRequest { FeatureId = "youtube", Scope = "Scout's data folder", Target = "a.md", Summary = "Save" });
        var undeclared = await gate.RequestWriteAsync(new RequestWriteRequest { FeatureId = "youtube", Scope = "videos.delete", Target = "x", Summary = "Delete" });
        var read = await gate.RequestWriteAsync(new RequestWriteRequest { FeatureId = "youtube", Scope = "videos.list", Target = "x", Summary = "Read" });
        var early = await Denied(() => gate.RecordWriteAsync(new RecordWriteRequest
        {
            FeatureId = "youtube",
            Scope = "videos.insert",
            Target = "intro.mp4",
            Outcome = "ok",
            ActionId = queued.ActionId,
        }).ResponseAsync);
        await desktop.ApproveActionAsync(new ActionDecisionRequest { Id = queued.ActionId });
        await gate.RecordWriteAsync(new RecordWriteRequest
        {
            FeatureId = "youtube",
            Scope = "videos.insert",
            Target = "intro.mp4",
            Outcome = "uploaded",
            ActionId = queued.ActionId,
        });

        (beforeGrant.Decision, beforeGrant.Reason, beforeGrant.HasActionId).ShouldBe((
            "refused", "The owner has not allowed youtube to write; allow it on the Features screen of Scout Desktop.", false));
        (queued.Decision, queued.Reason, queued.HasActionId).ShouldBe(("queued", Waiting, true));
        (allowed.Decision, allowed.Reason, allowed.HasActionId).ShouldBe(("allowed", string.Empty, true));
        (undeclared.Decision, undeclared.Reason).ShouldBe(("refused", "videos.delete is not declared by youtube"));
        (read.Decision, read.Reason).ShouldBe(("refused", "videos.list is a read"));
        early.StatusCode.ShouldBe(StatusCode.FailedPrecondition);
        early.Status.Detail.ShouldBe($"Action '{queued.ActionId}' is not approved by the owner.");
        (await desktop.ListWritesAsync(new ListWritesRequest())).Entries
            .Select(e => (e.FeatureId, e.Action, e.Target, e.Outcome, e.ApprovedBy, e.ActionId))
            .ShouldBe([
                ("youtube", "videos.insert", "intro.mp4", "uploaded", "owner", queued.ActionId),
                ("youtube", "videos.insert", "intro.mp4", "approved", "owner", queued.ActionId),
                ("youtube", "Scout's data folder", "a.md", "started", string.Empty, allowed.ActionId),
            ]);
    }

    [Fact]
    public async Task The_Worker_Gate_Refuses_A_Call_Without_A_Valid_Token()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var none = host.CreateChannel(withToken: false);
        using var wrong = ControlPlaneTestHost.CreateChannelFor(host.SocketPath, "not-a-token");
        var request = new RequestWriteRequest { FeatureId = "youtube", Scope = "videos.insert", Target = "a", Summary = "b" };

        var withoutToken = await Denied(() => new WorkerGate.WorkerGateClient(none).RequestWriteAsync(request).ResponseAsync);
        var withWrongToken = await Denied(() => new WorkerGate.WorkerGateClient(wrong).RequestWriteAsync(request).ResponseAsync);

        withoutToken.StatusCode.ShouldBe(StatusCode.Unauthenticated);
        withWrongToken.StatusCode.ShouldBe(StatusCode.Unauthenticated);
        (await host.Services.GetRequiredService<IActionQueue>().ListPendingAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Host_Started_Without_A_Worker_Token_Keeps_The_Gate_Closed_And_Serves_The_Desktop()
    {
        await using var host = await ControlPlaneTestHost.StartAsync(withWorkerToken: false);
        using var workerChannel = host.CreateWorkerChannel();
        using var desktopChannel = host.CreateChannel();
        var request = new RequestWriteRequest { FeatureId = "youtube", Scope = "videos.insert", Target = "a", Summary = "b" };

        var worker = await Denied(() => new WorkerGate.WorkerGateClient(workerChannel).RequestWriteAsync(request).ResponseAsync);
        var desktopOnGate = await Denied(() => new WorkerGate.WorkerGateClient(desktopChannel).RequestWriteAsync(request).ResponseAsync);
        var listed = await new GrpcFeatures.FeaturesClient(desktopChannel).ListFeaturesAsync(new ListFeaturesRequest());

        worker.StatusCode.ShouldBe(StatusCode.Unauthenticated);
        desktopOnGate.StatusCode.ShouldBe(StatusCode.PermissionDenied);
        listed.Features.Select(f => f.Id).ShouldBe([FeatureCatalog.GitHubId, FeatureCatalog.KubernetesId, FeatureCatalog.YouTubeId, FeatureCatalog.DesktopId]);
    }

    private static RequestWriteRequest IssueIn(string groupId, string repository) => new()
    {
        FeatureId = FeatureCatalog.GitHubId,
        Scope = "New issues",
        Target = "GitHubActions.create_issue",
        Summary = "create_issue(owner=acme, repo=shop, title=Broken build, body)",
        Payload = """{"body":"It fails.","owner":"acme","repo":"shop","title":"Broken build"}""",
        GroupId = groupId,
        Repository = repository,
    };

    [Fact]
    public async Task RequestWrite_Queues_The_Action_In_Its_Group_And_CloseWriteGroup_Closes_The_Group_Without_Deciding_Any_Action()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var desktopChannel = host.CreateChannel();
        using var workerChannel = host.CreateWorkerChannel();
        var desktop = new GrpcFeatures.FeaturesClient(desktopChannel);
        var gate = new WorkerGate.WorkerGateClient(workerChannel);
        await desktop.SetWriteGrantAsync(new SetWriteGrantRequest { Id = FeatureCatalog.GitHubId, Granted = true, ConfirmWrites = true });
        var first = await gate.RequestWriteAsync(IssueIn("turn-1", "acme/shop"));
        var second = await gate.RequestWriteAsync(IssueIn("turn-1", "acme/shop"));

        var open = (await desktop.ListPendingActionsAsync(new ListPendingActionsRequest())).Actions;
        var closed = await gate.CloseWriteGroupAsync(new CloseWriteGroupRequest { GroupId = "turn-1" });
        var blank = await Denied(() => gate.CloseWriteGroupAsync(new CloseWriteGroupRequest { GroupId = " " }).ResponseAsync);
        var listed = (await desktop.ListPendingActionsAsync(new ListPendingActionsRequest())).Actions;

        open.Select(a => (a.Id, a.GroupId, a.Repository, a.GroupState)).ShouldBe([
            (first.ActionId, "turn-1", "acme/shop", "open"),
            (second.ActionId, "turn-1", "acme/shop", "open"),
        ]);
        closed.Actions.ShouldBe(2);
        (blank.StatusCode, blank.Status.Detail).ShouldBe((StatusCode.InvalidArgument, "A group is closed by its id; none was given."));
        listed.Select(a => (a.Id, a.Status, a.GroupState)).ShouldBe([
            (first.ActionId, "pending", "closed"),
            (second.ActionId, "pending", "closed"),
        ]);
        (await desktop.ListWritesAsync(new ListWritesRequest())).Entries.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\n")]
    public async Task A_Blank_Group_Id_And_Repository_Are_Treated_As_Absent_So_The_Action_Is_Queued_In_No_Group(string blank)
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var desktopChannel = host.CreateChannel();
        using var workerChannel = host.CreateWorkerChannel();
        var desktop = new GrpcFeatures.FeaturesClient(desktopChannel);
        var gate = new WorkerGate.WorkerGateClient(workerChannel);
        await desktop.SetWriteGrantAsync(new SetWriteGrantRequest { Id = FeatureCatalog.GitHubId, Granted = true, ConfirmWrites = true });
        var withoutTheFields = IssueIn("x", "x");
        withoutTheFields.ClearGroupId();
        withoutTheFields.ClearRepository();

        var blankFields = await gate.RequestWriteAsync(IssueIn(blank, blank));
        var absentFields = await gate.RequestWriteAsync(withoutTheFields);

        var queued = await host.Services.GetRequiredService<IActionQueue>().ListPendingAsync(CancellationToken.None);
        queued.Select(a => (a.Id, a.GroupId ?? "no group", a.Repository ?? "no repository")).ShouldBe([
            (blankFields.ActionId, "no group", "no repository"),
            (absentFields.ActionId, "no group", "no repository")], "a blank group id or repository is queued exactly as a request without the field");
        (await desktop.ListPendingActionsAsync(new ListPendingActionsRequest())).Actions.Select(a => (a.HasGroupId, a.HasRepository, a.HasGroupState))
            .ShouldBe([(false, false, false), (false, false, false)], "an action in no group carries no group state");
    }

    [Fact]
    public async Task The_Desktop_Token_Cannot_Close_A_Group_And_The_Group_Stays_Open()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var desktopChannel = host.CreateChannel();
        using var workerChannel = host.CreateWorkerChannel();
        var desktop = new GrpcFeatures.FeaturesClient(desktopChannel);
        await desktop.SetWriteGrantAsync(new SetWriteGrantRequest { Id = FeatureCatalog.GitHubId, Granted = true, ConfirmWrites = true });
        await new WorkerGate.WorkerGateClient(workerChannel).RequestWriteAsync(IssueIn("turn-1", "acme/shop"));

        var refused = await Denied(() =>
            new WorkerGate.WorkerGateClient(desktopChannel).CloseWriteGroupAsync(new CloseWriteGroupRequest { GroupId = "turn-1" }).ResponseAsync);

        (refused.StatusCode, refused.Status.Detail).ShouldBe(
            (StatusCode.PermissionDenied, "This caller may not call /scout.controlplane.v1.WorkerGate/CloseWriteGroup."));
        (await host.Services.GetRequiredService<IActionQueue>().ListPendingAsync(CancellationToken.None))
            .ShouldHaveSingleItem().GroupClosedAt.ShouldBeNull();
        (await desktop.ListPendingActionsAsync(new ListPendingActionsRequest())).Actions.ShouldHaveSingleItem().GroupState.ShouldBe("open");
    }

    [Fact]
    public async Task A_Close_The_Queue_Cannot_Write_Fails_The_Worker_Call_And_Leaves_The_Group_Open()
    {
        await using var host = await ControlPlaneTestHost.StartAsync(configureServices: services =>
        {
            services.RemoveAll<IActionQueue>();
            services.AddSingleton(sp => FailingCalls.Wrap<IActionQueue>(
                new JsonFileActionQueue(sp.GetRequiredService<IOptions<FeatureStateStoreOptions>>(), NullLogger<JsonFileActionQueue>.Instance),
                (method, _) => method.Name == nameof(IActionQueue.CloseGroupAsync) ? new IOException("disk full") : null));
        });
        using var desktopChannel = host.CreateChannel();
        using var workerChannel = host.CreateWorkerChannel();
        var desktop = new GrpcFeatures.FeaturesClient(desktopChannel);
        var gate = new WorkerGate.WorkerGateClient(workerChannel);
        await desktop.SetWriteGrantAsync(new SetWriteGrantRequest { Id = FeatureCatalog.GitHubId, Granted = true, ConfirmWrites = true });
        await gate.RequestWriteAsync(IssueIn("turn-1", "acme/shop"));

        var failed = await Denied(() => gate.CloseWriteGroupAsync(new CloseWriteGroupRequest { GroupId = "turn-1" }).ResponseAsync);

        failed.StatusCode.ShouldBe(StatusCode.Unknown);
        var left = (await desktop.ListPendingActionsAsync(new ListPendingActionsRequest())).Actions.ShouldHaveSingleItem();
        (left.Status, left.GroupState).ShouldBe(("pending", "open"));
        (await host.Services.GetRequiredService<IActionQueue>().ListPendingAsync(CancellationToken.None))
            .ShouldHaveSingleItem().GroupClosedAt.ShouldBeNull();
    }

    [Fact]
    public async Task A_Worker_That_Stops_After_A_Reversible_Write_Returns_And_Before_Its_Record_Leaves_The_Write_In_The_Log()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var desktopChannel = host.CreateChannel();
        using var workerChannel = host.CreateWorkerChannel();
        var desktop = new GrpcFeatures.FeaturesClient(desktopChannel);
        await desktop.SetWriteGrantAsync(new SetWriteGrantRequest { Id = FeatureCatalog.YouTubeId, Granted = true, ConfirmWrites = true });

        var allowed = await new WorkerGate.WorkerGateClient(workerChannel).RequestWriteAsync(
            new RequestWriteRequest { FeatureId = FeatureCatalog.YouTubeId, Scope = "Scout's data folder", Target = "a.md", Summary = "Save" });

        (allowed.Decision, allowed.HasActionId).ShouldBe(("allowed", true), "the allowed answer names the log entry written before the write runs");
        var entry = (await desktop.ListWritesAsync(new ListWritesRequest())).Entries.ShouldHaveSingleItem();
        (entry.FeatureId, entry.Action, entry.Target, entry.Outcome, entry.HasApprovedBy, entry.ActionId)
            .ShouldBe((FeatureCatalog.YouTubeId, "Scout's data folder", "a.md", "started", false, allowed.ActionId));
    }

    [Fact]
    public async Task A_Reversible_Write_Recorded_Against_Its_Started_Id_Lists_Once_With_How_It_Ended()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var desktopChannel = host.CreateChannel();
        using var workerChannel = host.CreateWorkerChannel();
        var desktop = new GrpcFeatures.FeaturesClient(desktopChannel);
        var gate = new WorkerGate.WorkerGateClient(workerChannel);
        await desktop.SetWriteGrantAsync(new SetWriteGrantRequest { Id = FeatureCatalog.YouTubeId, Granted = true, ConfirmWrites = true });
        var allowed = await gate.RequestWriteAsync(
            new RequestWriteRequest { FeatureId = FeatureCatalog.YouTubeId, Scope = "Scout's data folder", Target = "a.md", Summary = "Save" });

        await gate.RecordWriteAsync(new RecordWriteRequest
        {
            FeatureId = FeatureCatalog.YouTubeId,
            Scope = "Scout's data folder",
            Target = "a.md",
            Outcome = "succeeded",
            ActionId = allowed.ActionId,
        });

        var entry = (await desktop.ListWritesAsync(new ListWritesRequest())).Entries.ShouldHaveSingleItem();
        (entry.Target, entry.Outcome, entry.HasApprovedBy, entry.ActionId).ShouldBe(("a.md", "succeeded", false, allowed.ActionId));
        (await host.Services.GetRequiredService<IWriteLog>().ListAsync(10, CancellationToken.None)).Select(e => e.Outcome)
            .ShouldBe(["succeeded", "started"], "the log keeps both lines; the list shows the write once");
    }

    [Fact]
    public async Task ListWrites_With_A_Limit_Lists_That_Many_Writes_Newest_First_With_A_Started_Write_And_Its_Record_Counted_Once()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var desktopChannel = host.CreateChannel();
        using var workerChannel = host.CreateWorkerChannel();
        var desktop = new GrpcFeatures.FeaturesClient(desktopChannel);
        var gate = new WorkerGate.WorkerGateClient(workerChannel);
        await desktop.SetWriteGrantAsync(new SetWriteGrantRequest { Id = FeatureCatalog.YouTubeId, Granted = true, ConfirmWrites = true });
        async Task<string> SaveAsync(string target, bool recorded)
        {
            var allowed = await gate.RequestWriteAsync(
                new RequestWriteRequest { FeatureId = FeatureCatalog.YouTubeId, Scope = "Scout's data folder", Target = target, Summary = "Save" });
            if (recorded)
            {
                await gate.RecordWriteAsync(new RecordWriteRequest
                {
                    FeatureId = FeatureCatalog.YouTubeId,
                    Scope = "Scout's data folder",
                    Target = target,
                    Outcome = "succeeded",
                    ActionId = allowed.ActionId,
                });
            }

            return allowed.ActionId;
        }

        await SaveAsync("a.md", recorded: true);
        var second = await SaveAsync("b.md", recorded: false);
        var third = await SaveAsync("c.md", recorded: true);

        (await desktop.ListWritesAsync(new ListWritesRequest { Limit = 2 })).Entries
            .Select(e => (e.Target, e.Outcome, e.ActionId))
            .ShouldBe([("c.md", "succeeded", third), ("b.md", "started", second)], "two writes, newest first; the third write's started line is not one of them");
    }

    [Fact]
    public async Task RequestWrite_Fails_The_Worker_s_Call_When_The_Started_Entry_Cannot_Be_Written()
    {
        await using var host = await ControlPlaneTestHost.StartAsync(configureServices: services =>
        {
            services.RemoveAll<IWriteLog>();
            services.AddSingleton(sp => FailingCalls.Wrap<IWriteLog>(
                new JsonLinesWriteLog(sp.GetRequiredService<IOptions<FeatureStateStoreOptions>>(), NullLogger<JsonLinesWriteLog>.Instance),
                (method, args) => method.Name == nameof(IWriteLog.AppendAsync) && args?[0] is Scout.ControlPlane.Actions.WriteLogEntry { Outcome: "started" }
                    ? new IOException("disk full")
                    : null));
        });
        using var desktopChannel = host.CreateChannel();
        using var workerChannel = host.CreateWorkerChannel();
        var desktop = new GrpcFeatures.FeaturesClient(desktopChannel);
        await desktop.SetWriteGrantAsync(new SetWriteGrantRequest { Id = FeatureCatalog.YouTubeId, Granted = true, ConfirmWrites = true });

        var failed = await Denied(() => new WorkerGate.WorkerGateClient(workerChannel).RequestWriteAsync(
            new RequestWriteRequest { FeatureId = FeatureCatalog.YouTubeId, Scope = "Scout's data folder", Target = "a.md", Summary = "Save" }).ResponseAsync);

        failed.StatusCode.ShouldBe(StatusCode.Unknown, "a write whose started entry is not in the log is never answered allowed");
        (await desktop.ListWritesAsync(new ListWritesRequest())).Entries.ShouldBeEmpty();
    }
}
