using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Features;
using Scout.ControlPlane.Operator;
using Scout.ControlPlane.WriteGate;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.WriteGate;

/// <summary>
/// The worker's write gate over the real catalog and Scout's own state, queue and
/// log files in a temp folder.
/// </summary>
public sealed class WriteGateServiceTests : IDisposable
{
    private const string Upload = "videos.insert";
    private const string DataFolder = "Scout's data folder";
    private const string NotAllowed =
        "The owner has not allowed youtube to write; allow it on the Features screen of Scout Desktop.";

    private readonly string _directory = Directory.CreateTempSubdirectory("scout-write-gate-").FullName;
    private readonly JsonFileFeatureStateStore _states;
    private readonly JsonFileActionQueue _actions;
    private readonly JsonLinesWriteLog _writes;
    private readonly FeaturesService _features;

    public WriteGateServiceTests()
    {
        var options = Options.Create(new FeatureStateStoreOptions { FilePath = Path.Combine(_directory, "features.json") });
        _states = new JsonFileFeatureStateStore(options, NullLogger<JsonFileFeatureStateStore>.Instance);
        _actions = new JsonFileActionQueue(options, NullLogger<JsonFileActionQueue>.Instance);
        _writes = new JsonLinesWriteLog(options, NullLogger<JsonLinesWriteLog>.Instance);
        _features = new FeaturesService(
            new FeatureCatalog(),
            _states,
            new InMemoryCredentialStore(),
            new NullFeatureActivator(),
            Array.Empty<IFeatureCredentialValidator>());
    }

