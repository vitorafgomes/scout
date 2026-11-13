using NSubstitute;
using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Features;
using Scout.ControlPlane.Operator;
using Scout.ControlPlane.WriteGate;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Operator;

/// <summary>A project deletion run together with an owner approval that queues the project's YouTube actions.</summary>
public sealed partial class OperatorServiceTests
{
    private const string Done = "done";
    private const string ProjectGone = "refused: the project is gone";
    private const string ActionsOpen = "refused: The project has pending YouTube actions; finish or cancel them first.";

    private static readonly TimeSpan LongerThanAnUnblockedCall = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan NeverReachedByAPassingRun = TimeSpan.FromSeconds(30);

    public static TheoryData<string> ProjectApprovals => new() { "publish", "experiment start", "adopted winner" };

    private async Task<(string Id, string Media, Func<OperatorService, CancellationToken, Task> Approve)> ReadyForAsync(string approval)
    {
        OperatorProject project;
        Func<OperatorService, CancellationToken, Task> approve;
        if (approval == "publish")
        {
            project = await ApprovedAsync();
            var id = project.Id;
            approve = (service, ct) => service.PublishPackageAsync(id, "public", null, madeForKids: false, syntheticMedia: true, ct);
        }
        else
        {
            project = await LiveAsync(TitleProposal("t1", "Rivers bend"));
            var id = project.Id;
            if (approval == "experiment start")
            {
                approve = (service, ct) => service.StartExperimentAsync(id, 0, ct);
            }
            else
            {
                await _operator.StartExperimentAsync(id, 0, None);
                await WorkerSavesAsync(id, p => p with { Experiment = p.Experiment! with { Status = "concluded", WinnerArmId = "variant-a" } });
                approve = (service, ct) => service.AdoptExperimentWinnerAsync(id, ct);
            }
        }

        await _actions.RejectApprovedAsync(FeatureCatalog.YouTubeId, (_, _) => true, "ran before the race", None);
        var media = await _operator.GetMediaDirectoryAsync(project.Id, None);
        await File.WriteAllTextAsync(Path.Combine(media, "a.mp3"), "audio");
        return (project.Id, media, approve);
    }

