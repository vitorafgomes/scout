using System.Text.Json;
using System.Text.Json.Serialization;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Contracts;
using Scout.ControlPlane.Features;
using Scout.ControlPlane.WriteGate;
using Shouldly;
using Xunit;
using GrpcFeatures = Scout.ControlPlane.Contracts.Features;
using QueuedAction = Scout.ControlPlane.Actions.PendingAction;

namespace Scout.ControlPlane.Tests.Integration;

/// <summary>
/// The owner's Approve all and Reject all and the read of one group's actions, over the real UDS host: what the desktop
/// gets back, what the worker may not call, and what a queue or a log that fails part way leaves behind.
/// </summary>
[Collection("uds")]
public sealed class ActionSetGrpcTests
{
    private const string Group = "turn-1";

    private static readonly JsonSerializerOptions QueueFile = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [Fact]
    public async Task Approve_Set_And_Reject_Set_Decide_The_Listed_Actions_Over_gRPC_And_Answer_Them_As_They_Stand()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var (features, worker, channels) = Clients(host);
        using var _ = channels;
        await GrantGitHubAsync(features);
        var ids = await AskAsync(worker, 4);
        await worker.CloseWriteGroupAsync(new CloseWriteGroupRequest { GroupId = Group });
        var listed = await ListedAsync(features);

        var approved = await features.ApproveSetAsync(Set(listed, ids[0], ids[1]));
        var rejected = await features.RejectSetAsync(Set(listed, ids[2], ids[3]));

