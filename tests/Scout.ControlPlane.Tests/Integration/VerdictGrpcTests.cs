using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Contracts;
using Scout.ControlPlane.Features;
using Shouldly;
using Xunit;
using GrpcFeatures = Scout.ControlPlane.Contracts.Features;

namespace Scout.ControlPlane.Tests.Integration;

/// <summary>The verdict a review card sends, through the real UDS host: only the desktop may give it, and only the worker gets its payload.</summary>
[Collection("uds")]
public sealed class VerdictGrpcTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private const string ReviewUrl = "https://github.com/octocat/hello/pull/42#pullrequestreview-88";

    private static GiveVerdictRequest Approve(string body = "Ready to merge.") => new()
    {
        Repository = "octocat/hello",
        Number = 42,
        Verdict = "approve",
        Body = body,
        CommentsJson = """[{"path":"src/Checkout.cs","line":3,"body":"c is new"}]""",
        Commit = Sha,
        TestsLine = "Tests: 3 checks passed.",
    };

    private static Task<Feature> GrantAsync(GrpcFeatures.FeaturesClient features) =>
        features.SetWriteGrantAsync(new SetWriteGrantRequest { Id = FeatureCatalog.GitHubId, Granted = true, ConfirmWrites = true }).ResponseAsync;

    private static RecordWriteRequest Record(string actionId, string outcome, string result) => new()
    {
        FeatureId = "github",
        Scope = "Pull request verdicts",
        Target = "octocat/hello#42",
        Outcome = outcome,
        ActionId = actionId,
        Result = result,
    };

    [Fact]
    public async Task The_Desktop_Gives_A_Verdict_That_Is_Queued_Then_Running_Then_Given_With_Its_Link()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var desktopChannel = host.CreateChannel();
        using var workerChannel = host.CreateWorkerChannel();
        var features = new GrpcFeatures.FeaturesClient(desktopChannel);
        var gate = new WorkerGate.WorkerGateClient(workerChannel);
        await GrantAsync(features);

        var given = await features.GiveVerdictAsync(Approve());
        var queued = await features.GetVerdictAsync(new GetVerdictRequest { ActionId = given.ActionId });
        var claimed = await gate.NextApprovedActionAsync(new NextApprovedActionRequest { FeatureId = "github" });
        var running = await features.GetVerdictAsync(new GetVerdictRequest { ActionId = given.ActionId });
        await gate.RecordWriteAsync(Record(given.ActionId, "succeeded", ReviewUrl));
        var done = await features.GetVerdictAsync(new GetVerdictRequest { ActionId = given.ActionId });

        (queued.State, running.State, done.State, done.Url, done.Reason).ShouldBe(("queued", "running", "given", ReviewUrl, ""));
        (done.Repository, done.Number, done.Commit, done.Verdict).ShouldBe(("octocat/hello", 42, Sha, "approve"));
        claimed.Action.Id.ShouldBe(given.ActionId);
        claimed.Payload.ShouldContain("\"body\":\"Ready to merge.\"");
        claimed.Payload.ShouldContain("\"commit\":\"" + Sha + "\"");
        claimed.Payload.ShouldContain("\"testsLine\":\"Tests: 3 checks passed.\"");
        var action = (await host.Services.GetRequiredService<IActionQueue>().FindAsync(given.ActionId, CancellationToken.None)).ShouldNotBeNull();
        (action.Action, action.Target, action.Summary).ShouldBe(("Pull request verdicts", "octocat/hello#42", "Approve octocat/hello#42"));
    }

    [Fact]
    public async Task A_Verdict_That_Failed_Reads_Failed_With_The_Reason()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var desktopChannel = host.CreateChannel();
        using var workerChannel = host.CreateWorkerChannel();
        var features = new GrpcFeatures.FeaturesClient(desktopChannel);
        var gate = new WorkerGate.WorkerGateClient(workerChannel);
        await GrantAsync(features);
        var given = await features.GiveVerdictAsync(Approve());
        await gate.NextApprovedActionAsync(new NextApprovedActionRequest { FeatureId = "github" });

        await gate.RecordWriteAsync(Record(given.ActionId, "failed: refused", "The pull request changed since the review; review it again."));
        var read = await features.GetVerdictAsync(new GetVerdictRequest { ActionId = given.ActionId });

        (read.State, read.Url, read.Reason).ShouldBe(("failed", "", "The pull request changed since the review; review it again."));
    }

    [Fact]
    public async Task The_Same_Click_Twice_Is_One_Action()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var channel = host.CreateChannel();
        var features = new GrpcFeatures.FeaturesClient(channel);
        await GrantAsync(features);

        var first = await features.GiveVerdictAsync(Approve());
        var second = await features.GiveVerdictAsync(Approve());

        second.ActionId.ShouldBe(first.ActionId);
    }

    [Fact]
    public async Task The_Worker_Token_May_Neither_Give_Nor_Read_A_Verdict()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var channel = host.CreateWorkerChannel();
        var features = new GrpcFeatures.FeaturesClient(channel);

        var give = await Should.ThrowAsync<RpcException>(() => features.GiveVerdictAsync(Approve()).ResponseAsync);
        var read = await Should.ThrowAsync<RpcException>(() => features.GetVerdictAsync(new GetVerdictRequest { ActionId = "x" }).ResponseAsync);
        var find = await Should.ThrowAsync<RpcException>(() => features.GetVerdictForAsync(new GetVerdictForRequest { Repository = "octocat/hello", Number = 42, Commit = Sha }).ResponseAsync);

        give.StatusCode.ShouldBe(StatusCode.PermissionDenied);
        give.Status.Detail.ShouldBe("This caller may not call /scout.controlplane.v1.Features/GiveVerdict.");
        read.StatusCode.ShouldBe(StatusCode.PermissionDenied);
        find.StatusCode.ShouldBe(StatusCode.PermissionDenied);
        (await host.Services.GetRequiredService<IActionQueue>().ListOpenAsync("github", CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Verdict_Without_The_Grant_Is_A_Failed_Precondition_And_A_Bad_Or_Oversized_One_An_Invalid_Argument()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var channel = host.CreateChannel();
        var features = new GrpcFeatures.FeaturesClient(channel);

        var notGranted = await Should.ThrowAsync<RpcException>(() => features.GiveVerdictAsync(Approve()).ResponseAsync);
        await GrantAsync(features);
        var oversized = await Should.ThrowAsync<RpcException>(() => features.GiveVerdictAsync(Approve(new string('x', 200_001))).ResponseAsync);
        var badComments = await Should.ThrowAsync<RpcException>(() => features.GiveVerdictAsync(new GiveVerdictRequest(Approve()) { CommentsJson = "not json" }).ResponseAsync);
        var badRepository = await Should.ThrowAsync<RpcException>(() => features.GiveVerdictAsync(new GiveVerdictRequest(Approve()) { Repository = "octocat" }).ResponseAsync);
        var unknown = await Should.ThrowAsync<RpcException>(() => features.GetVerdictAsync(new GetVerdictRequest { ActionId = "nope" }).ResponseAsync);

        notGranted.StatusCode.ShouldBe(StatusCode.FailedPrecondition);
        notGranted.Status.Detail.ShouldBe("The owner has not allowed github to write; allow it on the Features screen of Scout Desktop.");
        oversized.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        oversized.Status.Detail.ShouldBe("payload: The verdict is over 200000 characters; keep fewer comments.");
        badComments.Status.Detail.ShouldBe("comments: Every comment needs a path, a positive line and a body.");
        badRepository.Status.Detail.ShouldStartWith("repository: ");
        unknown.StatusCode.ShouldBe(StatusCode.NotFound);
    }

    [Fact]
    public async Task A_Verdict_The_Model_Asks_For_Is_Queued_With_Its_Arguments_And_Handed_To_The_Worker_After_The_Owner_Approves()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var desktopChannel = host.CreateChannel();
        using var workerChannel = host.CreateWorkerChannel();
        var features = new GrpcFeatures.FeaturesClient(desktopChannel);
        var gate = new WorkerGate.WorkerGateClient(workerChannel);
        await GrantAsync(features);
        const string arguments = """{"commit":"0123456","number":"42","owner":"octocat","repo":"hello","verdict":"request_changes"}""";

        var queued = await gate.RequestWriteAsync(new RequestWriteRequest
        {
            FeatureId = "github",
            Scope = "Pull request verdicts",
            Target = "GitHubActions.give_pull_request_verdict",
            Summary = "give_pull_request_verdict(number=42, owner=octocat, repo=hello, verdict=request_changes)",
            Payload = arguments,
        });
        var before = await gate.NextApprovedActionAsync(new NextApprovedActionRequest { FeatureId = "github" });
        var listed = await features.ListPendingActionsAsync(new ListPendingActionsRequest());
        await features.ApproveActionAsync(new ActionDecisionRequest { Id = queued.ActionId });
        var after = await gate.NextApprovedActionAsync(new NextApprovedActionRequest { FeatureId = "github" });

        queued.Decision.ShouldBe("queued");
        before.Action.ShouldBeNull();
        listed.ToString().ShouldNotContain("request_changes\"");
        after.Action.Id.ShouldBe(queued.ActionId);
        after.Payload.ShouldBe(arguments);
    }

    [Fact]
    public async Task A_Restarted_Desktop_Finds_The_Latest_Verdict_On_A_Commit_And_Nothing_On_Another()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var desktopChannel = host.CreateChannel();
        using var workerChannel = host.CreateWorkerChannel();
        var features = new GrpcFeatures.FeaturesClient(desktopChannel);
        var gate = new WorkerGate.WorkerGateClient(workerChannel);
        await GrantAsync(features);
        var key = new GetVerdictForRequest { Repository = "octocat/hello", Number = 42, Commit = Sha };

        var none = await features.GetVerdictForAsync(key);
        var failed = await features.GiveVerdictAsync(Approve());
        await gate.NextApprovedActionAsync(new NextApprovedActionRequest { FeatureId = "github" });
        await gate.RecordWriteAsync(Record(failed.ActionId, "failed: github 403", "GitHub denied the request (insufficient token permissions)."));
        var retried = await features.GiveVerdictAsync(new GiveVerdictRequest(Approve()) { Verdict = "request_changes" });
        var found = await features.GetVerdictForAsync(key);
        var otherCommit = await features.GetVerdictForAsync(new GetVerdictForRequest(key) { Commit = "fedcba9" });

        none.Found.ShouldBeFalse();
        retried.ActionId.ShouldNotBe(failed.ActionId);
        (found.Found, found.ActionId, found.State, found.Url, found.Reason, found.Verdict)
            .ShouldBe((true, retried.ActionId, "queued", "", "", "request_changes"));
        otherCommit.Found.ShouldBeFalse();
    }
}