    private OperatorService Hooked(Parking? afterOpenActionsRead, Parking? beforeQueueing)
    {
        var queue = Substitute.For<IActionQueue>();
        queue.ListOpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var open = await _actions.ListOpenAsync(call.ArgAt<string>(0), call.ArgAt<CancellationToken>(1));
            await (afterOpenActionsRead?.HereAsync() ?? Task.CompletedTask);
            return open;
        });
        queue.ListPendingAsync(Arg.Any<CancellationToken>()).Returns(call => _actions.ListPendingAsync(call.ArgAt<CancellationToken>(0)));
        queue.RejectApprovedAsync(
                Arg.Any<string>(), Arg.Any<Func<PendingAction, IReadOnlyList<PendingAction>, bool>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await (beforeQueueing?.HereAsync() ?? Task.CompletedTask);
                return await _actions.RejectApprovedAsync(
                    call.ArgAt<string>(0),
                    call.ArgAt<Func<PendingAction, IReadOnlyList<PendingAction>, bool>>(1),
                    call.ArgAt<string>(2),
                    call.ArgAt<CancellationToken>(3));
            });
        queue.EnqueueApprovedAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<(string, string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await (beforeQueueing?.HereAsync() ?? Task.CompletedTask);
                return await _actions.EnqueueApprovedAsync(
                    call.ArgAt<string>(0), call.ArgAt<IReadOnlyList<(string, string, string)>>(1), call.ArgAt<CancellationToken>(2));
            });
        return new OperatorService(_store, new WriteGateService(new FeatureCatalog(), _states, queue, _writes), _writes);
    }

    private static async Task<string> OutcomeOf(Task call)
    {
        try
        {
            await call;
            return Done;
        }
        catch (OperatorNotFoundException)
        {
            return ProjectGone;
        }
        catch (OperatorValidationException ex)
        {
            return $"refused: {ex.Message}";
        }
    }

    private async Task<(bool Stored, bool MediaKept, bool AnApprovedActionNamesIt)> StateOfAsync(string id, string media) =>
        ((await _store.ReadProjectsAsync(None)).Any(p => p.Id == id),
            File.Exists(Path.Combine(media, "a.mp3")),
            (await _actions.ListOpenAsync(FeatureCatalog.YouTubeId, None)).Any(a => a.Target.Split(':')[0] == id));

    [Theory]
    [MemberData(nameof(ProjectApprovals))]
    public async Task An_Approval_Made_While_A_Delete_Is_Between_Its_Open_Actions_Read_And_Its_Removal_Is_Refused_Because_The_Project_Is_Gone(
        string approval)
    {
        var (id, media, approve) = await ReadyForAsync(approval);
        var deleteParked = new Parking();
        using var service = Hooked(afterOpenActionsRead: deleteParked, beforeQueueing: null);

        var deleting = Task.Run(() => service.DeleteProjectAsync(id, None));
        await deleteParked.Reached;
        var approving = Task.Run(() => approve(service, None));
        await Task.WhenAny(approving, Task.Delay(LongerThanAnUnblockedCall));
        deleteParked.Release();
        var deleted = await OutcomeOf(deleting);
        var approved = await OutcomeOf(approving);

        (deleted, approved, await StateOfAsync(id, media)).ShouldBe(
            (Done, ProjectGone, (false, false, false)),
            $"the {approval} waits for the delete and finds no project; no approved action names the deleted project");
    }

    [Theory]
    [MemberData(nameof(ProjectApprovals))]
    public async Task A_Delete_Made_While_An_Approval_Is_Between_Its_Project_Save_And_Its_Queueing_Is_Refused_Because_An_Action_Is_Open(
        string approval)
    {
        var (id, media, approve) = await ReadyForAsync(approval);
        var approvalParked = new Parking();
        using var service = Hooked(afterOpenActionsRead: null, beforeQueueing: approvalParked);

        var approving = Task.Run(() => approve(service, None));
        await approvalParked.Reached;
        var deleting = Task.Run(() => service.DeleteProjectAsync(id, None));
        await Task.WhenAny(deleting, Task.Delay(LongerThanAnUnblockedCall));
        approvalParked.Release();
        var deleted = await OutcomeOf(deleting);
        var approved = await OutcomeOf(approving);

        (deleted, approved, await StateOfAsync(id, media)).ShouldBe(
            (ActionsOpen, Done, (true, true, true)),
            $"the delete waits for the {approval} to queue and finds its action open; the project and its media stay");
    }

    [Fact]
    public async Task A_Delete_Cancelled_While_It_Waits_For_A_Publish_Ends_Without_Waiting_And_Leaves_The_Project_Its_Media_And_No_Deletion_Record()
    {
        var (id, media, approve) = await ReadyForAsync("publish");
        var approvalParked = new Parking();
        using var service = Hooked(afterOpenActionsRead: null, beforeQueueing: approvalParked);
        using var cancel = new CancellationTokenSource();

        var approving = Task.Run(() => approve(service, None));
        await approvalParked.Reached;
        var deleting = Task.Run(() => service.DeleteProjectAsync(id, cancel.Token));
        await cancel.CancelAsync();
        var endedWhileThePublishWasParked = await Task.WhenAny(deleting, Task.Delay(NeverReachedByAPassingRun)) == deleting;
        approvalParked.Release();

        endedWhileThePublishWasParked.ShouldBeTrue("a cancelled wait ends without waiting for the publish to finish");
        await Should.ThrowAsync<OperationCanceledException>(deleting);
        (await OutcomeOf(approving)).ShouldBe(Done);
        (await StateOfAsync(id, media)).ShouldBe((true, true, true));
        (await _writes.ListAsync(100, None)).ShouldNotContain(e => e.Target == id && e.Outcome == "deleted project");
    }

    private sealed class Parking
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _taken;

        public Task Reached => _reached.Task.WaitAsync(NeverReachedByAPassingRun);

        public void Release() => _released.TrySetResult();

        public Task HereAsync()
        {
            if (Interlocked.Exchange(ref _taken, 1) != 0)
            {
                return Task.CompletedTask;
            }

            _reached.SetResult();
            return _released.Task;
        }
    }
}