        (approved.Decided, approved.GroupId, approved.Mismatches.Count).ShouldBe((true, Group, 0));
        approved.Actions.Select(a => (a.Id, a.Status, a.GroupId)).ShouldBe([(ids[0], "approved", Group), (ids[1], "approved", Group)]);
        (rejected.Decided, rejected.Mismatches.Count).ShouldBe((true, 0));
        rejected.Actions.Select(a => (a.Id, a.Status)).ShouldBe([(ids[2], "rejected"), (ids[3], "rejected")]);
        (await features.ListWritesAsync(new ListWritesRequest())).Entries.Reverse().Select(e => (e.ActionId, e.Outcome, e.GroupId)).ShouldBe([
            (ids[0], "approved", Group), (ids[1], "approved", Group), (ids[2], "rejected", Group), (ids[3], "rejected", Group)]);
    }

    [Fact]
    public async Task A_Set_That_Does_Not_Match_The_Listing_Answers_The_Mismatch_And_A_Set_Refused_Whole_Answers_Failed_Precondition()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var (features, worker, channels) = Clients(host);
        using var _ = channels;
        await GrantGitHubAsync(features);
        var ids = await AskAsync(worker, 2);
        var listed = await ListedAsync(features);

        var whileOpen = await Should.ThrowAsync<RpcException>(() => features.ApproveSetAsync(Set(listed, ids)).ResponseAsync);
        await worker.CloseWriteGroupAsync(new CloseWriteGroupRequest { GroupId = Group });
        var stale = Set(listed, ids);
        stale.Actions[1].Digest = "0000";
        var mismatched = await features.ApproveSetAsync(stale);

        (whileOpen.StatusCode, whileOpen.Status.Detail).ShouldBe((StatusCode.FailedPrecondition, ActionDecisionService.StillQueuing));
        mismatched.Decided.ShouldBeFalse();
        mismatched.Mismatches.Select(m => (m.Id, m.Reason)).ShouldBe([(ids[1], ActionDecisionService.ChangedAfterListing)]);
        mismatched.Actions.Select(a => a.Status).ShouldBe(["pending", "pending"]);
        (await features.ListWritesAsync(new ListWritesRequest())).Entries.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("ApproveSet")]
    [InlineData("RejectSet")]
    [InlineData("GetActionGroup")]
    [InlineData("GetActionText")]
    [InlineData("ApproveCardIssues")]
    public async Task The_Worker_Token_May_Not_Decide_Or_Read_A_Set(string call)
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var (features, worker, channels) = Clients(host);
        using var _ = channels;
        await GrantGitHubAsync(features);
        var ids = await AskAsync(worker, 2);
        await worker.CloseWriteGroupAsync(new CloseWriteGroupRequest { GroupId = Group });
        var listed = await ListedAsync(features);
        var asWorker = new GrpcFeatures.FeaturesClient(channels.Worker);

        var refused = await Should.ThrowAsync<RpcException>(() => call switch
        {
            "ApproveSet" => (Task)asWorker.ApproveSetAsync(Set(listed, ids)).ResponseAsync,
            "RejectSet" => asWorker.RejectSetAsync(Set(listed, ids)).ResponseAsync,
            "GetActionText" => asWorker.GetActionTextAsync(new GetActionTextRequest { Id = ids[0] }).ResponseAsync,
            "ApproveCardIssues" => asWorker.ApproveCardIssuesAsync(Card("card-1", 2)).ResponseAsync,
            _ => asWorker.GetActionGroupAsync(new GetActionGroupRequest { GroupId = Group }).ResponseAsync,
        });

        refused.StatusCode.ShouldBe(StatusCode.PermissionDenied);
        (await ListedAsync(features)).Values.Select(a => a.Status).ShouldBe(["pending", "pending"]);
        (await features.ListWritesAsync(new ListWritesRequest())).Entries.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("ApproveSet")]
    [InlineData("RejectSet")]
    [InlineData("GetActionGroup")]
    public async Task A_Call_Without_A_Group_Id_Is_Refused_As_An_Invalid_Argument(string call)
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var (features, worker, channels) = Clients(host);
        using var _ = channels;
        await GrantGitHubAsync(features);
        var ids = await AskAsync(worker, 1);
        await worker.CloseWriteGroupAsync(new CloseWriteGroupRequest { GroupId = Group });
        var set = Set(await ListedAsync(features), ids);
        set.GroupId = " ";

        var refused = await Should.ThrowAsync<RpcException>(() => call switch
        {
            "ApproveSet" => (Task)features.ApproveSetAsync(set).ResponseAsync,
            "RejectSet" => features.RejectSetAsync(set).ResponseAsync,
            _ => features.GetActionGroupAsync(new GetActionGroupRequest { GroupId = " " }).ResponseAsync,
        });

        refused.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        (await ListedAsync(features)).Values.Select(a => a.Status).ShouldBe(["pending"]);
    }

    [Fact]
    public async Task A_Queue_That_Cannot_Write_The_Set_Fails_The_Call_And_Decides_And_Logs_Nothing()
    {
        await using var host = await ControlPlaneTestHost.StartAsync(configureServices: services =>
        {
            services.RemoveAll<IActionQueue>();
            services.AddSingleton(sp => FailingCalls.Wrap<IActionQueue>(
                new JsonFileActionQueue(sp.GetRequiredService<IOptions<FeatureStateStoreOptions>>(), NullLogger<JsonFileActionQueue>.Instance),
                (method, _) => method.Name == nameof(IActionQueue.DecideSetAsync) ? new IOException("disk full") : null));
        });
        var (features, worker, channels) = Clients(host);
        using var _ = channels;
        await GrantGitHubAsync(features);
        var ids = await AskAsync(worker, 2);
        await worker.CloseWriteGroupAsync(new CloseWriteGroupRequest { GroupId = Group });
        var listed = await ListedAsync(features);

        var failed = await Should.ThrowAsync<RpcException>(() => features.ApproveSetAsync(Set(listed, ids)).ResponseAsync);

        failed.StatusCode.ShouldNotBe(StatusCode.OK);
        (await ListedAsync(features)).Values.Select(a => a.Status).ShouldBe(["pending", "pending"]);
        (await features.ListWritesAsync(new ListWritesRequest())).Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_Approval_Log_Failing_Part_Way_Hands_Out_The_Logged_And_Rejects_The_Rest_After_The_Window()
    {
        var clock = new MovableTime();
        var approvals = 0;
        await using var host = await ControlPlaneTestHost.StartAsync(configureServices: services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.RemoveAll<IWriteLog>();
            services.AddSingleton(sp => FailingCalls.Wrap<IWriteLog>(
                new JsonLinesWriteLog(sp.GetRequiredService<IOptions<FeatureStateStoreOptions>>(), NullLogger<JsonLinesWriteLog>.Instance),
                (method, args) => method.Name == nameof(IWriteLog.AppendAsync)
                    && ((Scout.ControlPlane.Actions.WriteLogEntry)args![0]!).Outcome == WriteGateService.ApprovedOutcome
                    && Interlocked.Increment(ref approvals) > 1
                    ? new IOException("disk full")
                    : null));
        });
        var (features, worker, channels) = Clients(host);
        using var _ = channels;
        await GrantGitHubAsync(features);
        var ids = await AskAsync(worker, 3);
        await worker.CloseWriteGroupAsync(new CloseWriteGroupRequest { GroupId = Group });
        var set = Set(await ListedAsync(features), ids);

        var failed = await Should.ThrowAsync<RpcException>(() => features.ApproveSetAsync(set).ResponseAsync);
        var retriedInTheWindow = await features.ApproveSetAsync(set);
        var handedOut = await worker.NextApprovedActionAsync(new NextApprovedActionRequest { FeatureId = FeatureCatalog.GitHubId });
        var nothingElseInTheWindow = await worker.NextApprovedActionAsync(new NextApprovedActionRequest { FeatureId = FeatureCatalog.GitHubId });
        clock.Ahead = WriteGateService.ApprovalLogWindow + TimeSpan.FromSeconds(1);
        var nothingAfterTheWindow = await worker.NextApprovedActionAsync(new NextApprovedActionRequest { FeatureId = FeatureCatalog.GitHubId });
        var retriedAfterTheWindow = await features.ApproveSetAsync(set);

        failed.StatusCode.ShouldNotBe(StatusCode.OK);
        retriedInTheWindow.Decided.ShouldBeFalse("a set whose approvals the log did not all take is never answered as decided");
        retriedInTheWindow.Mismatches.Select(m => (m.Id, m.Reason)).ShouldBe([(ids[1], WriteGateService.ApprovalNotInLog), (ids[2], WriteGateService.ApprovalNotInLog)]);
        handedOut.Action.Id.ShouldBe(ids[0]);
        nothingElseInTheWindow.Action.ShouldBeNull("an approval not in the log is not handed out within the window");
        nothingAfterTheWindow.Action.ShouldBeNull("an approval not in the log is rejected once the window passed");
        retriedAfterTheWindow.Decided.ShouldBeFalse();
        retriedAfterTheWindow.Actions.Select(a => (a.Id, a.Status)).ShouldBe([(ids[0], "executing"), (ids[1], "rejected"), (ids[2], "rejected")]);
        retriedAfterTheWindow.Actions.ShouldAllBe(a => a.Status != "executed", "no row of the failed set may read as sent");
        (await features.ListWritesAsync(new ListWritesRequest())).Entries.Reverse().Select(e => (e.ActionId, e.Outcome)).ShouldBe([
            (ids[0], "approved"),
            (ids[1], $"rejected: {WriteGateService.ApprovalNotInLog}"),
            (ids[2], $"rejected: {WriteGateService.ApprovalNotInLog}")]);
    }

    [Fact]
    public async Task Get_Action_Group_Returns_The_Group_s_Rows_In_Every_Status_With_Their_Text_And_Says_When_A_Text_Was_Cut()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var (features, worker, channels) = Clients(host);
        using var _ = channels;
        await GrantGitHubAsync(features);
        var longBody = new string('b', QueuedAction.MaxListedBodyLength + 10);
        var ids = await AskAsync(worker, 3);
        ids.AddRange(await AskAsync(worker, 1, longBody, first: 3));
        await worker.CloseWriteGroupAsync(new CloseWriteGroupRequest { GroupId = Group });
        var listed = await ListedAsync(features);
        await features.ApproveSetAsync(Set(listed, ids[0], ids[1]));
        var sent = await worker.NextApprovedActionAsync(new NextApprovedActionRequest { FeatureId = FeatureCatalog.GitHubId });
        await worker.RecordWriteAsync(new RecordWriteRequest
        {
            FeatureId = FeatureCatalog.GitHubId,
            Scope = "New issues",
            Target = sent.Action.Target,
            Outcome = "succeeded",
            ActionId = sent.Action.Id,
            Result = "https://github.com/acme/shop/issues/1",
        });
        await features.RejectSetAsync(Set(listed, ids[2]));

        var group = await features.GetActionGroupAsync(new GetActionGroupRequest { GroupId = Group });

        group.GroupId.ShouldBe(Group);
        group.Actions.Select(a => (a.Id, a.Status, a.GroupState, a.Result)).ShouldBe([
            (ids[0], "executed", "closed", "https://github.com/acme/shop/issues/1"),
            (ids[1], "approved", "closed", string.Empty),
            (ids[2], "rejected", "closed", string.Empty),
            (ids[3], "pending", "closed", string.Empty)]);
        group.Actions.Select(a => (a.HasTitle, a.Title, a.Body.Length, a.TextClipped, a.HasPayloadDigest)).ShouldBe([
            (false, string.Empty, 0, false, false),
            (true, "Finding 1", "Body of finding 1.".Length, false, false),
            (true, "Finding 2", "Body of finding 2.".Length, false, false),
            (true, "Finding 3", QueuedAction.MaxListedBodyLength, true, true)]);
        listed[ids[3]].TextClipped.ShouldBeTrue("the list says the text was cut too");
        listed[ids[0]].HasTextClipped.ShouldBeFalse();
    }

    [Fact]
    public async Task Get_Action_Group_Of_50_Actions_With_The_Largest_Payload_The_Queue_Takes_Fits_The_Desktop_Receive_Limit()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var shape = """{"body":"","title":"t"}""";
        var body = new string('漢', QueuedAction.MaxPayloadLength - shape.Length);
        var payload = $$"""{"body":"{{body}}","title":"t"}""";
        var queue = (JsonFileActionQueue)host.Services.GetRequiredService<IActionQueue>();
        var rows = Enumerable.Range(0, 50).Select(i => new QueuedAction(
                $"a{i}", FeatureCatalog.GitHubId, "New issues", "GitHubActions.create_issue", "create_issue(owner=acme, repo=shop, title=t, body)",
                DateTimeOffset.UtcNow, PendingActionStatus.Pending, null)
        {
            Payload = payload,
            GroupId = Group,
            Repository = "acme/shop",
        })
            .ToList();
        await File.WriteAllTextAsync(queue.FilePath, JsonSerializer.Serialize(rows, QueueFile));
        using var channel = host.CreateDesktopSizedChannel();

        var group = await new GrpcFeatures.FeaturesClient(channel).GetActionGroupAsync(new GetActionGroupRequest { GroupId = Group });

        group.Actions.Count.ShouldBe(50);
        group.Actions.ShouldAllBe(a => a.Body.Length == QueuedAction.MaxListedBodyLength && a.TextClipped);
    }

    [Fact]
    public async Task Approve_Card_Issues_Over_gRPC_Queues_Them_Approved_In_One_Closed_Group_Logged_Once_And_A_Second_Click_Answers_The_Same()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var (features, _, channels) = Clients(host);
        using var _ = channels;
        await GrantGitHubAsync(features);

        var first = await features.ApproveCardIssuesAsync(Card("card-1", 3));
        var second = await features.ApproveCardIssuesAsync(Card("card-1", 3));

        (first.Decided, first.Mismatches.Count, first.Actions.Count).ShouldBe((true, 0, 3));
        first.Actions.ShouldAllBe(a => a.Status == "approved" && a.Action == "New issues" && a.Target == "GitHubActions.create_issue"
            && a.GroupId == first.GroupId && a.Repository == "acme/shop");
        (second.Decided, second.GroupId).ShouldBe((true, first.GroupId));
        second.Actions.Select(a => a.Id).ShouldBe(first.Actions.Select(a => a.Id));
        (await ListedAsync(features)).Values.ShouldAllBe(a => a.Status == "approved", "nothing waits in Approvals");
        (await features.GetActionGroupAsync(new GetActionGroupRequest { GroupId = first.GroupId })).Actions.ShouldAllBe(a => a.GroupState == "closed");
        (await features.ListWritesAsync(new ListWritesRequest())).Entries.Reverse().Select(e => (e.ActionId, e.Outcome, e.GroupId))
            .ShouldBe(first.Actions.Select(a => (a.Id, "approved", first.GroupId)));
    }

    [Fact]
    public async Task A_Card_Click_That_Queues_A_Failed_Issue_Again_Answers_The_New_Group_And_Each_Row_Carries_Its_Own_Group()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var (features, _, channels) = Clients(host);
        using var _ = channels;
        await GrantGitHubAsync(features);
        var first = await features.ApproveCardIssuesAsync(Card("card-1", 2));
        await host.Services.GetRequiredService<IActionQueue>().ExecuteAsync(
            first.Actions[1].Id, "failed: github 502", "GitHub is currently unavailable. Try again later.", _ => Task.CompletedTask, CancellationToken.None);

        var second = await features.ApproveCardIssuesAsync(Card("card-1", 2));
        var third = await features.ApproveCardIssuesAsync(Card("card-1", 2));

        second.Actions[0].Id.ShouldBe(first.Actions[0].Id, "the issue still open answers its row");
        second.Actions[1].Id.ShouldNotBe(first.Actions[1].Id, "the failed issue is queued again");
        second.Actions.Select(a => a.GroupId).ShouldBe([first.GroupId, second.Actions[1].GroupId], "each row on the wire carries its own group");
        second.Actions[1].GroupId.ShouldNotBe(first.GroupId);
        second.GroupId.ShouldBe(second.Actions[1].GroupId, "the answer's group is the one this click queued");
        third.Actions.Select(a => a.Id).ShouldBe(second.Actions.Select(a => a.Id));
        third.GroupId.ShouldBe(first.GroupId, "a click that queues nothing answers the first row's group");
    }

    [Fact]
    public async Task A_Card_Click_Without_The_Grant_Or_Over_The_Bound_Is_Refused_And_Queues_Nothing()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var (features, _, channels) = Clients(host);
        using var _ = channels;

        var withoutTheGrant = await Should.ThrowAsync<RpcException>(() => features.ApproveCardIssuesAsync(Card("card-1", 1)).ResponseAsync);
        await GrantGitHubAsync(features);
        var overTheBound = await Should.ThrowAsync<RpcException>(() => features.ApproveCardIssuesAsync(Card("card-1", 51)).ResponseAsync);

        withoutTheGrant.StatusCode.ShouldBe(StatusCode.FailedPrecondition);
        (overTheBound.StatusCode, overTheBound.Status.Detail).ShouldBe((StatusCode.InvalidArgument, "issues: One click opens 1 to 50 issues."));
        (await ListedAsync(features)).ShouldBeEmpty();
        (await features.ListWritesAsync(new ListWritesRequest())).Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Queue_That_Cannot_Write_The_Card_s_Issues_Fails_The_Click_And_Queues_And_Logs_Nothing()
    {
        await using var host = await ControlPlaneTestHost.StartAsync(configureServices: services =>
        {
            services.RemoveAll<IActionQueue>();
            services.AddSingleton(sp => FailingCalls.Wrap<IActionQueue>(
                new JsonFileActionQueue(sp.GetRequiredService<IOptions<FeatureStateStoreOptions>>(), NullLogger<JsonFileActionQueue>.Instance),
                (method, _) => method.Name == nameof(IActionQueue.EnqueueApprovedOnceEachAsync) ? new IOException("disk full") : null));
        });
        var (features, _, channels) = Clients(host);
        using var _ = channels;
        await GrantGitHubAsync(features);

        var failed = await Should.ThrowAsync<RpcException>(() => features.ApproveCardIssuesAsync(Card("card-1", 2)).ResponseAsync);

        failed.StatusCode.ShouldBe(StatusCode.Unknown, "a queue that cannot be written fails the click; it is never answered as queued");
        (await ListedAsync(features)).ShouldBeEmpty();
        (await features.ListWritesAsync(new ListWritesRequest())).Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Get_Action_Text_Answers_The_Full_Text_And_The_Listed_Digest_Of_An_Action_Until_It_Is_Executed()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var (features, worker, channels) = Clients(host);
        using var _ = channels;
        await GrantGitHubAsync(features);
        var longBody = new string('b', QueuedAction.MaxListedBodyLength + 500);
        var ids = await AskAsync(worker, 1, longBody);
        ids.AddRange(await AskAsync(worker, 3, first: 1));
        var listed = await ListedAsync(features);
        await features.ApproveActionAsync(new ActionDecisionRequest { Id = ids[1] });
        await features.RejectActionAsync(new ActionDecisionRequest { Id = ids[2] });
        await features.ApproveActionAsync(new ActionDecisionRequest { Id = ids[3] });
        var sent = await worker.NextApprovedActionAsync(new NextApprovedActionRequest { FeatureId = FeatureCatalog.GitHubId });
        await worker.RecordWriteAsync(new RecordWriteRequest
        {
            FeatureId = FeatureCatalog.GitHubId,
            Scope = "New issues",
            Target = sent.Action.Target,
            Outcome = "succeeded",
            ActionId = sent.Action.Id,
            Result = "https://github.com/acme/shop/issues/1",
        });
        var executed = sent.Action.Id;
        executed.ShouldBe(ids[1], "the claim takes the oldest approved action");

        var texts = new List<GetActionTextResponse>();
        foreach (var id in ids)
        {
            texts.Add(await features.GetActionTextAsync(new GetActionTextRequest { Id = id }));
        }

        listed[ids[0]].TextClipped.ShouldBeTrue("the list carries a preview of the long body");
        (texts[0].Title, texts[0].Body, texts[0].PayloadDigest, texts[0].CutAfterExecution)
            .ShouldBe(("Finding 0", longBody, listed[ids[0]].PayloadDigest, false), "a waiting action: the full text, the digest the list gave");
        foreach (var i in new List<int> { 2, 3 })
        {
            (texts[i].Title, texts[i].Body, texts[i].PayloadDigest, texts[i].CutAfterExecution)
                .ShouldBe(($"Finding {i}", $"Body of finding {i}.", listed[ids[i]].PayloadDigest, false), "a decided action not yet executed keeps its full text");
        }

        var afterExecution = texts[1];
        (afterExecution.HasTitle, afterExecution.HasBody, afterExecution.HasPayloadDigest, afterExecution.CutAfterExecution)
            .ShouldBe((false, false, false, true), "an executed action keeps only what says where it went, and says so");
    }

    [Theory]
    [InlineData("no-such-action")]
    [InlineData(" ")]
    public async Task Get_Action_Text_Of_An_Unknown_Or_Blank_Id_Is_Not_Found(string id)
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var (features, worker, channels) = Clients(host);
        using var _ = channels;
        await GrantGitHubAsync(features);
        await AskAsync(worker, 1);

        var refused = await Should.ThrowAsync<RpcException>(() => features.GetActionTextAsync(new GetActionTextRequest { Id = id }).ResponseAsync);

        refused.StatusCode.ShouldBe(StatusCode.NotFound);
    }

    [Fact]
    public async Task Get_Action_Text_On_A_Queue_That_Cannot_Be_Read_Fails_And_Is_Never_Answered_As_Not_Found()
    {
        await using var host = await ControlPlaneTestHost.StartAsync(configureServices: services =>
        {
            services.RemoveAll<IActionQueue>();
            services.AddSingleton(sp => FailingCalls.Wrap<IActionQueue>(
                new JsonFileActionQueue(sp.GetRequiredService<IOptions<FeatureStateStoreOptions>>(), NullLogger<JsonFileActionQueue>.Instance),
                (method, _) => method.Name == nameof(IActionQueue.FindAsync) ? new IOException("disk read failed") : null));
        });
        var (features, worker, channels) = Clients(host);
        using var _ = channels;
        await GrantGitHubAsync(features);
        var ids = await AskAsync(worker, 1);

        var failed = await Should.ThrowAsync<RpcException>(() => features.GetActionTextAsync(new GetActionTextRequest { Id = ids[0] }).ResponseAsync);

        failed.StatusCode.ShouldBe(StatusCode.Unknown, "a read that failed is a failure, never an action that does not exist");
    }

    [Fact]
    public async Task Get_Action_Text_Of_The_Largest_Payload_The_Queue_Takes_Fits_The_Desktop_Receive_Limit()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var title = new string('漢', 1_000);
        var shape = $$"""{"body":"","title":"{{title}}"}""";
        var body = new string('漢', QueuedAction.MaxPayloadLength - shape.Length);
        var payload = $$"""{"body":"{{body}}","title":"{{title}}"}""";
        payload.Length.ShouldBe(QueuedAction.MaxPayloadLength);
        var queue = (JsonFileActionQueue)host.Services.GetRequiredService<IActionQueue>();
        var row = new QueuedAction(
            "a0", FeatureCatalog.GitHubId, "New issues", "GitHubActions.create_issue", "create_issue(body, owner=acme, repo=shop, title=t)",
            DateTimeOffset.UtcNow, PendingActionStatus.Pending, null)
        {
            Payload = payload,
            GroupId = Group,
            Repository = "acme/shop",
        };
        await File.WriteAllTextAsync(queue.FilePath, JsonSerializer.Serialize(new List<QueuedAction> { row }, QueueFile));
        using var channel = host.CreateDesktopSizedChannel();

        var text = await new GrpcFeatures.FeaturesClient(channel).GetActionTextAsync(new GetActionTextRequest { Id = "a0" });

        (text.Title, text.Body, text.PayloadDigest).ShouldBe((title, body, row.PayloadDigest()));
    }

    [Fact]
    public async Task Get_Action_Group_Of_More_Actions_Than_Their_Previews_Fit_Returns_Every_One_Within_The_Desktop_Receive_Limit_And_A_Row_Without_Its_Preview_Says_So()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var title = new string('漢', QueuedAction.MaxListedTitleLength);
        var body = new string('漢', QueuedAction.MaxListedBodyLength);
        var payload = $$"""{"body":"{{body}}","owner":"acme","repo":"shop","title":"{{title}}"}""";
        var previewBytes = System.Text.Encoding.UTF8.GetByteCount(title) + System.Text.Encoding.UTF8.GetByteCount(body);
        var count = Scout.Host.ControlPlane.ControlPlaneServiceRegistration.MaxMessageBytes / previewBytes + 1;
        var rows = Enumerable.Range(0, count).Select(i => new QueuedAction(
                $"a{i}", FeatureCatalog.GitHubId, "New issues", "GitHubActions.create_issue", "create_issue(owner=acme, repo=shop, title=t, body)",
                DateTimeOffset.UtcNow, PendingActionStatus.Pending, null)
        {
            Payload = payload,
            GroupId = Group,
            Repository = "acme/shop",
        })
            .ToList();
        var queue = (JsonFileActionQueue)host.Services.GetRequiredService<IActionQueue>();
        await File.WriteAllTextAsync(queue.FilePath, JsonSerializer.Serialize(rows, QueueFile));
        using var channel = host.CreateDesktopSizedChannel();
        var features = new GrpcFeatures.FeaturesClient(channel);

        var group = await features.GetActionGroupAsync(new GetActionGroupRequest { GroupId = Group });

        var digest = rows[0].PayloadDigest();
        group.Actions.Select(a => a.Id).ShouldBe(rows.Select(r => r.Id), "every action of the group is returned");
        var withPreview = group.Actions.Where(a => a.HasTitle).ToList();
        withPreview.Count.ShouldBe(Scout.Host.ControlPlane.Services.FeatureMapping.MaxListedTextBytes / previewBytes);
        withPreview.ShouldAllBe(a => a.Title == title && a.Body == body && !a.TextClipped && a.PayloadDigest == digest && a.GroupState == ActionDecisionService.GroupOverBound);
        var withoutPreview = group.Actions.Where(a => !a.HasTitle).ToList();
        withoutPreview.Count.ShouldBe(count - withPreview.Count);
        withoutPreview.ShouldAllBe(a => !a.HasBody && a.TextClipped && a.PayloadDigest == digest && a.GroupState == ActionDecisionService.GroupOverBound);
        var text = await features.GetActionTextAsync(new GetActionTextRequest { Id = withoutPreview[^1].Id });
        (text.Title, text.Body, text.PayloadDigest).ShouldBe((title, body, digest), "the text of a row returned without it is one call away");
    }

    private static (GrpcFeatures.FeaturesClient Features, WorkerGate.WorkerGateClient Worker, Channels Channels) Clients(ControlPlaneTestHost host)
    {
        var channels = new Channels(host.CreateChannel(), host.CreateWorkerChannel());
        return (new GrpcFeatures.FeaturesClient(channels.Desktop), new WorkerGate.WorkerGateClient(channels.Worker), channels);
    }

    private static Task<Feature> GrantGitHubAsync(GrpcFeatures.FeaturesClient features) =>
        features.SetWriteGrantAsync(new SetWriteGrantRequest { Id = FeatureCatalog.GitHubId, Granted = true, ConfirmWrites = true }).ResponseAsync;

    private static async Task<List<string>> AskAsync(WorkerGate.WorkerGateClient worker, int count, string? body = null, int first = 0)
    {
        var ids = new List<string>();
        for (var i = first; i < first + count; i++)
        {
            ids.Add((await worker.RequestWriteAsync(new RequestWriteRequest
            {
                FeatureId = FeatureCatalog.GitHubId,
                Scope = "New issues",
                Target = "GitHubActions.create_issue",
                Summary = "create_issue(owner=acme, repo=shop, title, body)",
                Payload = $$"""{"owner":"acme","repo":"shop","title":"Finding {{i}}","body":"{{body ?? $"Body of finding {i}."}}"}""",
                GroupId = Group,
                Repository = "acme/shop",
            })).ActionId);
        }

        return ids;
    }

    private static ApproveCardIssuesRequest Card(string cardId, int count)
    {
        var card = new ApproveCardIssuesRequest { CardId = cardId, Repository = "acme/shop" };
        card.Issues.AddRange(Enumerable.Range(0, count).Select(i => new CardIssue { Title = $"Card finding {i}", Body = $"What, why, how and where of finding {i}." }));
        return card;
    }

    private static async Task<Dictionary<string, Scout.ControlPlane.Contracts.PendingAction>> ListedAsync(GrpcFeatures.FeaturesClient features) =>
        (await features.ListPendingActionsAsync(new ListPendingActionsRequest())).Actions.ToDictionary(a => a.Id);

    private static ActionSetRequest Set(Dictionary<string, Scout.ControlPlane.Contracts.PendingAction> listed, params IEnumerable<string> ids)
    {
        var set = new ActionSetRequest { GroupId = Group };
        set.Actions.AddRange(ids.Select(id => new ActionRef { Id = id, Digest = listed[id].PayloadDigest }));
        return set;
    }

    private sealed class Channels(Grpc.Net.Client.GrpcChannel desktop, Grpc.Net.Client.GrpcChannel worker) : IDisposable
    {
        public Grpc.Net.Client.GrpcChannel Desktop { get; } = desktop;

        public Grpc.Net.Client.GrpcChannel Worker { get; } = worker;

        public void Dispose()
        {
            Desktop.Dispose();
            Worker.Dispose();
        }
    }

    private sealed class MovableTime : TimeProvider
    {
        public TimeSpan Ahead { get; set; }

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + Ahead;
    }
}
