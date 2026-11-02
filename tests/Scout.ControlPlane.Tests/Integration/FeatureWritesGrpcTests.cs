using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Contracts;
using Scout.ControlPlane.Features;
using Shouldly;
using Xunit;
using DomainWriteLogEntry = Scout.ControlPlane.Actions.WriteLogEntry;
using GrpcFeatures = Scout.ControlPlane.Contracts.Features;

namespace Scout.ControlPlane.Tests.Integration;

/// <summary>
/// The write grant, the approval queue and the write log over the real UDS host.
/// </summary>
[Collection("uds")]
public sealed class FeatureWritesGrpcTests
{
    private const string YouTubeWriteRows =
        "Scout's data folder (write), videos.insert (irreversible write), videos.update (irreversible write), " +
        "thumbnails.set (irreversible write), playlistItems.insert (irreversible write), " +
        "comments.insert (irreversible write), comments.setModerationStatus (irreversible write).";

    [Fact]
    public async Task Enable_Without_Confirm_Writes_Answers_FailedPrecondition_Naming_The_Rows()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var channel = host.CreateChannel();
        var client = new GrpcFeatures.FeaturesClient(channel);
        var credentials = new SetCredentialsRequest { Id = FeatureCatalog.YouTubeId };
        credentials.Credentials.Add("api_key", "yt-dummy-key");
        await client.SetCredentialsAsync(credentials);

        var refused = await Should.ThrowAsync<RpcException>(() =>
            client.EnableAsync(new EnableRequest { Id = FeatureCatalog.YouTubeId }).ResponseAsync);

        refused.StatusCode.ShouldBe(StatusCode.FailedPrecondition);
        refused.Status.Detail.ShouldBe("Confirm the writes of youtube before enabling it: " + YouTubeWriteRows);
        var listed = (await client.ListFeaturesAsync(new ListFeaturesRequest())).Features
            .Single(f => f.Id == FeatureCatalog.YouTubeId);
        listed.Enabled.ShouldBeFalse();
        listed.Granted.ShouldBeFalse();
        listed.HasGrantedAt.ShouldBeFalse();
        listed.WriteClass.ShouldBe(2);
        listed.ScopeMatrix.Select(r => r.Access).ShouldBe([0, 0, 0, 0, 0, 0, 1, 2, 2, 2, 2, 2, 2]);
        var github = (await client.ListFeaturesAsync(new ListFeaturesRequest())).Features
            .Single(f => f.Id == FeatureCatalog.GitHubId);
        github.WriteClass.ShouldBe(0);
        github.ScopeMatrix.Select(r => r.Access).Distinct().ShouldBe([0]);

        var enabled = await client.EnableAsync(new EnableRequest { Id = FeatureCatalog.YouTubeId, ConfirmWrites = true });

