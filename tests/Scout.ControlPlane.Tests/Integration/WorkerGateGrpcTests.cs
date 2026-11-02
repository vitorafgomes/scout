using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
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
        (allowed.Decision, allowed.Reason, allowed.HasActionId).ShouldBe(("allowed", string.Empty, false));
        (undeclared.Decision, undeclared.Reason).ShouldBe(("refused", "videos.delete is not declared by youtube"));
        (read.Decision, read.Reason).ShouldBe(("refused", "videos.list is a read"));
        early.StatusCode.ShouldBe(StatusCode.FailedPrecondition);
        early.Status.Detail.ShouldBe($"Action '{queued.ActionId}' is not approved by the owner.");
        var logged = (await desktop.ListWritesAsync(new ListWritesRequest())).Entries.ShouldHaveSingleItem();
        (logged.FeatureId, logged.Action, logged.Target, logged.Outcome, logged.ApprovedBy, logged.ActionId)
            .ShouldBe(("youtube", "videos.insert", "intro.mp4", "uploaded", "owner", queued.ActionId));
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
        listed.Features.Select(f => f.Id).ShouldBe([FeatureCatalog.GitHubId, FeatureCatalog.KubernetesId, FeatureCatalog.YouTubeId]);
    }
}
