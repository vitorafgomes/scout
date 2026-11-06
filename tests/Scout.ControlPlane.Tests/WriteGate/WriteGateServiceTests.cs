using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Features;
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
        await _actions.ApproveAsync(second.ActionId!, null, CancellationToken.None);
        await _actions.ApproveAsync(first.ActionId!, null, CancellationToken.None);

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
        await _actions.ApproveAsync(queued.ActionId!, null, CancellationToken.None);
        var claimed = (await gate.ClaimNextApprovedAsync(FeatureCatalog.YouTubeId, CancellationToken.None)).ShouldNotBeNull();

        await gate.RecordAsync(FeatureCatalog.YouTubeId, "videos.update", "UC1/v1", "succeeded", claimed.Id, CancellationToken.None);

        (await _actions.FindAsync(claimed.Id, CancellationToken.None)).ShouldNotBeNull().Status.ShouldBe(PendingActionStatus.Executed);
        var entry = (await _writes.ListSinceAsync(DateTimeOffset.MinValue, CancellationToken.None)).Single(e => e.ActionId == claimed.Id && e.Outcome == "succeeded");
        entry.Action.ShouldBe("videos.update");
        await Should.ThrowAsync<WriteNotRecordableException>(
            () => gate.RecordAsync(FeatureCatalog.YouTubeId, "videos.update", "UC1/v1", "succeeded", claimed.Id, CancellationToken.None));
    }
}
