using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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
using QueuedAction = Scout.ControlPlane.Actions.PendingAction;

namespace Scout.ControlPlane.Tests.Integration;

/// <summary>
/// What the owner decides on the Approvals screen, over the real UDS host: whether a decision is accepted, what History
/// keeps of it, and what becomes of an approved action once the GitHub grant moves under it.
/// </summary>
[Collection("uds")]
public sealed class ApprovalsGrpcTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private const string Verdicts = "Pull request verdicts";
    private const string NeverFinished = "Scout was running it when writes were revoked and it never finished; ask again";
    private static readonly JsonSerializerOptions QueueFile = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private const string MayAlreadyBeOnGitHub = "Scout stopped before it recorded this, so it may already be on GitHub: look there before asking for it again.";

    private static GiveVerdictRequest Verdict(int number) => new()
    {
        Repository = "octocat/hello",
        Number = number,
        Verdict = "approve",
        Body = "Ready to merge.",
        CommentsJson = "[]",
        Commit = Sha,
        TestsLine = "Tests: 3 checks passed.",
    };

    private static Task<Feature> GrantGitHubAsync(GrpcFeatures.FeaturesClient features) =>
        features.SetWriteGrantAsync(new SetWriteGrantRequest { Id = FeatureCatalog.GitHubId, Granted = true, ConfirmWrites = true }).ResponseAsync;

    private static async Task EnableGitHubAgainAsync(GrpcFeatures.FeaturesClient features)
    {
        var credentials = new SetCredentialsRequest { Id = FeatureCatalog.GitHubId };
        credentials.Credentials.Add("pat", "ghp_dummy_token_value");
        await features.SetCredentialsAsync(credentials);
        (await features.EnableAsync(new EnableRequest { Id = FeatureCatalog.GitHubId, ConfirmWrites = true })).Granted.ShouldBeTrue();
    }

    [Fact]
    public async Task An_Action_Waiting_When_GitHub_Writes_Are_Revoked_And_Granted_Again_Cannot_Be_Approved()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var queue = host.Services.GetRequiredService<IActionQueue>();
        using var channel = host.CreateChannel();
        var features = new GrpcFeatures.FeaturesClient(channel);
        await GrantGitHubAsync(features);
        var waiting = await queue.EnqueueAsync(FeatureCatalog.GitHubId, Verdicts, "octocat/hello#42", "Approve octocat/hello#42", CancellationToken.None);
        await features.SetWriteGrantAsync(new SetWriteGrantRequest { Id = FeatureCatalog.GitHubId, Granted = false });
        await GrantGitHubAsync(features);

        var refused = await Should.ThrowAsync<RpcException>(() =>
            features.ApproveActionAsync(new ActionDecisionRequest { Id = waiting.Id }).ResponseAsync);

        refused.StatusCode.ShouldBe(StatusCode.FailedPrecondition);
        (await queue.FindAsync(waiting.Id, CancellationToken.None)).ShouldNotBeNull().Status.ShouldBe(PendingActionStatus.Rejected);
    }

    [Fact]
    public async Task Approving_An_Action_Of_A_Feature_Whose_Writes_The_Owner_Has_Not_Granted_Is_Refused()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var queue = host.Services.GetRequiredService<IActionQueue>();
        using var channel = host.CreateChannel();
        var features = new GrpcFeatures.FeaturesClient(channel);
        var waiting = await queue.EnqueueAsync(FeatureCatalog.GitHubId, Verdicts, "octocat/hello#42", "Approve octocat/hello#42", CancellationToken.None);
        (await features.ListFeaturesAsync(new ListFeaturesRequest())).Features.Single(f => f.Id == FeatureCatalog.GitHubId).Granted.ShouldBeFalse();

        var refused = await Should.ThrowAsync<RpcException>(() =>
            features.ApproveActionAsync(new ActionDecisionRequest { Id = waiting.Id }).ResponseAsync);

        refused.StatusCode.ShouldBe(StatusCode.FailedPrecondition);
        (await queue.FindAsync(waiting.Id, CancellationToken.None)).ShouldNotBeNull().Status.ShouldNotBe(PendingActionStatus.Approved);
    }

    [Fact]
    public async Task The_Owner_Rejecting_An_Action_With_A_Reason_Is_Logged_In_History_With_The_Reason()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var queue = host.Services.GetRequiredService<IActionQueue>();
        using var channel = host.CreateChannel();
        var features = new GrpcFeatures.FeaturesClient(channel);
        await GrantGitHubAsync(features);
        var waiting = await queue.EnqueueAsync(FeatureCatalog.GitHubId, Verdicts, "octocat/hello#42", "Approve octocat/hello#42", CancellationToken.None);
        (await features.RejectActionAsync(new ActionDecisionRequest { Id = waiting.Id, Reason = "Wrong tone" })).Status.ShouldBe("rejected");

        var newest = await features.ListWritesAsync(new ListWritesRequest { Limit = 1 });

        newest.Entries.Select(e => (e.FeatureId, e.Action, e.Target, e.Outcome, e.ApprovedBy, e.ActionId))
            .ShouldBe([(FeatureCatalog.GitHubId, Verdicts, "octocat/hello#42", "rejected: Wrong tone", "owner", waiting.Id)]);
    }

    [Fact]
    public async Task The_Owner_Approving_An_Action_Is_Logged_In_History_As_Approved_By_The_Owner()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var queue = host.Services.GetRequiredService<IActionQueue>();
        using var channel = host.CreateChannel();
        var features = new GrpcFeatures.FeaturesClient(channel);
        await GrantGitHubAsync(features);
        var waiting = await queue.EnqueueAsync(FeatureCatalog.GitHubId, Verdicts, "octocat/hello#42", "Approve octocat/hello#42", CancellationToken.None);
        (await features.ApproveActionAsync(new ActionDecisionRequest { Id = waiting.Id })).Status.ShouldBe("approved");

        var newest = await features.ListWritesAsync(new ListWritesRequest { Limit = 1 });

        newest.Entries.Select(e => (e.FeatureId, e.Action, e.Target, e.Outcome, e.ApprovedBy, e.ActionId))
            .ShouldBe([(FeatureCatalog.GitHubId, Verdicts, "octocat/hello#42", "approved", "owner", waiting.Id)]);
    }

    [Fact]
    public async Task An_Action_Approved_And_Not_Yet_Claimed_By_A_Worker_Is_Still_Listed_As_Approved()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var queue = host.Services.GetRequiredService<IActionQueue>();
        using var channel = host.CreateChannel();
        var features = new GrpcFeatures.FeaturesClient(channel);
        await GrantGitHubAsync(features);
        var waiting = await queue.EnqueueAsync(FeatureCatalog.GitHubId, Verdicts, "octocat/hello#42", "Approve octocat/hello#42", CancellationToken.None);
        (await features.ApproveActionAsync(new ActionDecisionRequest { Id = waiting.Id })).Status.ShouldBe("approved");

        var listed = await features.ListPendingActionsAsync(new ListPendingActionsRequest());

        listed.Actions.Select(a => (a.Id, a.Status)).ShouldBe([(waiting.Id, "approved")]);
    }

    [Fact]
    public async Task An_Action_A_Worker_Claimed_Before_GitHub_Writes_Were_Revoked_And_Granted_Again_Is_Rejected_And_Logged_Once_Its_Claim_Lapses()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var queue = host.Services.GetRequiredService<IActionQueue>();
        using var desktopChannel = host.CreateChannel();
        using var workerChannel = host.CreateWorkerChannel();
        var features = new GrpcFeatures.FeaturesClient(desktopChannel);
        var worker = new WorkerGate.WorkerGateClient(workerChannel);
        await GrantGitHubAsync(features);
        var given = await features.GiveVerdictAsync(Verdict(42));
        (await worker.NextApprovedActionAsync(new NextApprovedActionRequest { FeatureId = FeatureCatalog.GitHubId })).Action.Id.ShouldBe(given.ActionId);
        await features.SetWriteGrantAsync(new SetWriteGrantRequest { Id = FeatureCatalog.GitHubId, Granted = false });
        await GrantGitHubAsync(features);
        await LapseClaimAsync((JsonFileActionQueue)queue, given.ActionId);
        (await queue.FindAsync(given.ActionId, CancellationToken.None)).ShouldNotBeNull().ClaimedAt.ShouldNotBeNull()
            .ShouldBeLessThan(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(30));

        await worker.NextApprovedActionAsync(new NextApprovedActionRequest { FeatureId = FeatureCatalog.GitHubId });

        var after = (await queue.FindAsync(given.ActionId, CancellationToken.None)).ShouldNotBeNull();
        (after.Status, after.Reason).ShouldBe((PendingActionStatus.Rejected, MayAlreadyBeOnGitHub));
        (await features.ListWritesAsync(new ListWritesRequest())).Entries
            .Where(e => e.ActionId == given.ActionId && e.Outcome != "approved")
            .Select(e => (e.Outcome, e.ApprovedBy))
            .ShouldBe([($"rejected: {MayAlreadyBeOnGitHub}", "owner")]);
    }

    [Fact]
    public async Task An_Action_A_Worker_Claimed_Before_GitHub_Writes_Were_Revoked_Is_Rejected_And_Logged_Once_Its_Claim_Lapses_Even_With_No_Grant_At_All()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var queue = host.Services.GetRequiredService<IActionQueue>();
        using var desktopChannel = host.CreateChannel();
        using var workerChannel = host.CreateWorkerChannel();
        var features = new GrpcFeatures.FeaturesClient(desktopChannel);
        var worker = new WorkerGate.WorkerGateClient(workerChannel);
        await GrantGitHubAsync(features);
        var given = await features.GiveVerdictAsync(Verdict(42));
        (await worker.NextApprovedActionAsync(new NextApprovedActionRequest { FeatureId = FeatureCatalog.GitHubId })).Action.Id.ShouldBe(given.ActionId);
        await features.SetWriteGrantAsync(new SetWriteGrantRequest { Id = FeatureCatalog.GitHubId, Granted = false });
        await LapseClaimAsync((JsonFileActionQueue)queue, given.ActionId);

        var polled = await worker.NextApprovedActionAsync(new NextApprovedActionRequest { FeatureId = FeatureCatalog.GitHubId });

        polled.Action.ShouldBeNull();
        var after = (await queue.FindAsync(given.ActionId, CancellationToken.None)).ShouldNotBeNull();
        (after.Status, after.Reason).ShouldBe((PendingActionStatus.Rejected, MayAlreadyBeOnGitHub));
        (await features.ListPendingActionsAsync(new ListPendingActionsRequest())).Actions.ShouldNotContain(a => a.Id == given.ActionId);
        (await features.ListWritesAsync(new ListWritesRequest())).Entries
            .Where(e => e.ActionId == given.ActionId && e.Outcome != "approved")
            .Select(e => (e.Outcome, e.ApprovedBy))
            .ShouldBe([($"rejected: {MayAlreadyBeOnGitHub}", "owner")]);
    }

    [Fact]
    public async Task A_Claimed_Action_Whose_Claim_Has_Not_Lapsed_Is_Left_Running_After_The_Owner_Revoked()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var queue = host.Services.GetRequiredService<IActionQueue>();
        using var desktopChannel = host.CreateChannel();
        using var workerChannel = host.CreateWorkerChannel();
        var features = new GrpcFeatures.FeaturesClient(desktopChannel);
        var worker = new WorkerGate.WorkerGateClient(workerChannel);
        await GrantGitHubAsync(features);
        var given = await features.GiveVerdictAsync(Verdict(42));
        (await worker.NextApprovedActionAsync(new NextApprovedActionRequest { FeatureId = FeatureCatalog.GitHubId })).Action.Id.ShouldBe(given.ActionId);
        await features.SetWriteGrantAsync(new SetWriteGrantRequest { Id = FeatureCatalog.GitHubId, Granted = false });

        await worker.NextApprovedActionAsync(new NextApprovedActionRequest { FeatureId = FeatureCatalog.GitHubId });

        (await queue.FindAsync(given.ActionId, CancellationToken.None)).ShouldNotBeNull().Status.ShouldBe(PendingActionStatus.Executing);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_YouTube_Action_A_Worker_Claimed_Before_Writes_Were_Revoked_Is_Rejected_And_Logged_Once_Its_Claim_Lapses(bool grantedAgain)
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var queue = host.Services.GetRequiredService<IActionQueue>();
        using var desktopChannel = host.CreateChannel();
        using var workerChannel = host.CreateWorkerChannel();
        var features = new GrpcFeatures.FeaturesClient(desktopChannel);
        var worker = new WorkerGate.WorkerGateClient(workerChannel);
        var upload = await ClaimedYouTubeUploadAsync(host.Services, features, worker);
        await features.SetWriteGrantAsync(new SetWriteGrantRequest { Id = FeatureCatalog.YouTubeId, Granted = false });
        if (grantedAgain)
        {
            await GrantYouTubeAsync(features);
        }

        await LapseClaimAsync((JsonFileActionQueue)queue, upload.Id);

        var polled = await worker.NextApprovedActionAsync(new NextApprovedActionRequest());

        polled.Action.ShouldBeNull();
        var after = (await queue.FindAsync(upload.Id, CancellationToken.None)).ShouldNotBeNull();
        (after.Status, after.Reason).ShouldBe((PendingActionStatus.Rejected, NeverFinished));
        (await features.ListWritesAsync(new ListWritesRequest())).Entries
            .Where(e => e.ActionId == upload.Id && e.Outcome != "approved")
            .Select(e => (e.Outcome, e.ApprovedBy))
            .ShouldBe([($"rejected: {NeverFinished}", "owner")]);
    }

    [Fact]
    public async Task A_YouTube_Action_Whose_Claim_Has_Not_Lapsed_Is_Left_Running_After_The_Owner_Revoked()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var queue = host.Services.GetRequiredService<IActionQueue>();
        using var desktopChannel = host.CreateChannel();
        using var workerChannel = host.CreateWorkerChannel();
        var features = new GrpcFeatures.FeaturesClient(desktopChannel);
        var worker = new WorkerGate.WorkerGateClient(workerChannel);
        var upload = await ClaimedYouTubeUploadAsync(host.Services, features, worker);
        await features.SetWriteGrantAsync(new SetWriteGrantRequest { Id = FeatureCatalog.YouTubeId, Granted = false });

        await worker.NextApprovedActionAsync(new NextApprovedActionRequest());

        (await queue.FindAsync(upload.Id, CancellationToken.None)).ShouldNotBeNull().Status.ShouldBe(PendingActionStatus.Executing);
    }

    [Theory]
    [InlineData("queue", "grant read")]
    [InlineData("queue", "claim")]
    [InlineData("log", "grant read")]
    [InlineData("log", "claim")]
    public async Task A_Rejection_The_Queue_Or_The_Log_Cannot_Write_Never_Answers_Granted_And_Hands_Out_No_Action(string failing, string call)
    {
        var armed = false;
        await using var host = await ControlPlaneTestHost.StartAsync(configureServices: services =>
        {
            services.RemoveAll<IActionQueue>();
            services.AddSingleton(sp => FailingCalls.Wrap<IActionQueue>(
                new JsonFileActionQueue(sp.GetRequiredService<IOptions<FeatureStateStoreOptions>>(), NullLogger<JsonFileActionQueue>.Instance),
                (method, _) => armed && failing == "queue" && method.Name == nameof(IActionQueue.RejectExecutingAsync) ? new IOException("disk full") : null));
            services.RemoveAll<IWriteLog>();
            services.AddSingleton(sp => FailingCalls.Wrap<IWriteLog>(
                new JsonLinesWriteLog(sp.GetRequiredService<IOptions<FeatureStateStoreOptions>>(), NullLogger<JsonLinesWriteLog>.Instance),
                (method, args) => armed && failing == "log" && method.Name == nameof(IWriteLog.AppendAsync)
                    && ((Scout.ControlPlane.Actions.WriteLogEntry)args![0]!).Outcome.StartsWith("rejected", StringComparison.Ordinal)
                    ? new IOException("disk full")
                    : null));
        });
        var queue = host.Services.GetRequiredService<IActionQueue>();
        using var desktopChannel = host.CreateChannel();
        using var workerChannel = host.CreateWorkerChannel();
        var features = new GrpcFeatures.FeaturesClient(desktopChannel);
        var worker = new WorkerGate.WorkerGateClient(workerChannel);
        await GrantGitHubAsync(features);
        var given = await features.GiveVerdictAsync(Verdict(42));
        (await worker.NextApprovedActionAsync(new NextApprovedActionRequest { FeatureId = FeatureCatalog.GitHubId })).Action.Id.ShouldBe(given.ActionId);
        await features.SetWriteGrantAsync(new SetWriteGrantRequest { Id = FeatureCatalog.GitHubId, Granted = false });
        if (call == "claim")
        {
            using var file = new JsonFileActionQueue(
                host.Services.GetRequiredService<IOptions<FeatureStateStoreOptions>>(), NullLogger<JsonFileActionQueue>.Instance);
            await LapseClaimAsync(file, given.ActionId);
        }

        armed = true;
        var failed = await Should.ThrowAsync<RpcException>(() => call == "grant read"
            ? worker.CheckActionGrantAsync(new CheckActionGrantRequest { FeatureId = FeatureCatalog.GitHubId, ActionId = given.ActionId }).ResponseAsync
            : (Task)worker.NextApprovedActionAsync(new NextApprovedActionRequest { FeatureId = FeatureCatalog.GitHubId }).ResponseAsync);

        failed.StatusCode.ShouldNotBe(StatusCode.OK);
        var left = (await queue.FindAsync(given.ActionId, CancellationToken.None)).ShouldNotBeNull();
        left.Status.ShouldBe(failing == "queue" ? PendingActionStatus.Executing : PendingActionStatus.Rejected);
        (await features.ListWritesAsync(new ListWritesRequest())).Entries
            .ShouldNotContain(e => e.ActionId == given.ActionId && e.Outcome.StartsWith("rejected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_Stale_Approved_Verdict_Swept_Aside_By_A_Later_Card_Verdict_Is_Logged_In_History_As_Rejected()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var queue = host.Services.GetRequiredService<IActionQueue>();
        using var channel = host.CreateChannel();
        var features = new GrpcFeatures.FeaturesClient(channel);
        var stale = (await queue.EnqueueApprovedAsync(
            FeatureCatalog.GitHubId, [(Verdicts, "octocat/hello#41", "Approve octocat/hello#41")], CancellationToken.None)).Single();
        await GrantGitHubAsync(features);
        await features.GiveVerdictAsync(Verdict(42));
        (await queue.FindAsync(stale.Id, CancellationToken.None)).ShouldNotBeNull().Status.ShouldBe(PendingActionStatus.Rejected);

        var entries = await features.ListWritesAsync(new ListWritesRequest());

        entries.Entries.Where(e => e.ActionId == stale.Id).Select(e => (e.Outcome, e.ApprovedBy)).ShouldContain(
            ("rejected: GitHub's write permission changed after this verdict was given, so it will not be sent; give it again.", "owner"));
    }

    [Fact]
    public async Task Enabling_GitHub_Again_While_Its_Writes_Are_Granted_Keeps_An_Approved_Verdict_For_The_Worker()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var desktopChannel = host.CreateChannel();
        using var workerChannel = host.CreateWorkerChannel();
        var features = new GrpcFeatures.FeaturesClient(desktopChannel);
        var worker = new WorkerGate.WorkerGateClient(workerChannel);
        await GrantGitHubAsync(features);
        var given = await features.GiveVerdictAsync(Verdict(42));
        await EnableGitHubAgainAsync(features);

        var claimed = await worker.NextApprovedActionAsync(new NextApprovedActionRequest { FeatureId = FeatureCatalog.GitHubId });

        (claimed.Action?.Id).ShouldBe(given.ActionId);
    }

    [Fact]
    public async Task Approving_An_Action_Asked_Before_The_Current_Grant_Is_Refused_And_The_Action_Rejected_And_Logged()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var queue = host.Services.GetRequiredService<IActionQueue>();
        using var channel = host.CreateChannel();
        var features = new GrpcFeatures.FeaturesClient(channel);
        var waiting = await queue.EnqueueAsync(FeatureCatalog.GitHubId, Verdicts, "octocat/hello#42", "Approve octocat/hello#42", CancellationToken.None);
        await GrantGitHubAsync(features);

        var refused = await Should.ThrowAsync<RpcException>(() =>
            features.ApproveActionAsync(new ActionDecisionRequest { Id = waiting.Id }).ResponseAsync);

        (refused.StatusCode, refused.Status.Detail).ShouldBe((StatusCode.FailedPrecondition, "This action was asked before writes were granted, so it was rejected; ask again."));
        var after = (await queue.FindAsync(waiting.Id, CancellationToken.None)).ShouldNotBeNull();
        (after.Status, after.Reason).ShouldBe((PendingActionStatus.Rejected, "asked before writes were granted; ask again"));
        (await features.ListWritesAsync(new ListWritesRequest())).Entries.Select(e => (e.Outcome, e.ApprovedBy, e.ActionId))
            .ShouldBe([("rejected: asked before writes were granted; ask again", "owner", waiting.Id)]);
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("disable")]
    [InlineData("delete credentials")]
    [InlineData("enable failure")]
    public async Task Every_Call_That_Clears_The_Grant_Logs_One_Rejection_In_History_For_Each_Action_It_Rejected(string clearing)
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var queue = host.Services.GetRequiredService<IActionQueue>();
        using var channel = host.CreateChannel();
        var features = new GrpcFeatures.FeaturesClient(channel);
        await EnableGitHubAgainAsync(features);
        var waiting = await queue.EnqueueAsync(FeatureCatalog.GitHubId, Verdicts, "octocat/hello#42", "Approve octocat/hello#42", CancellationToken.None);
        var approved = await queue.EnqueueAsync(FeatureCatalog.GitHubId, Verdicts, "octocat/hello#43", "Approve octocat/hello#43", CancellationToken.None);
        await features.ApproveActionAsync(new ActionDecisionRequest { Id = approved.Id });

        switch (clearing)
        {
            case "revoke":
                await features.SetWriteGrantAsync(new SetWriteGrantRequest { Id = FeatureCatalog.GitHubId, Granted = false });
                break;
            case "disable":
                await features.DisableAsync(new DisableRequest { Id = FeatureCatalog.GitHubId });
                break;
            case "delete credentials":
                await features.DeleteCredentialsAsync(new DeleteCredentialsRequest { Id = FeatureCatalog.GitHubId });
                break;
            default:
                ((StubFeatureActivator)host.Services.GetRequiredService<IFeatureActivator>()).FailWith = "boom";
                await Should.ThrowAsync<RpcException>(() =>
                    features.EnableAsync(new EnableRequest { Id = FeatureCatalog.GitHubId, ConfirmWrites = true }).ResponseAsync);
                break;
        }

        (await features.ListWritesAsync(new ListWritesRequest())).Entries
            .Where(e => e.Outcome.StartsWith("rejected", StringComparison.Ordinal))
            .Select(e => (e.ActionId, e.Outcome, e.ApprovedBy))
            .OrderBy(e => e.ActionId == waiting.Id ? 0 : 1)
            .ShouldBe([
                (waiting.Id, "rejected: writes were revoked", "owner"),
                (approved.Id, "rejected: writes were revoked", "owner"),
            ]);
    }

    [Fact]
    public async Task The_Grant_Read_Just_Before_A_Send_Answers_Granted_While_The_Grant_Holds_And_Leaves_The_Action_Running()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var queue = host.Services.GetRequiredService<IActionQueue>();
        using var desktopChannel = host.CreateChannel();
        using var workerChannel = host.CreateWorkerChannel();
        var features = new GrpcFeatures.FeaturesClient(desktopChannel);
        var worker = new WorkerGate.WorkerGateClient(workerChannel);
        await GrantGitHubAsync(features);
        var given = await features.GiveVerdictAsync(Verdict(42));
        (await worker.NextApprovedActionAsync(new NextApprovedActionRequest { FeatureId = FeatureCatalog.GitHubId })).Action.Id.ShouldBe(given.ActionId);

        var checkedGrant = await worker.CheckActionGrantAsync(new CheckActionGrantRequest { FeatureId = FeatureCatalog.GitHubId, ActionId = given.ActionId });

        checkedGrant.Granted.ShouldBeTrue();
        (await queue.FindAsync(given.ActionId, CancellationToken.None)).ShouldNotBeNull().Status.ShouldBe(PendingActionStatus.Executing);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_Grant_Read_Just_Before_A_Send_After_The_Owner_Revoked_Rejects_The_Claimed_Action_As_Writes_Were_Revoked_And_Logs_It(bool grantedAgain)
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var queue = host.Services.GetRequiredService<IActionQueue>();
        using var desktopChannel = host.CreateChannel();
        using var workerChannel = host.CreateWorkerChannel();
        var features = new GrpcFeatures.FeaturesClient(desktopChannel);
        var worker = new WorkerGate.WorkerGateClient(workerChannel);
        await GrantGitHubAsync(features);
        var given = await features.GiveVerdictAsync(Verdict(42));
        (await worker.NextApprovedActionAsync(new NextApprovedActionRequest { FeatureId = FeatureCatalog.GitHubId })).Action.Id.ShouldBe(given.ActionId);
        await features.SetWriteGrantAsync(new SetWriteGrantRequest { Id = FeatureCatalog.GitHubId, Granted = false });
        if (grantedAgain)
        {
            await GrantGitHubAsync(features);
        }

        var checkedGrant = await worker.CheckActionGrantAsync(new CheckActionGrantRequest { FeatureId = FeatureCatalog.GitHubId, ActionId = given.ActionId });

        checkedGrant.Granted.ShouldBeFalse();
        var after = (await queue.FindAsync(given.ActionId, CancellationToken.None)).ShouldNotBeNull();
        (after.Status, after.Reason).ShouldBe((PendingActionStatus.Rejected, "writes were revoked"));
        (await features.ListWritesAsync(new ListWritesRequest { Limit = 1 })).Entries.Select(e => (e.Outcome, e.ActionId))
            .ShouldBe([("rejected: writes were revoked", given.ActionId)]);
    }

    [Fact]
    public async Task A_Write_The_Worker_Records_With_Its_Signer_Shows_The_Signer_In_History()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var desktopChannel = host.CreateChannel();
        using var workerChannel = host.CreateWorkerChannel();
        var features = new GrpcFeatures.FeaturesClient(desktopChannel);
        var worker = new WorkerGate.WorkerGateClient(workerChannel);
        await GrantGitHubAsync(features);
        var given = await features.GiveVerdictAsync(Verdict(42));
        await worker.NextApprovedActionAsync(new NextApprovedActionRequest { FeatureId = FeatureCatalog.GitHubId });

        await worker.RecordWriteAsync(new RecordWriteRequest
        {
            FeatureId = FeatureCatalog.GitHubId,
            Scope = Verdicts,
            Target = "octocat/hello#42",
            Outcome = "succeeded",
            ActionId = given.ActionId,
            Result = "https://github.com/octocat/hello/pull/42#pullrequestreview-88",
            SignedAs = "scout-octocat[bot]",
        });

        var newest = (await features.ListWritesAsync(new ListWritesRequest { Limit = 1 })).Entries.ShouldHaveSingleItem();
        (newest.Outcome, newest.SignedAs).ShouldBe(("succeeded", "scout-octocat[bot]"));
    }

    [Fact]
    public async Task A_Write_Recorded_After_An_Approval_Given_With_A_Reason_Is_Logged_Once_Beside_The_Approval()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var queue = host.Services.GetRequiredService<IActionQueue>();
        using var desktopChannel = host.CreateChannel();
        using var workerChannel = host.CreateWorkerChannel();
        var features = new GrpcFeatures.FeaturesClient(desktopChannel);
        var worker = new WorkerGate.WorkerGateClient(workerChannel);
        await GrantGitHubAsync(features);
        var waiting = await queue.EnqueueAsync(FeatureCatalog.GitHubId, Verdicts, "octocat/hello#42", "Approve octocat/hello#42", CancellationToken.None);
        await features.ApproveActionAsync(new ActionDecisionRequest { Id = waiting.Id, Reason = "Checked the diff" });

        await worker.RecordWriteAsync(new RecordWriteRequest
        {
            FeatureId = FeatureCatalog.GitHubId,
            Scope = Verdicts,
            Target = "octocat/hello#42",
            Outcome = "succeeded",
            ActionId = waiting.Id,
        });

        (await features.ListWritesAsync(new ListWritesRequest())).Entries.Select(e => (e.Outcome, e.ActionId))
            .ShouldBe([("succeeded", waiting.Id), ("approved: Checked the diff", waiting.Id)]);
    }

    private static Task<Feature> GrantYouTubeAsync(GrpcFeatures.FeaturesClient features) =>
        features.SetWriteGrantAsync(new SetWriteGrantRequest { Id = FeatureCatalog.YouTubeId, Granted = true, ConfirmWrites = true }).ResponseAsync;

    private static async Task<Scout.ControlPlane.Actions.PendingAction> ClaimedYouTubeUploadAsync(IServiceProvider services, GrpcFeatures.FeaturesClient features, WorkerGate.WorkerGateClient worker)
    {
        await GrantYouTubeAsync(features);
        var upload = (await services.GetRequiredService<Scout.ControlPlane.Operator.OperatorService>()
            .ApproveAsync([("videos.insert", "p1", "Upload")], CancellationToken.None)).Single();
        (await worker.NextApprovedActionAsync(new NextApprovedActionRequest())).Action.Id.ShouldBe(upload.Id);
        return upload;
    }

    private static async Task LapseClaimAsync(JsonFileActionQueue queue, string actionId)
    {
        var all = JsonNode.Parse(await File.ReadAllTextAsync(queue.FilePath))!.AsArray();
        var action = all.Single(a => a!["id"]!.GetValue<string>() == actionId)!;
        action["claimedAt"] = (DateTimeOffset.UtcNow - Scout.ControlPlane.WriteGate.WriteGateService.ClaimLease - TimeSpan.FromMinutes(1)).ToString("O", CultureInfo.InvariantCulture);
        await File.WriteAllTextAsync(queue.FilePath, all.ToJsonString());
    }

    private static string Digest(string payload) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

    [Fact]
    public async Task ListActions_Returns_A_Waiting_Action_Its_Group_Repository_Text_Digest_And_State_And_Never_Puts_The_Text_In_The_Summary_Or_The_Log()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var desktopChannel = host.CreateChannel();
        using var workerChannel = host.CreateWorkerChannel();
        var features = new GrpcFeatures.FeaturesClient(desktopChannel);
        var gate = new WorkerGate.WorkerGateClient(workerChannel);
        await GrantGitHubAsync(features);
        const string IssueBody = "The build fails on main since the last merge.";
        const string CommentBody = "This loop reads past the end of the list.";
        var issuePayload = $$"""{"body":"{{IssueBody}}","owner":"acme","repo":"shop","title":"Broken build"}""";
        var commentPayload = $$"""{"body":"{{CommentBody}}","number":"5","owner":"acme","repo":"shop"}""";
        Task<RequestWriteResponse> AskAsync(string scope, string target, string summary, string payload) => gate.RequestWriteAsync(new RequestWriteRequest
        {
            FeatureId = FeatureCatalog.GitHubId,
            Scope = scope,
            Target = target,
            Summary = summary,
            Payload = payload,
            GroupId = "turn-1",
            Repository = "acme/shop",
        }).ResponseAsync;
        var issue = await AskAsync("New issues", "GitHubActions.create_issue", "create_issue(owner=acme, repo=shop, title=Broken build, body)", issuePayload);
        var comment = await AskAsync("Pull request comments", "GitHubActions.comment_on_pull_request", "comment_on_pull_request(owner=acme, repo=shop, number=5, body)", commentPayload);
        var approved = await AskAsync("New issues", "GitHubActions.create_issue", "create_issue(owner=acme, repo=shop, title=Second, body)", issuePayload);
        var rejected = await AskAsync("New issues", "GitHubActions.create_issue", "create_issue(owner=acme, repo=shop, title=Third, body)", issuePayload);
        var approval = await features.ApproveActionAsync(new ActionDecisionRequest { Id = approved.ActionId });
        var rejection = await features.RejectActionAsync(new ActionDecisionRequest { Id = rejected.ActionId });

        var listed = (await features.ListPendingActionsAsync(new ListPendingActionsRequest())).Actions.ToDictionary(a => a.Id);

        var waitingIssue = listed[issue.ActionId];
        (waitingIssue.GroupId, waitingIssue.Repository, waitingIssue.Title, waitingIssue.Body, waitingIssue.PayloadDigest, waitingIssue.GroupState)
            .ShouldBe(("turn-1", "acme/shop", "Broken build", IssueBody, "57fe33b9ba33c86f4b470f074ec0391d1a68761c9b62754e5e033b2724bf14a5", "open"));
        var waitingComment = listed[comment.ActionId];
        (waitingComment.HasTitle, waitingComment.Body, waitingComment.PayloadDigest, waitingComment.GroupState)
            .ShouldBe((false, CommentBody, Digest(commentPayload), "open"));
        var decided = listed[approved.ActionId];
        (decided.Status, decided.GroupId, decided.HasTitle, decided.HasBody, decided.HasPayloadDigest, decided.HasGroupState)
            .ShouldBe(("approved", "turn-1", false, false, false, false));
        listed.ShouldNotContainKey(rejected.ActionId);
        foreach (var answered in new[] { approval, rejection })
        {
            (answered.HasTitle, answered.HasBody, answered.HasPayloadDigest, answered.HasGroupState).ShouldBe((false, false, false, false));
        }

        listed.Values.ShouldAllBe(a => !a.Summary.Contains(IssueBody) && !a.Summary.Contains(CommentBody));
        var log = (await features.ListWritesAsync(new ListWritesRequest())).Entries;
        log.Select(e => (e.ActionId, e.Outcome)).ShouldBe([(rejected.ActionId, "rejected"), (approved.ActionId, "approved")]);
        log[0].GroupId.ShouldBe("turn-1");
        var onDisk = await File.ReadAllTextAsync(((JsonLinesWriteLog)host.Services.GetRequiredService<IWriteLog>()).FilePath);
        onDisk.ShouldNotContain(IssueBody);
        onDisk.ShouldNotContain(CommentBody);
    }

    [Fact]
    public async Task ListActions_Of_500_Waiting_Actions_With_The_Largest_Payload_The_Queue_Takes_Fits_The_Desktop_Receive_Limit_With_A_Preview_Of_Each_Text()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var title = new string('漢', QueuedAction.MaxListedTitleLength + 10);
        var shape = $$"""{"body":"","title":"{{title}}"}""";
        var body = new string('漢', QueuedAction.MaxListedBodyLength + 10) + new string('a', QueuedAction.MaxPayloadLength - shape.Length - QueuedAction.MaxListedBodyLength - 10);
        var payload = $$"""{"body":"{{body}}","title":"{{title}}"}""";
        payload.Length.ShouldBe(QueuedAction.MaxPayloadLength);
        var queue = (JsonFileActionQueue)host.Services.GetRequiredService<IActionQueue>();
        var rows = Enumerable.Range(0, 500).Select(i => new QueuedAction(
                $"a{i}", FeatureCatalog.GitHubId, "New issues", "GitHubActions.create_issue", "create_issue(body, owner=acme, repo=shop, title=t)",
                DateTimeOffset.UtcNow, PendingActionStatus.Pending, null)
        {
            Payload = payload,
            GroupId = $"turn-{i / 50}",
            Repository = "acme/shop",
        })
            .ToList();
        await File.WriteAllTextAsync(queue.FilePath, JsonSerializer.Serialize(rows, QueueFile));
        using var channel = host.CreateDesktopSizedChannel();

        var listed = (await new GrpcFeatures.FeaturesClient(channel).ListPendingActionsAsync(new ListPendingActionsRequest())).Actions;

        var digest = Digest(payload);
        var listedTitle = title[..QueuedAction.MaxListedTitleLength];
        var listedBody = body[..QueuedAction.MaxListedBodyLength];
        listed.Count.ShouldBe(500);
        listed.ShouldAllBe(a => a.Title == listedTitle && a.Body == listedBody && a.TextClipped && a.PayloadDigest == digest);
    }

    [Fact]
    public async Task ListActions_Of_More_Waiting_Actions_Than_Their_Previews_Fit_Lists_Every_One_Within_The_Desktop_Receive_Limit_And_A_Row_Without_Its_Preview_Says_So_And_Is_Still_Decided()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var channel = host.CreateDesktopSizedChannel();
        var features = new GrpcFeatures.FeaturesClient(channel);
        await GrantGitHubAsync(features);
        var title = new string('漢', QueuedAction.MaxListedTitleLength);
        var body = new string('漢', QueuedAction.MaxListedBodyLength);
        var payload = $$"""{"body":"{{body}}","owner":"acme","repo":"shop","title":"{{title}}"}""";
        var previewBytes = Encoding.UTF8.GetByteCount(title) + Encoding.UTF8.GetByteCount(body);
        var count = Scout.Host.ControlPlane.ControlPlaneServiceRegistration.MaxMessageBytes / previewBytes + 1;
        var now = DateTimeOffset.UtcNow;
        var rows = Enumerable.Range(0, count).Select(i => new QueuedAction(
                $"a{i}", FeatureCatalog.GitHubId, "New issues", "GitHubActions.create_issue", "create_issue(body, owner=acme, repo=shop, title=t)",
                now, PendingActionStatus.Pending, null)
        {
            Payload = payload,
            GroupId = $"turn-{i / 50}",
            Repository = "acme/shop",
            GroupClosedAt = now,
        })
            .ToList();
        var queue = (JsonFileActionQueue)host.Services.GetRequiredService<IActionQueue>();
        await File.WriteAllTextAsync(queue.FilePath, JsonSerializer.Serialize(rows, QueueFile));

        var listed = (await features.ListPendingActionsAsync(new ListPendingActionsRequest())).Actions;

        var digest = Digest(payload);
        listed.Select(a => a.Id).ShouldBe(rows.Select(r => r.Id), "every waiting action is listed");
        var withPreview = listed.Where(a => a.HasTitle).ToList();
        withPreview.Count.ShouldBe(Scout.Host.ControlPlane.Services.FeatureMapping.MaxListedTextBytes / previewBytes);
        withPreview.ShouldAllBe(a => a.Title == title && a.Body == body && !a.TextClipped && a.PayloadDigest == digest && a.GroupState == "closed");
        var withoutPreview = listed.Where(a => !a.HasTitle).ToList();
        withoutPreview.Count.ShouldBe(count - withPreview.Count);
        withoutPreview.ShouldAllBe(a => !a.HasBody && a.TextClipped && a.PayloadDigest == digest && a.GroupState == "closed");

        var last = withoutPreview[^1];
        var text = await features.GetActionTextAsync(new GetActionTextRequest { Id = last.Id });
        (text.Title, text.Body, text.PayloadDigest).ShouldBe((title, body, digest), "the text of a row listed without it is one call away");
        var lastGroup = withoutPreview.Where(a => a.GroupId == last.GroupId).ToList();
        var set = new ActionSetRequest { GroupId = last.GroupId };
        set.Actions.AddRange(lastGroup.Select(a => new ActionRef { Id = a.Id, Digest = a.PayloadDigest }));
        var decided = await features.ApproveSetAsync(set);
        (decided.Decided, decided.Mismatches.Count).ShouldBe((true, 0));
        decided.Actions.Select(a => (a.Id, a.Status)).ShouldBe(lastGroup.Select(a => (a.Id, "approved")));
    }
}