    public void Dispose()
    {
        _states.Dispose();
        _actions.Dispose();
        _writes.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private WriteGateService Gate() => new(new FeatureCatalog(), _states, _actions, _writes);

    private Task<FeatureSnapshot> GrantYouTubeAsync() =>
        _features.SetWriteGrantAsync(FeatureCatalog.YouTubeId, granted: true, confirmWrites: true, CancellationToken.None);

    private WriteGateService GateAt(DateTimeOffset now, IWriteLog? log = null) =>
        new(new FeatureCatalog(), _states, _actions, log ?? _writes, new StubTime(now));

    private Task<PendingAction> ApproveAsync(string actionId) =>
        new ActionDecisionService(_states, _actions, _writes).ApproveAsync(actionId, reason: null, CancellationToken.None);

    [Theory]
    [InlineData("youtube", "videos.delete", "videos.delete is not declared by youtube")]
    [InlineData("github", "Contents.write", "Contents.write is not declared by github")]
    [InlineData("nope", "videos.insert", "videos.insert is not declared by nope")]
    [InlineData("", "videos.insert", "videos.insert is not declared by ")]
    [InlineData("youtube", "VIDEOS.INSERT", "VIDEOS.INSERT is not declared by youtube")]
    public async Task An_Undeclared_Write_Is_Refused_Even_With_The_Grant(string feature, string scope, string reason)
    {
        await GrantYouTubeAsync();

        var decision = await Gate().RequestAsync(feature, scope, "intro.mp4", "Upload", CancellationToken.None);

        decision.ShouldBe(new WriteDecision(WriteDecisionKind.Refused, reason));
        (await _actions.ListPendingAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Read_Row_Is_Refused_As_A_Write()
    {
        await GrantYouTubeAsync();

        var decision = await Gate().RequestAsync("youtube", "videos.list", "abc", "Read", CancellationToken.None);

        decision.ShouldBe(new WriteDecision(WriteDecisionKind.Refused, "videos.list is a read"));
    }

    [Theory]
    [InlineData(DataFolder)]
    [InlineData(Upload)]
    public async Task A_Declared_Write_Without_The_Grant_Is_Refused_Naming_The_Features_Screen(string scope)
    {
        var decision = await Gate().RequestAsync("youtube", scope, "intro.mp4", "Upload", CancellationToken.None);

        decision.ShouldBe(new WriteDecision(WriteDecisionKind.Refused, NotAllowed));
        (await _actions.ListPendingAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Reversible_Write_Under_The_Grant_Is_Allowed()
    {
        await GrantYouTubeAsync();

        var decision = await Gate().RequestAsync("youtube", DataFolder, "scripts/intro.md", "Save the script", CancellationToken.None);

        decision.ShouldBe(new WriteDecision(WriteDecisionKind.Allowed, string.Empty));
        (await _actions.ListPendingAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_Irreversible_Write_Under_The_Grant_Waits_For_The_Owners_Approval()
    {
        await GrantYouTubeAsync();

        var decision = await Gate().RequestAsync("youtube", Upload, "intro.mp4", "Upload 'Intro' as private", CancellationToken.None);

        decision.Kind.ShouldBe(WriteDecisionKind.Queued);
        decision.Reason.ShouldBe("Waiting for the owner's approval on the Review screen of Scout Desktop.");
        var pending = (await _actions.ListPendingAsync(CancellationToken.None)).ShouldHaveSingleItem();
        (pending.Id, pending.FeatureId, pending.Action, pending.Target, pending.Summary, pending.Status)
            .ShouldBe((decision.ActionId, "youtube", Upload, "intro.mp4", "Upload 'Intro' as private", PendingActionStatus.Pending));
    }

    [Fact]
    public async Task A_Grant_Given_After_The_Gate_Was_Built_Applies_To_The_Next_Request()
    {
        var gate = Gate();
        var before = await gate.RequestAsync("youtube", DataFolder, "a.md", "Save", CancellationToken.None);

        await GrantYouTubeAsync();
        var granted = await gate.RequestAsync("youtube", DataFolder, "a.md", "Save", CancellationToken.None);
        await _features.SetWriteGrantAsync(FeatureCatalog.YouTubeId, granted: false, confirmWrites: false, CancellationToken.None);
        var revoked = await gate.RequestAsync("youtube", DataFolder, "a.md", "Save", CancellationToken.None);

        before.Kind.ShouldBe(WriteDecisionKind.Refused);
        granted.Kind.ShouldBe(WriteDecisionKind.Allowed);
        revoked.ShouldBe(new WriteDecision(WriteDecisionKind.Refused, NotAllowed));
    }

    [Fact]
    public async Task RecordWrite_Logs_A_Reversible_Write_And_An_Approved_Action()
    {
        await GrantYouTubeAsync();
        var gate = Gate();
        var queued = await gate.RequestAsync("youtube", Upload, "intro.mp4", "Upload", CancellationToken.None);
        await _actions.ApproveAsync(queued.ActionId!, reason: null, CancellationToken.None);
        var before = DateTimeOffset.UtcNow;

        await gate.RecordAsync("youtube", DataFolder, "scripts/intro.md", "ok", actionId: null, CancellationToken.None);
        await gate.RecordAsync("youtube", Upload, "intro.mp4", "uploaded as private", queued.ActionId, CancellationToken.None);

        var logged = await _writes.ListAsync(10, CancellationToken.None);
        logged.Select(e => (e.FeatureId, e.Action, e.Target, e.Outcome, e.ApprovedBy, e.ActionId)).ShouldBe([
            ("youtube", Upload, "intro.mp4", "uploaded as private", "owner", queued.ActionId),
            ("youtube", DataFolder, "scripts/intro.md", "ok", null, null),
        ]);
        logged[0].At.ShouldBeInRange(before, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task RecordWrite_Consumes_The_Approved_Action_So_A_Second_Record_With_The_Same_Id_Fails_And_The_Action_Shows_Executed()
    {
        await GrantYouTubeAsync();
        var gate = Gate();
        var queued = await gate.RequestAsync("youtube", Upload, "intro.mp4", "Upload", CancellationToken.None);
        await _actions.ApproveAsync(queued.ActionId!, reason: null, CancellationToken.None);

        await gate.RecordAsync("youtube", Upload, "intro.mp4", "uploaded as private", queued.ActionId, CancellationToken.None);
        var second = await Should.ThrowAsync<WriteNotRecordableException>(() =>
            gate.RecordAsync("youtube", Upload, "intro.mp4", "uploaded again", queued.ActionId, CancellationToken.None));

        second.Message.ShouldBe($"Action '{queued.ActionId}' is not approved by the owner.");
        (await _writes.ListAsync(10, CancellationToken.None)).ShouldHaveSingleItem().Outcome.ShouldBe("uploaded as private");
        (await _actions.FindAsync(queued.ActionId!, CancellationToken.None)).ShouldNotBeNull().Status
            .ShouldBe(PendingActionStatus.Executed);
    }

    [Fact]
    public async Task Concurrent_RecordWrites_Of_One_Approved_Action_Record_It_Once()
    {
        await GrantYouTubeAsync();
        var gate = Gate();
        var queued = await gate.RequestAsync("youtube", Upload, "intro.mp4", "Upload", CancellationToken.None);
        await _actions.ApproveAsync(queued.ActionId!, reason: null, CancellationToken.None);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            try
            {
                await gate.RecordAsync("youtube", Upload, "intro.mp4", "ok", queued.ActionId, CancellationToken.None);
                return true;
            }
            catch (WriteNotRecordableException)
            {
                return false;
            }
        }));

        attempts.Count(recorded => recorded).ShouldBe(1);
        (await _writes.ListAsync(10, CancellationToken.None)).ShouldHaveSingleItem().ActionId.ShouldBe(queued.ActionId);
    }

    [Fact]
    public async Task RecordWrite_With_An_Action_The_Owner_Has_Not_Approved_Fails_And_Logs_Nothing()
    {
        await GrantYouTubeAsync();
        var gate = Gate();
        var pending = await gate.RequestAsync("youtube", Upload, "intro.mp4", "Upload", CancellationToken.None);
        var rejected = await gate.RequestAsync("youtube", Upload, "outro.mp4", "Upload", CancellationToken.None);
        await _actions.RejectAsync(rejected.ActionId!, "No", CancellationToken.None);

        var unapproved = await Should.ThrowAsync<WriteNotRecordableException>(() =>
            gate.RecordAsync("youtube", Upload, "intro.mp4", "ok", pending.ActionId, CancellationToken.None));
        var refused = await Should.ThrowAsync<WriteNotRecordableException>(() =>
            gate.RecordAsync("youtube", Upload, "outro.mp4", "ok", rejected.ActionId, CancellationToken.None));
        var unknown = await Should.ThrowAsync<WriteNotRecordableException>(() =>
            gate.RecordAsync("youtube", Upload, "x.mp4", "ok", "no-such-action", CancellationToken.None));

        unapproved.Message.ShouldBe($"Action '{pending.ActionId}' is not approved by the owner.");
        refused.Message.ShouldBe($"Action '{rejected.ActionId}' is not approved by the owner.");
        unknown.Message.ShouldBe("Action 'no-such-action' is not approved by the owner.");
        (await _writes.ListAsync(10, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task RecordWrite_With_An_Approved_Action_Of_Another_Scope_Fails()
    {
        await GrantYouTubeAsync();
        var gate = Gate();
        var upload = await gate.RequestAsync("youtube", Upload, "intro.mp4", "Upload", CancellationToken.None);
        await _actions.ApproveAsync(upload.ActionId!, reason: null, CancellationToken.None);

        var other = await Should.ThrowAsync<WriteNotRecordableException>(() =>
            gate.RecordAsync("youtube", "comments.insert", "comment 7", "ok", upload.ActionId, CancellationToken.None));

        other.Message.ShouldBe($"Action '{upload.ActionId}' was approved for youtube {Upload}, not youtube comments.insert.");
        (await _writes.ListAsync(10, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task RecordWrite_Of_An_Irreversible_Write_Without_An_Action_Id_Is_Refused_Even_With_The_Grant()
    {
        await GrantYouTubeAsync();

        var failed = await Should.ThrowAsync<WriteNotRecordableException>(() =>
            Gate().RecordAsync("youtube", Upload, "intro.mp4", "uploaded as private", actionId: null, CancellationToken.None));

        failed.Message.ShouldBe($"{Upload} is irreversible; it is recorded only with an action the owner approved.");
        (await _writes.ListAsync(10, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task RecordWrite_Of_A_Reversible_Write_Without_The_Grant_Fails_And_Logs_Nothing()
    {
        var failed = await Should.ThrowAsync<WriteNotRecordableException>(() =>
            Gate().RecordAsync("youtube", DataFolder, "scripts/intro.md", "ok", actionId: null, CancellationToken.None));

        failed.Message.ShouldBe(NotAllowed);
        (await _writes.ListAsync(10, CancellationToken.None)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("youtube", "videos.delete", "videos.delete is not declared by youtube")]
    [InlineData("youtube", "videos.list", "videos.list is a read")]
    public async Task RecordWrite_Of_An_Undeclared_Scope_Or_A_Read_Fails(string feature, string scope, string message)
    {
        var failed = await Should.ThrowAsync<WriteNotRecordableException>(() =>
            Gate().RecordAsync(feature, scope, "x", "ok", actionId: null, CancellationToken.None));

        failed.Message.ShouldBe(message);
        (await _writes.ListAsync(10, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task RecordWrite_Of_An_Executing_Action_After_The_Owner_Revoked_Logs_It_Once_Consumes_It_And_Is_Refused()
    {
        await GrantYouTubeAsync();
        var gate = Gate();
        var queued = await gate.RequestAsync("youtube", Upload, "intro.mp4", "Upload", CancellationToken.None);
        await _actions.ApproveAsync(queued.ActionId!, reason: null, CancellationToken.None);
        await _actions.ClaimNextAsync("youtube", TimeSpan.FromMinutes(30), (_, _) => true, CancellationToken.None);
        await _features.SetWriteGrantAsync(FeatureCatalog.YouTubeId, granted: false, confirmWrites: false, CancellationToken.None);

        var revoked = await Should.ThrowAsync<WriteNotRecordableException>(() =>
            gate.RecordAsync("youtube", Upload, "intro.mp4", "uploaded as private", queued.ActionId, CancellationToken.None));
        var again = await Should.ThrowAsync<WriteNotRecordableException>(() =>
            gate.RecordAsync("youtube", Upload, "intro.mp4", "uploaded as private", queued.ActionId, CancellationToken.None));

        revoked.Message.ShouldBe("The owner revoked youtube's write permission before this action ran.");
        again.Message.ShouldBe(revoked.Message);
        (await _writes.ListAsync(10, CancellationToken.None)).Select(e => (e.Action, e.Outcome, e.ApprovedBy, e.ActionId))
            .ShouldBe([(Upload, "uploaded as private after revoke", (string?)"owner", queued.ActionId)]);
        (await _actions.FindAsync(queued.ActionId!, CancellationToken.None)).ShouldNotBeNull().Status
            .ShouldBe(PendingActionStatus.Executed);
    }

    [Fact]
    public async Task RecordWrite_Of_An_Approved_Action_Never_Claimed_After_The_Owner_Revoked_Is_Refused_And_Logs_Nothing()
    {
        await GrantYouTubeAsync();
        var gate = Gate();
        var queued = await gate.RequestAsync("youtube", Upload, "intro.mp4", "Upload", CancellationToken.None);
        await _actions.ApproveAsync(queued.ActionId!, reason: null, CancellationToken.None);
        await _features.SetWriteGrantAsync(FeatureCatalog.YouTubeId, granted: false, confirmWrites: false, CancellationToken.None);

        var revoked = await Should.ThrowAsync<WriteNotRecordableException>(() =>
            gate.RecordAsync("youtube", Upload, "intro.mp4", "uploaded as private", queued.ActionId, CancellationToken.None));

        revoked.Message.ShouldBe("The owner revoked youtube's write permission before this action ran.");
        (await _writes.ListAsync(10, CancellationToken.None)).ShouldBeEmpty();
        (await _actions.FindAsync(queued.ActionId!, CancellationToken.None)).ShouldNotBeNull().Status
            .ShouldBe(PendingActionStatus.Approved);
    }

    [Fact]
    public async Task A_Failed_Write_Log_Append_Leaves_The_Action_Approved_And_A_Retry_Records_It_Once()
    {
        await GrantYouTubeAsync();
        var gate = Gate();
        var queued = await gate.RequestAsync("youtube", Upload, "intro.mp4", "Upload", CancellationToken.None);
        await _actions.ApproveAsync(queued.ActionId!, reason: null, CancellationToken.None);
        Directory.CreateDirectory(_writes.FilePath);

        await Should.ThrowAsync<UnauthorizedAccessException>(() =>
            gate.RecordAsync("youtube", Upload, "intro.mp4", "uploaded as private", queued.ActionId, CancellationToken.None));
        var afterFailure = (await _actions.FindAsync(queued.ActionId!, CancellationToken.None)).ShouldNotBeNull().Status;
        Directory.Delete(_writes.FilePath);
        await gate.RecordAsync("youtube", Upload, "intro.mp4", "uploaded as private", queued.ActionId, CancellationToken.None);

        afterFailure.ShouldBe(PendingActionStatus.Approved);
        (await _writes.ListAsync(10, CancellationToken.None)).ShouldHaveSingleItem().ActionId.ShouldBe(queued.ActionId);
        (await _actions.FindAsync(queued.ActionId!, CancellationToken.None)).ShouldNotBeNull().Status
            .ShouldBe(PendingActionStatus.Executed);
    }

    [Fact]
    public async Task A_Failed_Mark_After_The_Append_Keeps_The_Log_Line_And_The_Retry_Is_Refused_As_Already_Recorded()
    {
        await GrantYouTubeAsync();
        var gate = Gate();
        var queued = await gate.RequestAsync("youtube", Upload, "intro.mp4", "Upload", CancellationToken.None);
        await _actions.ApproveAsync(queued.ActionId!, reason: null, CancellationToken.None);
        var tempPath = _actions.FilePath + ".tmp";
        Directory.CreateDirectory(tempPath);

        await Should.ThrowAsync<UnauthorizedAccessException>(() =>
            gate.RecordAsync("youtube", Upload, "intro.mp4", "uploaded as private", queued.ActionId, CancellationToken.None));
        var afterFailure = (await _actions.FindAsync(queued.ActionId!, CancellationToken.None)).ShouldNotBeNull().Status;
        var loggedAfterFailure = await _writes.ListAsync(10, CancellationToken.None);
        Directory.Delete(tempPath);
        var retry = await Should.ThrowAsync<WriteNotRecordableException>(() =>
            gate.RecordAsync("youtube", Upload, "intro.mp4", "uploaded again", queued.ActionId, CancellationToken.None));

        afterFailure.ShouldBe(PendingActionStatus.Approved);
        loggedAfterFailure.ShouldHaveSingleItem().Outcome.ShouldBe("uploaded as private");
        retry.Message.ShouldBe($"Action '{queued.ActionId}' is already recorded in the write log.");
        (await _writes.ListAsync(10, CancellationToken.None)).ShouldHaveSingleItem().Outcome.ShouldBe("uploaded as private");
        (await _actions.FindAsync(queued.ActionId!, CancellationToken.None)).ShouldNotBeNull().Status
            .ShouldBe(PendingActionStatus.Executed);
    }

    [Fact]
    public async Task A_Write_Logged_But_Never_Marked_Is_Not_Handed_Out_Again_Once_Its_Claim_Lapses_And_The_Next_Action_Is()
    {
        await GrantYouTubeAsync();
        var gate = Gate();
        var upload = await gate.RequestAsync("youtube", Upload, "intro.mp4", "Upload", CancellationToken.None);
        var retitle = await gate.RequestAsync("youtube", "videos.update", "UC1/v1", "Retitle v1", CancellationToken.None);
        await ApproveAsync(upload.ActionId!);
        var claimed = (await gate.ClaimNextApprovedAsync("youtube", CancellationToken.None)).ShouldNotBeNull();
        var tempPath = _actions.FilePath + ".tmp";
        Directory.CreateDirectory(tempPath);
        await Should.ThrowAsync<UnauthorizedAccessException>(() =>
            gate.RecordAsync("youtube", Upload, "intro.mp4", "uploaded as private", claimed.Id, CancellationToken.None));
        Directory.Delete(tempPath);
        var afterFailure = (await _actions.FindAsync(claimed.Id, CancellationToken.None)).ShouldNotBeNull().Status;
        await ApproveAsync(retitle.ActionId!);
        await LapseClaimAsync(claimed.Id);

        var next = await gate.ClaimNextApprovedAsync("youtube", CancellationToken.None);
        var none = await gate.ClaimNextApprovedAsync("youtube", CancellationToken.None);

        claimed.Id.ShouldBe(upload.ActionId);
        afterFailure.ShouldBe(PendingActionStatus.Executing);
        next.ShouldNotBeNull().Id.ShouldBe(retitle.ActionId, "the upload ran once and is in the write log, so its lapsed claim is not handed out again");
        none.ShouldBeNull();
        var settled = (await _actions.FindAsync(claimed.Id, CancellationToken.None)).ShouldNotBeNull();
        (settled.Status, settled.Outcome).ShouldBe((PendingActionStatus.Executed, "uploaded as private"));
        (await _writes.ListAsync(10, CancellationToken.None)).Select(e => (e.ActionId, e.Outcome)).ShouldBe([
            (retitle.ActionId, "approved"),
            (upload.ActionId, "uploaded as private"),
            (upload.ActionId, "approved"),
        ]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Write_Logged_But_Never_Marked_Is_Settled_As_Executed_Not_Rejected_As_Never_Finished_After_A_Revoke(bool grantedAgain)
    {
        await GrantYouTubeAsync();
        var gate = Gate();
        var upload = await gate.RequestAsync("youtube", Upload, "intro.mp4", "Upload", CancellationToken.None);
        await ApproveAsync(upload.ActionId!);
        var claimed = (await gate.ClaimNextApprovedAsync("youtube", CancellationToken.None)).ShouldNotBeNull();
        var tempPath = _actions.FilePath + ".tmp";
        Directory.CreateDirectory(tempPath);
        await Should.ThrowAsync<UnauthorizedAccessException>(() =>
            gate.RecordAsync("youtube", Upload, "intro.mp4", "uploaded as private", claimed.Id, CancellationToken.None));
        Directory.Delete(tempPath);
        await _features.SetWriteGrantAsync(FeatureCatalog.YouTubeId, granted: false, confirmWrites: false, CancellationToken.None);
        if (grantedAgain)
        {
            await GrantYouTubeAsync();
        }

        await LapseClaimAsync(claimed.Id);

        var next = await gate.ClaimNextApprovedAsync("youtube", CancellationToken.None);

        next.ShouldBeNull();
        var settled = (await _actions.FindAsync(claimed.Id, CancellationToken.None)).ShouldNotBeNull();
        (settled.Status, settled.Outcome, settled.Reason).ShouldBe((PendingActionStatus.Executed, "uploaded as private", (string?)null));
        (await _writes.ListAsync(10, CancellationToken.None)).Select(e => e.Outcome).ShouldBe(["uploaded as private", "approved"]);
    }

    [Fact]
    public async Task A_Claim_Moves_On_To_The_Next_Action_When_A_Late_Record_Marks_The_Logged_One_Executed_Meanwhile()
    {
        await GrantYouTubeAsync();
        var gate = Gate();
        var upload = await gate.RequestAsync("youtube", Upload, "intro.mp4", "Upload", CancellationToken.None);
        var retitle = await gate.RequestAsync("youtube", "videos.update", "UC1/v1", "Retitle v1", CancellationToken.None);
        await ApproveAsync(upload.ActionId!);
        var claimed = (await gate.ClaimNextApprovedAsync("youtube", CancellationToken.None)).ShouldNotBeNull();
        var tempPath = _actions.FilePath + ".tmp";
        Directory.CreateDirectory(tempPath);
        await Should.ThrowAsync<UnauthorizedAccessException>(() =>
            gate.RecordAsync("youtube", Upload, "intro.mp4", "uploaded as private", claimed.Id, CancellationToken.None));
        Directory.Delete(tempPath);
        await ApproveAsync(retitle.ActionId!);
        await LapseClaimAsync(claimed.Id);
        var log = Substitute.For<IWriteLog>();
        log.ListSinceAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var entries = await _writes.ListSinceAsync(call.Arg<DateTimeOffset>(), CancellationToken.None);
            if ((await _actions.FindAsync(claimed.Id, CancellationToken.None))?.Status == PendingActionStatus.Executing)
            {
                await _actions.MarkExecutedAsync(claimed.Id, CancellationToken.None);
            }

            return entries;
        });

        var racing = new WriteGateService(new FeatureCatalog(), _states, _actions, log);
        PendingAction? next = null;

        await Should.NotThrowAsync(async () => next = await racing.ClaimNextApprovedAsync("youtube", CancellationToken.None));

        next.ShouldNotBeNull().Id.ShouldBe(retitle.ActionId);
        (await _actions.FindAsync(claimed.Id, CancellationToken.None)).ShouldNotBeNull().Status.ShouldBe(PendingActionStatus.Executed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Poll_With_Nothing_Claimed_Reads_The_Queue_Once_For_Lapsed_Claims_And_Once_For_The_Claim(bool oneActionRunsWithinItsLease)
    {
        await GrantYouTubeAsync();
        var now = DateTimeOffset.UtcNow;
        IReadOnlyList<PendingAction> open = oneActionRunsWithinItsLease
            ? [new PendingAction("a1", "youtube", Upload, "intro.mp4", "Upload", now, PendingActionStatus.Executing, now) { ClaimedAt = now }]
            : [];
        var queue = Substitute.For<IActionQueue>();
        queue.ListOpenAsync("youtube", Arg.Any<CancellationToken>()).Returns(Task.FromResult(open));
        queue.RejectExecutingAsync("youtube", Arg.Any<Func<PendingAction, bool>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PendingAction>>([]));

        var next = await new WriteGateService(new FeatureCatalog(), _states, queue, _writes).ClaimNextApprovedAsync("youtube", CancellationToken.None);

        next.ShouldBeNull();
        queue.ReceivedCalls().Select(call => call.GetMethodInfo().Name).ShouldBe([nameof(IActionQueue.ListOpenAsync), nameof(IActionQueue.ClaimNextAsync)]);
    }

    [Fact]
    public async Task A_Claim_That_Cannot_Read_The_Write_Log_Fails_And_Hands_The_Action_Out_Only_After_Its_Lease()
    {
        await GrantYouTubeAsync();
        var gate = Gate();
        var queued = await gate.RequestAsync("youtube", Upload, "intro.mp4", "Upload", CancellationToken.None);
        await ApproveAsync(queued.ActionId!);

        await using (new FileStream(_writes.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Should.ThrowAsync<IOException>(() => gate.ClaimNextApprovedAsync("youtube", CancellationToken.None));
        }

        var withinTheLease = await gate.ClaimNextApprovedAsync("youtube", CancellationToken.None);
        var held = (await _actions.FindAsync(queued.ActionId!, CancellationToken.None)).ShouldNotBeNull();
        await LapseClaimAsync(queued.ActionId!);
        var afterTheLease = await gate.ClaimNextApprovedAsync("youtube", CancellationToken.None);

        withinTheLease.ShouldBeNull();
        held.Status.ShouldBe(PendingActionStatus.Executing);
        afterTheLease.ShouldNotBeNull().Id.ShouldBe(queued.ActionId);
    }

    [Theory]
    [InlineData("github", "Pull request reviews", false, false, "failed: not confirmed")]
    [InlineData("github", "Pull request verdicts", false, false, "failed: not confirmed")]
    [InlineData("github", "Pull request comments", false, false, "failed: not confirmed")]
    [InlineData("github", "New issues", false, false, "failed: not confirmed")]
    [InlineData("github", "Issue comments", false, false, "failed: not confirmed")]
    [InlineData("github", "New issues", true, false, "succeeded")]
    [InlineData("youtube", "videos.insert", true, false, "succeeded")]
    [InlineData("youtube", "videos.insert", false, true, null)]
    public async Task A_Claim_That_Lapsed_Is_Settled_By_The_Write_In_The_Log_First_Then_Failed_As_Not_Confirmed_On_A_GitHub_Row_And_Only_Otherwise_Handed_Out_Again(
        string feature, string row, bool writeInTheLog, bool handedOutAgain, string? outcome)
    {
        await _features.SetWriteGrantAsync(feature, granted: true, confirmWrites: true, CancellationToken.None);
        var gate = Gate();
        var asked = await gate.RequestAsync(feature, row, "the target", "the summary", CancellationToken.None);
        await ApproveAsync(asked.ActionId!);
        var first = (await gate.ClaimNextApprovedAsync(feature, CancellationToken.None)).ShouldNotBeNull();
        if (writeInTheLog)
        {
            await _writes.AppendAsync(
                new WriteLogEntry(DateTimeOffset.UtcNow, feature, row, "the target", "succeeded", "owner", first.Id), CancellationToken.None);
        }

        await LapseClaimAsync(first.Id);

        var again = await gate.ClaimNextApprovedAsync(feature, CancellationToken.None);
        var later = await gate.ClaimNextApprovedAsync(feature, CancellationToken.None);

        first.Id.ShouldBe(asked.ActionId);
        (again?.Id, later?.Id).ShouldBe((handedOutAgain ? first.Id : null, (string?)null));
        var ended = (await _actions.FindAsync(first.Id, CancellationToken.None)).ShouldNotBeNull();
        var notConfirmed = outcome == WriteGateService.NotConfirmedOutcome
            ? "Scout stopped before it recorded this, so it may already be on GitHub: look there before asking for it again."
            : null;
        (ended.Status, ended.Outcome, ended.Result).ShouldBe(
            (handedOutAgain ? PendingActionStatus.Executing : PendingActionStatus.Executed, outcome, notConfirmed));
        (await _writes.ListAsync(10, CancellationToken.None)).Select(e => e.Outcome)
            .ShouldBe(handedOutAgain ? ["approved"] : [outcome!, "approved"]);
    }

    [Fact]
    public async Task A_Lapsed_GitHub_Claim_Whose_Not_Confirmed_Record_Cannot_Be_Logged_Fails_The_Claim_Hands_Out_Nothing_Stays_Executing_And_Ends_Not_Confirmed_After_Its_Next_Lease()
    {
        await _features.SetWriteGrantAsync(FeatureCatalog.GitHubId, granted: true, confirmWrites: true, CancellationToken.None);
        var gate = Gate();
        var asked = await gate.RequestAsync("github", "New issues", "the target", "the summary", CancellationToken.None);
        await ApproveAsync(asked.ActionId!);
        var first = (await gate.ClaimNextApprovedAsync("github", CancellationToken.None)).ShouldNotBeNull();
        await LapseClaimAsync(first.Id);
        var log = Substitute.For<IWriteLog>();
        log.ListSinceAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(call => _writes.ListSinceAsync(call.Arg<DateTimeOffset>(), CancellationToken.None));
        log.AppendAsync(Arg.Any<WriteLogEntry>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new IOException("The disk is full.")));
        var failing = new WriteGateService(new FeatureCatalog(), _states, _actions, log);

        var failure = await Should.ThrowAsync<IOException>(() => failing.ClaimNextApprovedAsync("github", CancellationToken.None));
        var afterFailure = (await _actions.FindAsync(first.Id, CancellationToken.None)).ShouldNotBeNull();
        var withinTheLease = await gate.ClaimNextApprovedAsync("github", CancellationToken.None);
        await LapseClaimAsync(first.Id);
        var afterTheLease = await gate.ClaimNextApprovedAsync("github", CancellationToken.None);

        failure.Message.ShouldBe("The disk is full.");
        (afterFailure.Status, afterFailure.Outcome).ShouldBe((PendingActionStatus.Executing, (string?)null));
        (withinTheLease, afterTheLease).ShouldBe(((PendingAction?)null, (PendingAction?)null), "an action that may have been sent is never handed out again");
        var ended = (await _actions.FindAsync(first.Id, CancellationToken.None)).ShouldNotBeNull();
        (ended.Status, ended.Outcome, ended.Result).ShouldBe((
            PendingActionStatus.Executed,
            WriteGateService.NotConfirmedOutcome,
            "Scout stopped before it recorded this, so it may already be on GitHub: look there before asking for it again."));
        (await _writes.ListAsync(10, CancellationToken.None)).Select(e => e.Outcome).ShouldBe([WriteGateService.NotConfirmedOutcome, "approved"]);
    }

    [Fact]
    public async Task A_Lapsed_GitHub_Claim_Whose_Not_Confirmed_Record_Is_Logged_But_Cannot_Be_Saved_In_The_Queue_Fails_The_Claim_Hands_Out_Nothing_And_Is_Settled_From_The_Log_After_Its_Next_Lease()
    {
        await _features.SetWriteGrantAsync(FeatureCatalog.GitHubId, granted: true, confirmWrites: true, CancellationToken.None);
        var gate = Gate();
        var asked = await gate.RequestAsync("github", "New issues", "the target", "the summary", CancellationToken.None);
        await ApproveAsync(asked.ActionId!);
        var first = (await gate.ClaimNextApprovedAsync("github", CancellationToken.None)).ShouldNotBeNull();
        await LapseClaimAsync(first.Id);
        var tempPath = _actions.FilePath + ".tmp";
        var log = Substitute.For<IWriteLog>();
        log.ListSinceAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(call => _writes.ListSinceAsync(call.Arg<DateTimeOffset>(), CancellationToken.None));
        log.AppendAsync(Arg.Any<WriteLogEntry>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await _writes.AppendAsync(call.Arg<WriteLogEntry>(), CancellationToken.None);
            Directory.CreateDirectory(tempPath);
        });
        var failing = new WriteGateService(new FeatureCatalog(), _states, _actions, log);

        await Should.ThrowAsync<UnauthorizedAccessException>(() => failing.ClaimNextApprovedAsync("github", CancellationToken.None));
        Directory.Delete(tempPath);
        var afterFailure = (await _actions.FindAsync(first.Id, CancellationToken.None)).ShouldNotBeNull();
        var loggedAfterFailure = (await _writes.ListAsync(10, CancellationToken.None)).Select(e => e.Outcome).ToList();
        var withinTheLease = await gate.ClaimNextApprovedAsync("github", CancellationToken.None);
        await LapseClaimAsync(first.Id);
        var afterTheLease = await gate.ClaimNextApprovedAsync("github", CancellationToken.None);

        (afterFailure.Status, afterFailure.Outcome).ShouldBe((PendingActionStatus.Executing, (string?)null));
        loggedAfterFailure.ShouldBe([WriteGateService.NotConfirmedOutcome, "approved"]);
        (withinTheLease, afterTheLease).ShouldBe(((PendingAction?)null, (PendingAction?)null), "an action that may have been sent is never handed out again");
        var ended = (await _actions.FindAsync(first.Id, CancellationToken.None)).ShouldNotBeNull();
        (ended.Status, ended.Outcome).ShouldBe((PendingActionStatus.Executed, WriteGateService.NotConfirmedOutcome));
        (await _writes.ListAsync(10, CancellationToken.None)).Select(e => e.Outcome)
            .ShouldBe([WriteGateService.NotConfirmedOutcome, "approved"], "the record is in the log once, not once per claim");
    }

    private async Task LapseClaimAsync(string actionId)
    {
        var file = JsonNode.Parse(await File.ReadAllTextAsync(_actions.FilePath))!.AsArray();
        file.Single(a => (string)a!["id"]! == actionId)!["claimedAt"] = (DateTimeOffset.UtcNow - WriteGateService.ClaimLease - TimeSpan.FromMinutes(1)).ToString("O");
        await File.WriteAllTextAsync(_actions.FilePath, file.ToJsonString());
    }

    [Fact]
    public async Task A_Feature_That_Runs_Its_Own_Actions_Claims_The_Oldest_Approved_One_Requested_Under_The_Grant()
    {
        var gate = Gate();
        var before = await _actions.EnqueueAsync(FeatureCatalog.YouTubeId, "videos.insert", "UC1/intro.mp4", "before the grant", CancellationToken.None);
        await _actions.ApproveAsync(before.Id, null, CancellationToken.None);
        await GrantYouTubeAsync();
        var first = await gate.RequestAsync(FeatureCatalog.YouTubeId, "videos.insert", "UC1/intro.mp4", "Upload intro.mp4", CancellationToken.None);
        var second = await gate.RequestAsync(FeatureCatalog.YouTubeId, "videos.update", "UC1/v1", "Retitle v1", CancellationToken.None);
        first.Kind.ShouldBe(WriteDecisionKind.Queued);
        second.Kind.ShouldBe(WriteDecisionKind.Queued);

        (await gate.ClaimNextApprovedAsync(FeatureCatalog.YouTubeId, CancellationToken.None)).ShouldBeNull("nothing is approved yet");
        await ApproveAsync(second.ActionId!);
        await ApproveAsync(first.ActionId!);

        var claimed = (await gate.ClaimNextApprovedAsync(FeatureCatalog.YouTubeId, CancellationToken.None)).ShouldNotBeNull();
        claimed.Id.ShouldBe(first.ActionId, "oldest approved first; the one requested before the grant is never handed out");
        claimed.Status.ShouldBe(PendingActionStatus.Executing);
        (await gate.ClaimNextApprovedAsync(FeatureCatalog.YouTubeId, CancellationToken.None)).ShouldNotBeNull().Id.ShouldBe(second.ActionId);
        (await gate.ClaimNextApprovedAsync(FeatureCatalog.YouTubeId, CancellationToken.None)).ShouldBeNull("both are claimed within the lease");
        (await gate.ClaimNextApprovedAsync(FeatureCatalog.YouTubeId, CancellationToken.None)).ShouldBeNull("another feature's queue is untouched");
    }

    [Fact]
    public async Task Without_The_Grant_No_Action_Is_Handed_Out_Even_When_One_Is_Approved()
    {
        var gate = Gate();
        var action = await _actions.EnqueueAsync(FeatureCatalog.YouTubeId, "videos.update", "UC1/v1", "Retitle v1", CancellationToken.None);
        await _actions.ApproveAsync(action.Id, null, CancellationToken.None);

        (await gate.ClaimNextApprovedAsync(FeatureCatalog.YouTubeId, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task A_Claimed_Action_Is_Recorded_Once_Against_Its_Id()
    {
        var gate = Gate();
        await GrantYouTubeAsync();
        var queued = await gate.RequestAsync(FeatureCatalog.YouTubeId, "videos.update", "UC1/v1", "Retitle v1", CancellationToken.None);
        await ApproveAsync(queued.ActionId!);
        var claimed = (await gate.ClaimNextApprovedAsync(FeatureCatalog.YouTubeId, CancellationToken.None)).ShouldNotBeNull();

        await gate.RecordAsync(FeatureCatalog.YouTubeId, "videos.update", "UC1/v1", "succeeded", claimed.Id, CancellationToken.None);

        (await _actions.FindAsync(claimed.Id, CancellationToken.None)).ShouldNotBeNull().Status.ShouldBe(PendingActionStatus.Executed);
        var entry = (await _writes.ListSinceAsync(DateTimeOffset.MinValue, CancellationToken.None)).Single(e => e.ActionId == claimed.Id && e.Outcome == "succeeded");
        entry.Action.ShouldBe("videos.update");
        await Should.ThrowAsync<WriteNotRecordableException>(
            () => gate.RecordAsync(FeatureCatalog.YouTubeId, "videos.update", "UC1/v1", "succeeded", claimed.Id, CancellationToken.None));
    }

    [Fact]
    public async Task An_Approved_Action_Whose_Approval_Is_Not_In_The_Write_Log_Is_Not_Handed_Out_And_Is_Rejected_With_The_Rejection_Logged()
    {
        await GrantYouTubeAsync();
        var gate = Gate();
        var unlogged = await gate.RequestAsync("youtube", Upload, "intro.mp4", "Upload", CancellationToken.None);
        var logged = await gate.RequestAsync("youtube", "videos.update", "UC1/v1", "Retitle v1", CancellationToken.None);
        await _actions.ApproveAsync(unlogged.ActionId!, reason: null, CancellationToken.None);
        await new ActionDecisionService(_states, _actions, _writes).ApproveAsync(logged.ActionId!, reason: null, CancellationToken.None);
        var pastTheBound = GateAt(DateTimeOffset.UtcNow + WriteGateService.ApprovalLogWindow);

        var next = await pastTheBound.ClaimNextApprovedAsync("youtube", CancellationToken.None);
        var none = await pastTheBound.ClaimNextApprovedAsync("youtube", CancellationToken.None);

        next.ShouldNotBeNull().Id.ShouldBe(logged.ActionId, "the older action has no approval in the write log, so it is never handed to the worker");
        none.ShouldBeNull();
        var rejected = (await _actions.FindAsync(unlogged.ActionId!, CancellationToken.None)).ShouldNotBeNull();
        (rejected.Status, rejected.Reason).ShouldBe((PendingActionStatus.Rejected, "its approval is not in the write log; approve it again"));
        (await _writes.ListAsync(10, CancellationToken.None)).Select(e => (e.ActionId, e.Outcome)).ShouldBe([
            (unlogged.ActionId, "rejected: its approval is not in the write log; approve it again"),
            (logged.ActionId, "approved"),
        ]);
    }

    [Fact]
    public async Task A_Claim_That_Cannot_Mark_A_Logged_Write_Executed_Fails_Hands_Out_Nothing_And_Settles_It_After_Its_Lease()
    {
        await GrantYouTubeAsync();
        var gate = Gate();
        var upload = await gate.RequestAsync("youtube", Upload, "intro.mp4", "Upload", CancellationToken.None);
        await ApproveAsync(upload.ActionId!);
        var claimed = (await gate.ClaimNextApprovedAsync("youtube", CancellationToken.None)).ShouldNotBeNull();
        var blocked = Directory.CreateDirectory(_actions.FilePath + ".tmp");
        await Should.ThrowAsync<UnauthorizedAccessException>(() =>
            gate.RecordAsync("youtube", Upload, "intro.mp4", "uploaded as private", claimed.Id, CancellationToken.None));
        blocked.Delete();
        await LapseClaimAsync(claimed.Id);
        var log = Substitute.For<IWriteLog>();
        log.ListSinceAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            blocked.Create();
            return _writes.ListSinceAsync(call.Arg<DateTimeOffset>(), CancellationToken.None);
        });

        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => new WriteGateService(new FeatureCatalog(), _states, _actions, log).ClaimNextApprovedAsync("youtube", CancellationToken.None));
        blocked.Delete();
        var held = (await _actions.FindAsync(claimed.Id, CancellationToken.None)).ShouldNotBeNull().Status;
        var withinTheLease = await gate.ClaimNextApprovedAsync("youtube", CancellationToken.None);
        await LapseClaimAsync(claimed.Id);
        var afterTheLease = await gate.ClaimNextApprovedAsync("youtube", CancellationToken.None);

        held.ShouldBe(PendingActionStatus.Executing);
        withinTheLease.ShouldBeNull();
        afterTheLease.ShouldBeNull();
        var settled = (await _actions.FindAsync(claimed.Id, CancellationToken.None)).ShouldNotBeNull();
        (settled.Status, settled.Outcome).ShouldBe((PendingActionStatus.Executed, "uploaded as private"));
        (await _writes.ListAsync(10, CancellationToken.None)).Select(e => e.Outcome).ShouldBe(["uploaded as private", "approved"]);
    }

    [Theory]
    [InlineData("the Approve of a waiting action", "youtube")]
    [InlineData("a card verdict", "github")]
    [InlineData("the operator's approve", "youtube")]
    public async Task A_Claim_That_Lands_Between_The_Save_Of_An_Approval_And_Its_Log_Entry_Does_Not_Reject_The_Action_And_The_Next_Claim_Hands_It_Out(
        string producer, string feature)
    {
        await _features.SetWriteGrantAsync(feature, granted: true, confirmWrites: true, CancellationToken.None);
        var waiting = producer == "the Approve of a waiting action"
            ? (await Gate().RequestAsync("youtube", Upload, "intro.mp4", "Upload", CancellationToken.None)).ActionId
            : null;
        WriteGateService? gate = null;
        PendingAction? handedOutBetween = null;
        PendingAction? leftBetween = null;
        var log = Substitute.For<IWriteLog>();
        log.AppendAsync(Arg.Any<WriteLogEntry>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            if (gate is null)
            {
                var saved = (await _actions.ListOpenAsync(feature, CancellationToken.None)).ShouldHaveSingleItem();
                gate = GateAt(saved.DecidedAt.ShouldNotBeNull() + WriteGateService.ApprovalLogWindow - TimeSpan.FromTicks(1));
                handedOutBetween = await gate.ClaimNextApprovedAsync(feature, CancellationToken.None);
                leftBetween = await _actions.FindAsync(saved.Id, CancellationToken.None);
            }

            await _writes.AppendAsync(call.Arg<WriteLogEntry>(), CancellationToken.None);
        });
        using var store = new JsonFileOperatorStore(
            Options.Create(new FeatureStateStoreOptions { FilePath = Path.Combine(_directory, "features.json") }),
            NullLogger<JsonFileOperatorStore>.Instance);

        var approved = producer switch
        {
            "the Approve of a waiting action" =>
                await new ActionDecisionService(_states, _actions, log).ApproveAsync(waiting!, reason: null, CancellationToken.None),
            "a card verdict" => await new PullRequestVerdictService(_states, _actions, log).GiveAsync(
                new PullRequestVerdictRequest(
                    "octocat", "hello", 42, "approve", "Ready to merge.", [new VerdictComment("src/Checkout.cs", 3, "c is new")],
                    "0123456789abcdef0123456789abcdef01234567", "Tests: 3 checks passed."),
                CancellationToken.None),
            _ => (await new OperatorService(store, new WriteGateService(new FeatureCatalog(), _states, _actions, log), log)
                .ApproveAsync([(Upload, "p1", "Upload")], CancellationToken.None)).Single(),
        };
        var next = await gate.ShouldNotBeNull().ClaimNextApprovedAsync(feature, CancellationToken.None);

        handedOutBetween.ShouldBeNull();
        var left = leftBetween.ShouldNotBeNull();
        (left.Status, left.ClaimedAt, left.Reason).ShouldBe((PendingActionStatus.Approved, null, null));
        next.ShouldNotBeNull().Id.ShouldBe(approved.Id);
        (await _writes.ListAsync(10, CancellationToken.None)).Select(e => (e.ActionId, e.Outcome)).ShouldBe([(approved.Id, "approved")]);
    }

    [Fact]
    public async Task An_Approved_Action_Whose_Approval_Never_Reaches_The_Write_Log_Waits_Out_The_Bound_Then_Is_Rejected_With_The_Rejection_Logged_And_Is_Never_Handed_Out()
    {
        await GrantYouTubeAsync();
        var queued = await Gate().RequestAsync("youtube", Upload, "intro.mp4", "Upload", CancellationToken.None);
        var approvedAt = (await _actions.ApproveAsync(queued.ActionId!, reason: null, CancellationToken.None)).DecidedAt.ShouldNotBeNull();

        var withinTheBound = await GateAt(approvedAt + WriteGateService.ApprovalLogWindow - TimeSpan.FromTicks(1))
            .ClaimNextApprovedAsync("youtube", CancellationToken.None);
        var waiting = (await _actions.FindAsync(queued.ActionId!, CancellationToken.None)).ShouldNotBeNull();
        var loggedWhileWaiting = await _writes.ListAsync(10, CancellationToken.None);
        var atTheBound = await GateAt(approvedAt + WriteGateService.ApprovalLogWindow).ClaimNextApprovedAsync("youtube", CancellationToken.None);

        withinTheBound.ShouldBeNull();
        (waiting.Status, waiting.ClaimedAt, waiting.Reason).ShouldBe((PendingActionStatus.Approved, null, null));
        loggedWhileWaiting.ShouldBeEmpty();
        atTheBound.ShouldBeNull();
        var rejected = (await _actions.FindAsync(queued.ActionId!, CancellationToken.None)).ShouldNotBeNull();
        (rejected.Status, rejected.Reason).ShouldBe((PendingActionStatus.Rejected, "its approval is not in the write log; approve it again"));
        (await _writes.ListAsync(10, CancellationToken.None)).Select(e => (e.ActionId, e.Outcome, e.ApprovedBy))
            .ShouldBe([(queued.ActionId, "rejected: its approval is not in the write log; approve it again", "owner")]);
    }

    [Fact]
    public async Task A_Claim_That_Cannot_Return_A_Just_Approved_Action_To_The_Queue_Fails_Hands_Out_Nothing_And_Hands_It_Out_After_Its_Lease_Once_Its_Approval_Is_Logged()
    {
        await GrantYouTubeAsync();
        var queued = await Gate().RequestAsync("youtube", Upload, "intro.mp4", "Upload", CancellationToken.None);
        var approvedAt = (await _actions.ApproveAsync(queued.ActionId!, reason: null, CancellationToken.None)).DecidedAt.ShouldNotBeNull();
        var blocked = new DirectoryInfo(_actions.FilePath + ".tmp");
        var log = Substitute.For<IWriteLog>();
        log.ListSinceAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            blocked.Create();
            return _writes.ListSinceAsync(call.Arg<DateTimeOffset>(), CancellationToken.None);
        });

        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => GateAt(approvedAt + WriteGateService.ApprovalLogWindow - TimeSpan.FromTicks(1), log).ClaimNextApprovedAsync("youtube", CancellationToken.None));
        blocked.Delete();
        var held = (await _actions.FindAsync(queued.ActionId!, CancellationToken.None)).ShouldNotBeNull();
        await _writes.AppendAsync(
            new WriteLogEntry(DateTimeOffset.UtcNow, "youtube", Upload, "intro.mp4", "approved", "owner", queued.ActionId), CancellationToken.None);
        var withinTheLease = await Gate().ClaimNextApprovedAsync("youtube", CancellationToken.None);
        await LapseClaimAsync(queued.ActionId!);
        var afterTheLease = await Gate().ClaimNextApprovedAsync("youtube", CancellationToken.None);

        (held.Status, held.Reason).ShouldBe((PendingActionStatus.Executing, null));
        withinTheLease.ShouldBeNull();
        afterTheLease.ShouldNotBeNull().Id.ShouldBe(queued.ActionId);
        (await _writes.ListAsync(10, CancellationToken.None)).Select(e => e.Outcome).ShouldBe(["approved"]);
    }

    [Fact]
    public async Task RecordWrite_With_An_Approved_Action_Of_Another_Target_Fails_And_Leaves_It_Approved()
    {
        await GrantYouTubeAsync();
        var gate = Gate();
        var upload = await gate.RequestAsync("youtube", Upload, "intro.mp4", "Upload", CancellationToken.None);
        await _actions.ApproveAsync(upload.ActionId!, reason: null, CancellationToken.None);

        var other = await Should.ThrowAsync<WriteNotRecordableException>(() =>
            gate.RecordAsync("youtube", Upload, "outro.mp4", "ok", upload.ActionId, CancellationToken.None));

        other.Message.ShouldBe($"Action '{upload.ActionId}' was approved for intro.mp4, not outro.mp4.");
        (await _writes.ListAsync(10, CancellationToken.None)).ShouldBeEmpty();
        (await _actions.FindAsync(upload.ActionId!, CancellationToken.None))!.Status.ShouldBe(PendingActionStatus.Approved);
    }

    [Fact]
    public async Task A_Request_With_A_Group_And_A_Repository_Queues_The_Action_In_The_Request_Group_Never_One_The_Payload_Names()
    {
        await _features.SetWriteGrantAsync(FeatureCatalog.GitHubId, granted: true, confirmWrites: true, CancellationToken.None);
        var gate = Gate();
        const string Payload = """{"body":"b","group":"other","groupId":"other","owner":"evil","repo":"x","repository":"evil/x","title":"t"}""";

        var grouped = await gate.RequestAsync("github", "New issues", "GitHubActions.create_issue", "s", Payload, "turn-1", "acme/shop", CancellationToken.None);
        var ungrouped = await gate.RequestAsync("github", "New issues", "GitHubActions.create_issue", "s", Payload, CancellationToken.None);

        var inGroup = (await _actions.FindAsync(grouped.ActionId!, CancellationToken.None)).ShouldNotBeNull();
        (inGroup.GroupId, inGroup.Repository, inGroup.Payload).ShouldBe(("turn-1", "acme/shop", Payload));
        var single = (await _actions.FindAsync(ungrouped.ActionId!, CancellationToken.None)).ShouldNotBeNull();
        (single.GroupId, single.Repository, single.Status).ShouldBe(((string?)null, (string?)null, PendingActionStatus.Pending));
    }

    [Fact]
    public async Task Closing_A_Group_Stamps_Every_Action_Of_That_Group_Id_Only_Keeps_The_First_Stamp_And_Decides_None()
    {
        await _features.SetWriteGrantAsync(FeatureCatalog.GitHubId, granted: true, confirmWrites: true, CancellationToken.None);
        var gate = Gate();
        async Task<string> AskAsync(string? group, string? repository) =>
            (await gate.RequestAsync("github", "New issues", "GitHubActions.create_issue", "s", "{}", group, repository, CancellationToken.None)).ActionId!;
        var shop = await AskAsync("turn-1", "acme/shop");
        var site = await AskAsync("turn-1", "acme/site");
        var otherTurn = await AskAsync("turn-2", "acme/shop");
        var single = await AskAsync(null, null);

        var earlierClose = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
        var file = JsonNode.Parse(await File.ReadAllTextAsync(_actions.FilePath))!.AsArray();
        file.Single(row => (string?)row!["id"] == shop)!["groupClosedAt"] = earlierClose.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        await File.WriteAllTextAsync(_actions.FilePath, file.ToJsonString());

        var held = await gate.CloseGroupAsync("turn-1", CancellationToken.None);

        held.ShouldBe(2);
        var rows = (await _actions.ListAllAsync("github", CancellationToken.None)).ToDictionary(a => a.Id);
        rows[shop].GroupClosedAt.ShouldBe(earlierClose);
        rows[site].GroupClosedAt.ShouldNotBeNull().ShouldBeGreaterThan(earlierClose);
        (rows[otherTurn].GroupClosedAt, rows[single].GroupClosedAt).ShouldBe(((DateTimeOffset?)null, (DateTimeOffset?)null));
        rows.Values.Select(a => a.Status).ShouldAllBe(status => status == PendingActionStatus.Pending);
        (await _writes.ListAsync(10, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_Owner_s_Approval_And_The_Worker_s_Record_Of_An_Action_Queued_In_A_Request_Carry_That_Request_s_Group()
    {
        await _features.SetWriteGrantAsync(FeatureCatalog.GitHubId, granted: true, confirmWrites: true, CancellationToken.None);
        var queued = await Gate().RequestAsync(
            "github", "New issues", "GitHubActions.create_issue", "s", """{"title":"t","body":"b"}""", "turn-7", "acme/shop", CancellationToken.None);
        await ApproveAsync(queued.ActionId!);
        var claimed = (await Gate().ClaimNextApprovedAsync("github", CancellationToken.None)).ShouldNotBeNull();

        await Gate().RecordAsync("github", "New issues", claimed.Target, "succeeded", claimed.Id, "https://github.com/acme/shop/issues/1", "scout[bot]", CancellationToken.None);

        (await _writes.ListAsync(10, CancellationToken.None)).Select(e => (e.Outcome, e.ActionId, e.GroupId))
            .ShouldBe([("succeeded", queued.ActionId, "turn-7"), ("approved", queued.ActionId, "turn-7")]);
    }

    private static readonly DateTimeOffset Started = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    private Task<WriteDecision> StartAsync(DateTimeOffset at, string target = "scripts/intro.md") =>
        GateAt(at).RequestToRunAsync("youtube", DataFolder, target, "Save the script", null, null, null, CancellationToken.None);

    private Task<IReadOnlyList<WriteLogEntry>> LoggedAsync() => _writes.ListAsync(10, CancellationToken.None);

    [Fact]
    public async Task A_Reversible_Write_Is_In_The_Log_As_Started_Before_It_Is_Allowed_And_Its_Answer_Carries_That_Entry_s_Id()
    {
        await GrantYouTubeAsync();

        var decision = await StartAsync(Started);

        decision.Kind.ShouldBe(WriteDecisionKind.Allowed);
        var entry = (await LoggedAsync()).ShouldHaveSingleItem();
        entry.ShouldBe(new WriteLogEntry(Started, "youtube", DataFolder, "scripts/intro.md", "started", null, decision.ActionId.ShouldNotBeNull()));
        (await _actions.ListPendingAsync(CancellationToken.None)).ShouldBeEmpty("a reversible write never enters the approval queue");
    }

    [Fact]
    public async Task A_Grant_Check_That_Runs_No_Write_Appends_No_Started_Entry()
    {
        await GrantYouTubeAsync();

        var decision = await Gate().RequestAsync("youtube", DataFolder, "scripts/intro.md", "Save the script", CancellationToken.None);

        decision.ShouldBe(new WriteDecision(WriteDecisionKind.Allowed, string.Empty));
        (await LoggedAsync()).ShouldBeEmpty("a grant check is a decision only; nothing runs behind it");
    }

    [Theory]
    [InlineData(Upload, true, WriteDecisionKind.Queued)]
    [InlineData(DataFolder, false, WriteDecisionKind.Refused)]
    [InlineData("videos.list", true, WriteDecisionKind.Refused)]
    [InlineData("videos.delete", true, WriteDecisionKind.Refused)]
    public async Task A_Queued_Or_Refused_Write_Appends_No_Started_Entry(string scope, bool granted, WriteDecisionKind kind)
    {
        if (granted)
        {
            await GrantYouTubeAsync();
        }

        var decision = await GateAt(Started).RequestToRunAsync("youtube", scope, "intro.mp4", "Upload", null, null, null, CancellationToken.None);

        decision.Kind.ShouldBe(kind);
        (await LoggedAsync()).ShouldBeEmpty("only a write that may run now is started");
    }

    [Fact]
    public async Task A_Reversible_Write_Whose_Started_Entry_Cannot_Be_Written_Is_Not_Allowed()
    {
        await GrantYouTubeAsync();
        Directory.CreateDirectory(_writes.FilePath);

        await Should.ThrowAsync<UnauthorizedAccessException>(() => StartAsync(Started));

        Directory.Delete(_writes.FilePath);
        (await LoggedAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Record_With_A_Started_Id_Appends_How_The_Write_Ended_With_That_Id_And_No_Approver()
    {
        await GrantYouTubeAsync();
        var started = await StartAsync(Started);

        await Gate().RecordAsync("youtube", DataFolder, "scripts/intro.md", "succeeded", started.ActionId, null, "scout[bot]", CancellationToken.None);

        (await LoggedAsync()).Select(e => (e.Outcome, e.ApprovedBy, e.ActionId, e.SignedAs)).ShouldBe([
            ("succeeded", (string?)null, started.ActionId, (string?)"scout[bot]"),
            ("started", null, started.ActionId, null),
        ]);
    }

    [Theory]
    [InlineData("started")]
    [InlineData("approved")]
    [InlineData("approved: looks fine")]
    public async Task A_Record_On_A_Write_Row_Cannot_Say_Started_Or_Approved(string outcome)
    {
        await GrantYouTubeAsync();
        var started = await StartAsync(Started);

        var refused = await Should.ThrowAsync<WriteNotRecordableException>(() =>
            Gate().RecordAsync("youtube", DataFolder, "scripts/intro.md", outcome, started.ActionId, CancellationToken.None));

        refused.Message.ShouldBe($"A write's record cannot read '{outcome}'; it says how the write ended.");
        (await LoggedAsync()).ShouldHaveSingleItem().Outcome.ShouldBe("started");
    }

    [Fact]
    public async Task A_Record_Naming_A_Queued_Action_On_A_Write_Row_Is_Still_Refused_And_Leaves_That_Action_As_It_Was()
    {
        await GrantYouTubeAsync();
        var gate = Gate();
        var waiting = await gate.RequestAsync("youtube", Upload, "intro.mp4", "Upload", CancellationToken.None);
        var approved = await gate.RequestAsync("youtube", Upload, "outro.mp4", "Upload", CancellationToken.None);
        await _actions.ApproveAsync(approved.ActionId!, reason: null, CancellationToken.None);

        var notApproved = await Should.ThrowAsync<WriteNotRecordableException>(() =>
            gate.RecordAsync("youtube", DataFolder, "intro.mp4", "succeeded", waiting.ActionId, CancellationToken.None));
        var otherRow = await Should.ThrowAsync<WriteNotRecordableException>(() =>
            gate.RecordAsync("youtube", DataFolder, "outro.mp4", "succeeded", approved.ActionId, CancellationToken.None));

        notApproved.Message.ShouldBe($"Action '{waiting.ActionId}' is not approved by the owner.");
        otherRow.Message.ShouldBe($"Action '{approved.ActionId}' was approved for youtube {Upload}, not youtube {DataFolder}.");
        (await _actions.FindAsync(waiting.ActionId!, CancellationToken.None)).ShouldNotBeNull().Status.ShouldBe(PendingActionStatus.Pending);
        (await _actions.FindAsync(approved.ActionId!, CancellationToken.None)).ShouldNotBeNull().Status.ShouldBe(PendingActionStatus.Approved);
        (await LoggedAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Started_Write_With_Its_Record_Lists_Once_With_How_It_Ended()
    {
        await GrantYouTubeAsync();
        var started = await StartAsync(Started);
        await Gate().RecordAsync("youtube", DataFolder, "scripts/intro.md", "succeeded", started.ActionId, CancellationToken.None);

        var listed = WriteGateService.AsListed(await LoggedAsync(), Started + TimeSpan.FromHours(2));

        listed.Select(e => (e.Target, e.Outcome, e.ActionId)).ShouldBe([("scripts/intro.md", "succeeded", started.ActionId)]);
        (await LoggedAsync()).Count.ShouldBe(2, "the log keeps both lines; only the listing pairs them");
    }

    [Theory]
    [InlineData(29 * 60 + 59, "started")]
    [InlineData(30 * 60, "not confirmed: Scout stopped before it recorded how this ended, so it may have run")]
    [InlineData(48 * 60 * 60, "not confirmed: Scout stopped before it recorded how this ended, so it may have run")]
    public async Task A_Started_Write_With_No_Record_Lists_As_Started_Before_30_Minutes_And_As_Not_Confirmed_From_Then_On(int secondsLater, string shown)
    {
        await GrantYouTubeAsync();
        var started = await StartAsync(Started);

        var listed = WriteGateService.AsListed(await LoggedAsync(), Started + TimeSpan.FromSeconds(secondsLater));

        listed.Select(e => (e.At, e.Target, e.Outcome, e.ApprovedBy, e.ActionId))
            .ShouldBe([(Started, "scripts/intro.md", shown, (string?)null, started.ActionId)]);
        (await LoggedAsync()).ShouldHaveSingleItem().Outcome.ShouldBe("started", "the log itself is never rewritten");
    }

    [Fact]
    public async Task A_Record_That_Cannot_Be_Written_Leaves_The_Started_Entry_Which_Lists_As_Not_Confirmed()
    {
        await GrantYouTubeAsync();
        var started = await StartAsync(Started);

        await using (new FileStream(_writes.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Should.ThrowAsync<IOException>(() =>
                Gate().RecordAsync("youtube", DataFolder, "scripts/intro.md", "succeeded", started.ActionId, CancellationToken.None));
        }

        WriteGateService.AsListed(await LoggedAsync(), Started + WriteGateService.ClaimLease)
            .Select(e => (e.Outcome, e.ActionId))
            .ShouldBe([(WriteGateService.StartedNotRecordedOutcome, started.ActionId)]);
    }

    [Fact]
    public async Task A_Started_Write_Is_Never_Handed_Out_As_An_Action_After_Its_Lease()
    {
        await GrantYouTubeAsync();
        var started = await StartAsync(Started);

        var claimed = await GateAt(Started + WriteGateService.ClaimLease + TimeSpan.FromMinutes(1)).ClaimNextApprovedAsync("youtube", CancellationToken.None);

        claimed.ShouldBeNull();
        (await _actions.FindAsync(started.ActionId!, CancellationToken.None)).ShouldBeNull("a started write is never an action in the queue");
        (await _actions.ListAllAsync("youtube", CancellationToken.None)).ShouldBeEmpty();
        (await LoggedAsync()).Select(e => (e.Outcome, e.ActionId)).ShouldBe([("started", started.ActionId)], "nothing sends it again or records it for the worker");
    }

    [Fact]
    public async Task A_Record_Carrying_Another_Feature_s_Started_Id_Does_Not_Hide_That_Started_Entry()
    {
        await GrantYouTubeAsync();
        await _features.SetWriteGrantAsync(FeatureCatalog.GitHubId, granted: true, confirmWrites: true, CancellationToken.None);
        var started = await StartAsync(Started);

        await Gate().RecordAsync("github", DataFolder, "acme/shop", "succeeded", started.ActionId, CancellationToken.None);

        WriteGateService.AsListed(await LoggedAsync(), Started + WriteGateService.ClaimLease)
            .Select(e => (e.FeatureId, e.Target, e.Outcome, e.ActionId))
            .ShouldBe([
                ("github", "acme/shop", "succeeded", started.ActionId),
                ("youtube", "scripts/intro.md", WriteGateService.StartedNotRecordedOutcome, started.ActionId),
            ], "only a record of the same feature, row and target ends a started write");
    }

    [Fact]
    public async Task A_Claim_Expires_By_The_Injected_Clock()
    {
        await GrantYouTubeAsync();
        var queued = await Gate().RequestAsync("youtube", Upload, "intro.mp4", "Upload", CancellationToken.None);
        await _actions.ApproveAsync(queued.ActionId!, reason: null, CancellationToken.None);
        var claimed = (await _actions.ClaimNextAsync("youtube", WriteGateService.ClaimLease, (_, _) => true, CancellationToken.None)).ShouldNotBeNull();
        await _features.SetWriteGrantAsync(FeatureCatalog.YouTubeId, granted: false, confirmWrites: false, CancellationToken.None);
        var pastTheLease = claimed.ClaimedAt!.Value + WriteGateService.ClaimLease + TimeSpan.FromMinutes(1);

        await GateAt(pastTheLease).ClaimNextApprovedAsync("youtube", CancellationToken.None);

        var ended = (await _actions.FindAsync(queued.ActionId!, CancellationToken.None)).ShouldNotBeNull();
        (ended.Status, ended.Reason).ShouldBe((PendingActionStatus.Rejected, (string?)WriteGateService.NeverFinished), "the gate's clock says the claim lapsed");
        (await LoggedAsync()).Select(e => (e.Outcome, e.ActionId))
            .ShouldBe([($"rejected: {WriteGateService.NeverFinished}", queued.ActionId)]);
    }
}