        enabled.Enabled.ShouldBeTrue();
        enabled.Granted.ShouldBeTrue();
    }

    [Fact]
    public async Task SetWriteGrant_Needs_Confirm_Writes_And_Round_Trips_The_Grant_Over_Grpc()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var channel = host.CreateChannel();
        var client = new GrpcFeatures.FeaturesClient(channel);

        var refused = await Should.ThrowAsync<RpcException>(() =>
            client.SetWriteGrantAsync(new SetWriteGrantRequest { Id = FeatureCatalog.YouTubeId, Granted = true }).ResponseAsync);
        var noWrites = await Should.ThrowAsync<RpcException>(() =>
            client.SetWriteGrantAsync(new SetWriteGrantRequest
            {
                Id = FeatureCatalog.GitHubId,
                Granted = true,
                ConfirmWrites = true,
            }).ResponseAsync);
        var granted = await client.SetWriteGrantAsync(new SetWriteGrantRequest
        {
            Id = FeatureCatalog.YouTubeId,
            Granted = true,
            ConfirmWrites = true,
        });
        var revoked = await client.SetWriteGrantAsync(new SetWriteGrantRequest { Id = FeatureCatalog.YouTubeId, Granted = false });

        refused.StatusCode.ShouldBe(StatusCode.FailedPrecondition);
        refused.Status.Detail.ShouldBe("Confirm the writes of youtube before granting them: " + YouTubeWriteRows);
        noWrites.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        noWrites.Status.Detail.ShouldBe("granted: github declares no writes to grant.");
        granted.Granted.ShouldBeTrue();
        granted.Enabled.ShouldBeFalse();
        DateTimeOffset.Parse(granted.GrantedAt, System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBeInRange(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow);
        revoked.Granted.ShouldBeFalse();
        revoked.HasGrantedAt.ShouldBeFalse();
    }

    [Fact]
    public async Task Pending_Actions_And_Writes_Round_Trip_Over_Grpc()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var queue = host.Services.GetRequiredService<IActionQueue>();
        var log = host.Services.GetRequiredService<IWriteLog>();
        var upload = await queue.EnqueueAsync(
            "youtube-publish", "videos.insert", "intro.mp4", "Upload 'Intro' as private", CancellationToken.None);
        var reply = await queue.EnqueueAsync(
            "youtube-engage", "comments.insert", "comment 7", "Reply 'Thanks!'", CancellationToken.None);
        var at = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        await log.AppendAsync(
            new DomainWriteLogEntry(at, "youtube-production", "Scripts", "intro.md", "ok", null, null),
            CancellationToken.None);
        await log.AppendAsync(
            new DomainWriteLogEntry(at.AddMinutes(1), "youtube-publish", "videos.insert", "intro.mp4", "ok", "owner", upload.Id),
            CancellationToken.None);
        using var channel = host.CreateChannel();
        var client = new GrpcFeatures.FeaturesClient(channel);

        var pending = await client.ListPendingActionsAsync(new ListPendingActionsRequest());
        var approved = await client.ApproveActionAsync(new ActionDecisionRequest { Id = upload.Id });
        var rejected = await client.RejectActionAsync(new ActionDecisionRequest { Id = reply.Id, Reason = "Wrong tone" });
        var afterDecisions = await client.ListPendingActionsAsync(new ListPendingActionsRequest());
        var again = await Should.ThrowAsync<RpcException>(() =>
            client.ApproveActionAsync(new ActionDecisionRequest { Id = upload.Id }).ResponseAsync);
        var unknown = await Should.ThrowAsync<RpcException>(() =>
            client.RejectActionAsync(new ActionDecisionRequest { Id = "no-such-action" }).ResponseAsync);
        var newest = await client.ListWritesAsync(new ListWritesRequest { Limit = 1 });
        var defaulted = await client.ListWritesAsync(new ListWritesRequest());

        pending.Actions.Select(a => (a.Id, a.FeatureId, a.Action, a.Target, a.Summary, a.Status, a.HasDecidedAt)).ShouldBe([
            (upload.Id, "youtube-publish", "videos.insert", "intro.mp4", "Upload 'Intro' as private", "pending", false),
            (reply.Id, "youtube-engage", "comments.insert", "comment 7", "Reply 'Thanks!'", "pending", false),
        ]);
        DateTimeOffset.Parse(pending.Actions[0].RequestedAt, System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBe(upload.RequestedAt);
        approved.Status.ShouldBe("approved");
        approved.HasDecidedAt.ShouldBeTrue();
        rejected.Status.ShouldBe("rejected");
        rejected.HasDecidedAt.ShouldBeTrue();
        afterDecisions.Actions.ShouldBeEmpty();
        again.StatusCode.ShouldBe(StatusCode.FailedPrecondition);
        unknown.StatusCode.ShouldBe(StatusCode.NotFound);
        var only = newest.Entries.ShouldHaveSingleItem();
        (only.FeatureId, only.Action, only.Target, only.Outcome, only.ApprovedBy, only.ActionId)
            .ShouldBe(("youtube-publish", "videos.insert", "intro.mp4", "ok", "owner", upload.Id));
        DateTimeOffset.Parse(only.At, System.Globalization.CultureInfo.InvariantCulture).ShouldBe(at.AddMinutes(1));
        defaulted.Entries.Select(e => (e.Action, e.HasApprovedBy, e.HasActionId))
            .ShouldBe([("videos.insert", true, true), ("Scripts", false, false)]);
    }

    [Fact]
    public async Task The_Reason_The_Owner_Gives_On_Approve_Is_Stored_And_Returned()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var queue = host.Services.GetRequiredService<IActionQueue>();
        var upload = await queue.EnqueueAsync(
            "youtube", "videos.insert", "intro.mp4", "Upload 'Intro' as private", CancellationToken.None);
        using var channel = host.CreateChannel();
        var client = new GrpcFeatures.FeaturesClient(channel);

        var approved = await client.ApproveActionAsync(new ActionDecisionRequest { Id = upload.Id, Reason = "Checked the title" });

        approved.Reason.ShouldBe("Checked the title");
        (await queue.FindAsync(upload.Id, CancellationToken.None)).ShouldNotBeNull().Reason.ShouldBe("Checked the title");
    }

    [Fact]
    public void An_Executed_Action_Shows_Executed_On_The_Wire()
    {
        var executed = new Scout.ControlPlane.Actions.PendingAction(
            "act-1", "youtube", "videos.insert", "intro.mp4", "Upload", DateTimeOffset.UtcNow,
            PendingActionStatus.Executed, DateTimeOffset.UtcNow);

        Scout.Host.ControlPlane.Services.FeatureMapping.ToWire(executed).Status.ShouldBe("executed");
    }

    [Fact]
    public async Task The_New_Rpcs_Refuse_A_Call_Without_The_Session_Token()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var upload = await host.Services.GetRequiredService<IActionQueue>().EnqueueAsync(
            "youtube-publish", "videos.insert", "intro.mp4", "Upload", CancellationToken.None);
        using var channel = host.CreateChannel(withToken: false);
        var client = new GrpcFeatures.FeaturesClient(channel);

        var list = await Should.ThrowAsync<RpcException>(() =>
            client.ListPendingActionsAsync(new ListPendingActionsRequest()).ResponseAsync);
        var approve = await Should.ThrowAsync<RpcException>(() =>
            client.ApproveActionAsync(new ActionDecisionRequest { Id = upload.Id }).ResponseAsync);
        var writes = await Should.ThrowAsync<RpcException>(() =>
            client.ListWritesAsync(new ListWritesRequest()).ResponseAsync);

        list.StatusCode.ShouldBe(StatusCode.Unauthenticated);
        approve.StatusCode.ShouldBe(StatusCode.Unauthenticated);
        writes.StatusCode.ShouldBe(StatusCode.Unauthenticated);
        (await host.Services.GetRequiredService<IActionQueue>().ListPendingAsync(CancellationToken.None))
            .ShouldHaveSingleItem().Status.ShouldBe(PendingActionStatus.Pending);
    }
}
