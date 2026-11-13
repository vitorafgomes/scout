using NSubstitute;
using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Features;
using Scout.ControlPlane.WriteGate;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.WriteGate;

/// <summary>
/// The state the desktop's list gives a group of actions one request queued, from the group's rows and the clock: open,
/// closed by the worker's report or by a quiet spell, changed when an action joins after the close, over the bound; the
/// actions and their groups are read once, so a file that changes between two reads cannot fail the list.
/// </summary>
public sealed class ActionGroupStateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);

    /// <summary>One action of a case: when it was asked and closed, in seconds after the start, and its status and group.</summary>
    public sealed record Row(
        int AskedAt,
        PendingActionStatus Status = PendingActionStatus.Pending,
        int? ClosedAt = null,
        string? Group = "turn-1",
        string? Repository = "acme/shop");

    private static Row[] Waiting(int count, int? closedAt = null) =>
        Enumerable.Range(0, count).Select(i => new Row(i, ClosedAt: closedAt)).ToArray();

    private static string?[] Each(int count, string state) => Enumerable.Repeat<string?>(state, count).ToArray();

    public static TheoryData<string, Row[], int, string?[]> Cases() => new()
    {
        { "open while the request still queues", [new(0), new(30)], 60, ["open", "open"] },
        { "open at exactly ten minutes after the newest action", [new(0)], 600, ["open"] },
        { "closed by the worker's report", [new(0, ClosedAt: 40), new(30, ClosedAt: 40)], 60, ["closed", "closed"] },
        { "closed by timeout, the newest action over ten minutes old", [new(0), new(30)], 631, ["closed", "closed"] },
        { "open: a slow model pausing nine minutes between two calls", [new(0), new(540)], 600, ["open", "open"] },
        { "changed: an action joined after the report", [new(0, ClosedAt: 40), new(30, ClosedAt: 40), new(50)], 60, ["changed", "changed", "changed"] },
        { "changed: a second report after the join keeps the first", [new(0, ClosedAt: 40), new(50, ClosedAt: 60)], 70, ["changed", "changed"] },
        { "changed: the stamp holds on an action already decided", [new(0, PendingActionStatus.Rejected, ClosedAt: 40), new(50)], 60, ["changed"] },
        { "changed: a gap, no report: closed by the spell, then a row joined", [new(0), new(601)], 610, ["changed", "changed"] },
        { "closed: a gap, no report, then the report: closed whatever the gap", [new(0, ClosedAt: 700), new(660, ClosedAt: 700)], 710, ["closed", "closed"] },
        { "closed: a write, an eleven-minute step, a second write, then the report, read long after", [new(0, ClosedAt: 670), new(660, ClosedAt: 670)], 5000, ["closed", "closed"] },
        { "changed: a gap, the report, then a late row", [new(0, ClosedAt: 700), new(660, ClosedAt: 700), new(710)], 720, ["changed", "changed", "changed"] },        { "open: a gap of exactly ten minutes", [new(0), new(600)], 610, ["open", "open"] },
        { "open with 50 waiting", Waiting(50), 60, Each(50, "open") },
        { "over the bound with 51 waiting", Waiting(51), 60, Each(51, "over_bound") },
        { "50 waiting and one approved is within the bound", [.. Waiting(50), new(55, PendingActionStatus.Approved)], 60, [.. Each(50, "open"), null] },
        { "over the bound wins over changed", [.. Waiting(51, closedAt: 60), new(70)], 80, Each(52, "over_bound") },
        { "a single action with no group", [new(0, Group: null, Repository: null)], 60, [null] },
        { "one request on two repositories is two groups", [new(0, ClosedAt: 40, Repository: "acme/site"), new(30), new(50, Repository: "acme/site")], 60, ["changed", "open", "changed"] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_Group_State_Follows_Its_Rows_And_The_Clock(string label, Row[] rows, int nowSeconds, string?[] expected)
    {
        var actions = rows.Select((row, i) => new PendingAction(
                $"a{i}", FeatureCatalog.GitHubId, "New issues", "GitHubActions.create_issue", "s", T0.AddSeconds(row.AskedAt), row.Status,
                row.Status == PendingActionStatus.Pending ? null : T0.AddSeconds(row.AskedAt))
        {
            GroupId = row.Group,
            Repository = row.Repository,
            GroupClosedAt = row.ClosedAt is { } closedAt ? T0.AddSeconds(closedAt) : null,
        })
            .ToList();
        var queue = Holding(actions);
        var decisions = new ActionDecisionService(
            Substitute.For<IFeatureStateStore>(), queue, Substitute.For<IWriteLog>(), new StubTime(T0.AddSeconds(nowSeconds)));

        var listed = await decisions.ListPendingAsync(CancellationToken.None);

        listed.Select(l => l.GroupState).ShouldBe(expected, label);
    }

    [Fact]
    public async Task A_Queue_File_That_Changes_Between_Two_Reads_Still_Lists_The_Waiting_Action_With_Its_Group_State()
    {
        var waiting = new PendingAction("a0", FeatureCatalog.GitHubId, "New issues", "GitHubActions.create_issue", "s", T0, PendingActionStatus.Pending, null)
        {
            GroupId = "turn-1",
            Repository = "acme/shop",
            GroupClosedAt = T0.AddSeconds(10),
        };
        var queue = Holding([waiting]);
        queue.ListPendingAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<PendingAction>>([waiting]));
        queue.ListAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<PendingAction>>([]));
        var decisions = new ActionDecisionService(
            Substitute.For<IFeatureStateStore>(), queue, Substitute.For<IWriteLog>(), new StubTime(T0.AddSeconds(20)));

        var listed = await decisions.ListPendingAsync(CancellationToken.None);

        listed.ShouldHaveSingleItem().ShouldBe((waiting, "closed"));
    }

    private static IActionQueue Holding(IReadOnlyList<PendingAction> actions)
    {
        var queue = Substitute.For<IActionQueue>();
        queue.ListWhereAsync(Arg.Any<Func<PendingAction, bool>>(), Arg.Any<CancellationToken>()).Returns(call =>
            Task.FromResult<IReadOnlyList<PendingAction>>(actions.Where(call.Arg<Func<PendingAction, bool>>()).ToList()));
        return queue;
    }
}
