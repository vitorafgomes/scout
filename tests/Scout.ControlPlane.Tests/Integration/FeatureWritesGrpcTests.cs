using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
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
        "Scout's data folder (write), youtubeReporting jobs.create (write), videos.insert (irreversible write), videos.update (irreversible write), " +
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
        listed.ScopeMatrix.Select(r => r.Access).ShouldBe([0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 2, 2, 2, 2]);
        var github = (await client.ListFeaturesAsync(new ListFeaturesRequest())).Features
            .Single(f => f.Id == FeatureCatalog.GitHubId);
        github.WriteClass.ShouldBe(2);
        github.ScopeMatrix.Select(r => r.Access).ShouldBe([0, 0, 0, 0, 0, 1, 2, 2, 2, 2, 2, 1, 1]);
        var kubernetes = (await client.ListFeaturesAsync(new ListFeaturesRequest())).Features
            .Single(f => f.Id == FeatureCatalog.KubernetesId);
        kubernetes.WriteClass.ShouldBe(0);
        kubernetes.ScopeMatrix.Select(r => (r.Scope, r.Access)).ShouldBe([("Namespaces", 0), ("Pods", 0), ("Deployments", 0), ("Events", 0)]);
        github.ScopeMatrix.Where(r => r.Scope is "Contents" or "Notifications").Select(r => (r.Scope, r.Summary, r.Actions.Count))
            .ShouldBe([
                ("Contents", "Read files, directories and languages; list and search every file to audit a project", 3),
                ("Notifications", "Search every five minutes for your mentions, review requests and replies; read the pull requests that mention your Scout bot and the bot's own name", 3),
            ]);

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
                Id = FeatureCatalog.KubernetesId,
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
        noWrites.Status.Detail.ShouldBe("granted: kubernetes declares no writes to grant.");
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
        using var channel = host.CreateChannel();
        var client = new GrpcFeatures.FeaturesClient(channel);
        await client.SetWriteGrantAsync(new SetWriteGrantRequest { Id = FeatureCatalog.YouTubeId, Granted = true, ConfirmWrites = true });
        var upload = await queue.EnqueueAsync(
            FeatureCatalog.YouTubeId, "videos.insert", "intro.mp4", "Upload 'Intro' as private", CancellationToken.None);
        var reply = await queue.EnqueueAsync(
            FeatureCatalog.YouTubeId, "comments.insert", "comment 7", "Reply 'Thanks!'", CancellationToken.None);
        var at = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        await log.AppendAsync(
            new DomainWriteLogEntry(at, "youtube-production", "Scripts", "intro.md", "ok", null, null),
            CancellationToken.None);
        await log.AppendAsync(
            new DomainWriteLogEntry(at.AddMinutes(1), FeatureCatalog.YouTubeId, "videos.insert", "intro.mp4", "ok", "owner", upload.Id),
            CancellationToken.None);

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
            (upload.Id, "youtube", "videos.insert", "intro.mp4", "Upload 'Intro' as private", "pending", false),
            (reply.Id, "youtube", "comments.insert", "comment 7", "Reply 'Thanks!'", "pending", false),
        ]);
        DateTimeOffset.Parse(pending.Actions[0].RequestedAt, System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBe(upload.RequestedAt);
        approved.Status.ShouldBe("approved");
        approved.HasDecidedAt.ShouldBeTrue();
        rejected.Status.ShouldBe("rejected");
        rejected.HasDecidedAt.ShouldBeTrue();
        afterDecisions.Actions.Select(a => (a.Id, a.Status)).ShouldBe([(upload.Id, "approved")]);
        again.StatusCode.ShouldBe(StatusCode.FailedPrecondition);
        unknown.StatusCode.ShouldBe(StatusCode.NotFound);
        var only = newest.Entries.ShouldHaveSingleItem();
        (only.FeatureId, only.Action, only.Target, only.Outcome, only.ApprovedBy, only.ActionId)
            .ShouldBe(("youtube", "comments.insert", "comment 7", "rejected: Wrong tone", "owner", reply.Id));
        defaulted.Entries.Select(e => (e.Action, e.Outcome, e.HasApprovedBy, e.HasActionId)).ShouldBe([
            ("comments.insert", "rejected: Wrong tone", true, true),
            ("videos.insert", "approved", true, true),
            ("videos.insert", "ok", true, true),
            ("Scripts", "ok", false, false),
        ]);
        DateTimeOffset.Parse(defaulted.Entries[2].At, System.Globalization.CultureInfo.InvariantCulture).ShouldBe(at.AddMinutes(1));
    }

    [Fact]
    public async Task The_Reason_The_Owner_Gives_On_Approve_Is_Stored_Returned_And_Logged_After_Approved()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var queue = host.Services.GetRequiredService<IActionQueue>();
        using var channel = host.CreateChannel();
        var client = new GrpcFeatures.FeaturesClient(channel);
        await client.SetWriteGrantAsync(new SetWriteGrantRequest { Id = FeatureCatalog.YouTubeId, Granted = true, ConfirmWrites = true });
        var upload = await queue.EnqueueAsync(
            FeatureCatalog.YouTubeId, "videos.insert", "intro.mp4", "Upload 'Intro' as private", CancellationToken.None);

        var approved = await client.ApproveActionAsync(new ActionDecisionRequest { Id = upload.Id, Reason = "Checked the title" });

        approved.Reason.ShouldBe("Checked the title");
        (await queue.FindAsync(upload.Id, CancellationToken.None)).ShouldNotBeNull().Reason.ShouldBe("Checked the title");
        (await client.ListWritesAsync(new ListWritesRequest { Limit = 1 })).Entries.ShouldHaveSingleItem().Outcome
            .ShouldBe("approved: Checked the title");
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("disable")]
    [InlineData("delete credentials")]
    [InlineData("enable failure")]
    public async Task Every_Call_That_Clears_The_Write_Grant_Rejects_The_Waiting_And_Approved_Unclaimed_Actions_With_The_Reason_Writes_Were_Revoked(string clearing)
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var queue = host.Services.GetRequiredService<IActionQueue>();
        using var channel = host.CreateChannel();
        var client = new GrpcFeatures.FeaturesClient(channel);
        var credentials = new SetCredentialsRequest { Id = FeatureCatalog.YouTubeId };
        credentials.Credentials.Add("api_key", "yt-dummy-key");
        await client.SetCredentialsAsync(credentials);
        await client.EnableAsync(new EnableRequest { Id = FeatureCatalog.YouTubeId, ConfirmWrites = true });
        var upload = await queue.EnqueueAsync(
            FeatureCatalog.YouTubeId, "videos.insert", "intro.mp4", "Upload 'Intro' as private", CancellationToken.None);
        var retitle = await queue.EnqueueAsync(
            FeatureCatalog.YouTubeId, "videos.update", "intro.mp4", "Retitle 'Intro'", CancellationToken.None);
        await client.ApproveActionAsync(new ActionDecisionRequest { Id = retitle.Id });
        var otherFeature = await queue.EnqueueAsync(
            FeatureCatalog.GitHubId, "pulls.reviews.create", "octocat/hello#42", "Approve #42", CancellationToken.None);

        await ClearGrantAsync(host, client, clearing);

        var waited = (await queue.FindAsync(upload.Id, CancellationToken.None)).ShouldNotBeNull();
        (waited.Status, waited.Reason).ShouldBe((PendingActionStatus.Rejected, "writes were revoked"));
        var approved = (await queue.FindAsync(retitle.Id, CancellationToken.None)).ShouldNotBeNull();
        approved.Status.ShouldBe(PendingActionStatus.Rejected);
        approved.Reason.ShouldBeOneOf("writes were revoked", Scout.ControlPlane.Operator.OperatorService.RevokedReason);

        (await queue.FindAsync(otherFeature.Id, CancellationToken.None)).ShouldNotBeNull().Status.ShouldBe(PendingActionStatus.Pending);
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("disable")]
    [InlineData("delete credentials")]
    public async Task Clearing_The_GitHub_Grant_Rejects_An_Approved_Action_No_Worker_Claimed_With_The_Reason_Writes_Were_Revoked(string clearing)
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var queue = host.Services.GetRequiredService<IActionQueue>();
        using var channel = host.CreateChannel();
        var client = new GrpcFeatures.FeaturesClient(channel);
        await client.SetWriteGrantAsync(new SetWriteGrantRequest { Id = FeatureCatalog.GitHubId, Granted = true, ConfirmWrites = true });
        var review = await queue.EnqueueAsync(
            FeatureCatalog.GitHubId, "pulls.reviews.create", "octocat/hello#42", "Approve #42", CancellationToken.None);
        await client.ApproveActionAsync(new ActionDecisionRequest { Id = review.Id });

        _ = clearing switch
        {
            "revoke" => await client.SetWriteGrantAsync(new SetWriteGrantRequest { Id = FeatureCatalog.GitHubId, Granted = false }),
            "disable" => await client.DisableAsync(new DisableRequest { Id = FeatureCatalog.GitHubId }),
            _ => await client.DeleteCredentialsAsync(new DeleteCredentialsRequest { Id = FeatureCatalog.GitHubId }),
        };

        var after = (await queue.FindAsync(review.Id, CancellationToken.None)).ShouldNotBeNull();
        (after.Status, after.Reason).ShouldBe((PendingActionStatus.Rejected, "writes were revoked"));
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("disable")]
    [InlineData("delete credentials")]
    public async Task A_Queue_That_Cannot_Be_Written_Fails_The_Call_That_Clears_The_Grant_And_Leaves_The_Grant_In_Place(string clearing)
    {
        var queue = Substitute.For<IActionQueue>();
        queue.RejectUnstartedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("disk full"));
        await using var host = await ControlPlaneTestHost.StartAsync(
            configureServices: services =>
            {
                services.RemoveAll<IActionQueue>();
                services.AddSingleton(queue);
            });
        using var channel = host.CreateChannel();
        var client = new GrpcFeatures.FeaturesClient(channel);
        await client.SetWriteGrantAsync(new SetWriteGrantRequest { Id = FeatureCatalog.GitHubId, Granted = true, ConfirmWrites = true });

        var failed = await Should.ThrowAsync<RpcException>(() => clearing switch
        {
            "revoke" => client.SetWriteGrantAsync(new SetWriteGrantRequest { Id = FeatureCatalog.GitHubId, Granted = false }).ResponseAsync,
            "disable" => client.DisableAsync(new DisableRequest { Id = FeatureCatalog.GitHubId }).ResponseAsync,
            _ => client.DeleteCredentialsAsync(new DeleteCredentialsRequest { Id = FeatureCatalog.GitHubId }).ResponseAsync,
        });

        failed.StatusCode.ShouldNotBe(StatusCode.OK);
        (await client.ListFeaturesAsync(new ListFeaturesRequest())).Features
            .Single(f => f.Id == FeatureCatalog.GitHubId).Granted.ShouldBeTrue();
    }

    [Fact]
    public async Task An_Enable_Whose_Activation_Fails_Answers_The_Activation_Error_Even_When_The_Queue_Cannot_Be_Written()
    {
        var queue = Substitute.For<IActionQueue>();
        queue.RejectUnstartedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("disk full"));
        await using var host = await ControlPlaneTestHost.StartAsync(
            configureServices: services =>
            {
                services.RemoveAll<IActionQueue>();
                services.AddSingleton(queue);
            });
        using var channel = host.CreateChannel();
        var client = new GrpcFeatures.FeaturesClient(channel);
        var credentials = new SetCredentialsRequest { Id = FeatureCatalog.YouTubeId };
        credentials.Credentials.Add("api_key", "yt-dummy-key");
        await client.SetCredentialsAsync(credentials);
        ((StubFeatureActivator)host.Services.GetRequiredService<IFeatureActivator>()).FailWith = "boom";

        var failed = await Should.ThrowAsync<RpcException>(() =>
            client.EnableAsync(new EnableRequest { Id = FeatureCatalog.YouTubeId, ConfirmWrites = true }).ResponseAsync);

        (failed.StatusCode, failed.Status.Detail).ShouldBe((StatusCode.Internal, "boom"));
    }

    [Fact]
    public async Task Granting_Writes_Leaves_The_Actions_Waiting_In_Approvals_As_They_Are()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var queue = host.Services.GetRequiredService<IActionQueue>();
        using var channel = host.CreateChannel();
        var client = new GrpcFeatures.FeaturesClient(channel);
        var upload = await queue.EnqueueAsync(
            FeatureCatalog.YouTubeId, "videos.insert", "intro.mp4", "Upload 'Intro' as private", CancellationToken.None);

        await client.SetWriteGrantAsync(new SetWriteGrantRequest { Id = FeatureCatalog.YouTubeId, Granted = true, ConfirmWrites = true });

        (await queue.FindAsync(upload.Id, CancellationToken.None)).ShouldNotBeNull().Status.ShouldBe(PendingActionStatus.Pending);
    }

    private static async Task ClearGrantAsync(ControlPlaneTestHost host, GrpcFeatures.FeaturesClient client, string clearing)
    {
        switch (clearing)
        {
            case "revoke":
                await client.SetWriteGrantAsync(new SetWriteGrantRequest { Id = FeatureCatalog.YouTubeId, Granted = false });
                break;
            case "disable":
                await client.DisableAsync(new DisableRequest { Id = FeatureCatalog.YouTubeId });
                break;
            case "delete credentials":
                await client.DeleteCredentialsAsync(new DeleteCredentialsRequest { Id = FeatureCatalog.YouTubeId });
                break;
            default:
                ((StubFeatureActivator)host.Services.GetRequiredService<IFeatureActivator>()).FailWith = "boom";
                await Should.ThrowAsync<RpcException>(() =>
                    client.EnableAsync(new EnableRequest { Id = FeatureCatalog.YouTubeId, ConfirmWrites = true }).ResponseAsync);
                break;
        }
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
