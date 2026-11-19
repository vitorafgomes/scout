using Grpc.Core;
using Scout.ControlPlane.Contracts;
using Scout.ControlPlane.Controls;
using Shouldly;
using Xunit;
using GrpcFeatures = Scout.ControlPlane.Contracts.Features;

namespace Scout.ControlPlane.Tests.Integration;

/// <summary>
/// The owner's controls on a running job over the real UDS host: only the desktop token can ask for one, only the worker
/// token can take and settle one, the worker gets each once, and the write log keeps it as the owner's; none shows as an
/// action waiting for approval.
/// </summary>
[Collection("uds")]
public sealed class JobControlGrpcTests
{
    [Fact]
    public async Task Only_the_desktop_asks_for_a_control_only_the_worker_takes_and_settles_it_once_and_the_write_log_keeps_it_as_the_owners()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var desktopChannel = host.CreateChannel();
        using var workerChannel = host.CreateWorkerChannel();
        var desktop = new GrpcFeatures.FeaturesClient(desktopChannel);
        var worker = new WorkerGate.WorkerGateClient(workerChannel);
        var pause = new ControlJobRequest { Kind = "audit", Owner = "acme", Repo = "shop", Action = "pause" };

        var asked = await desktop.ControlJobAsync(pause);
        var workerAsks = await Should.ThrowAsync<RpcException>(() => new GrpcFeatures.FeaturesClient(workerChannel).ControlJobAsync(pause).ResponseAsync);
        var desktopTakes = await Should.ThrowAsync<RpcException>(() => new WorkerGate.WorkerGateClient(desktopChannel).NextJobControlAsync(new NextJobControlRequest()).ResponseAsync);
        var pendingApprovals = await desktop.ListPendingActionsAsync(new ListPendingActionsRequest());
        var taken = await worker.NextJobControlAsync(new NextJobControlRequest());
        var takenAgain = await worker.NextJobControlAsync(new NextJobControlRequest());
        var settled = await worker.SettleJobControlAsync(new SettleJobControlRequest { Id = asked.ControlId, Status = "applied", Outcome = "Paused the audit of acme/shop; an area in progress finishes first." });
        var settledAgain = await worker.SettleJobControlAsync(new SettleJobControlRequest { Id = asked.ControlId, Status = "applied", Outcome = "Again." });
        var writes = await desktop.ListWritesAsync(new ListWritesRequest { Limit = 10 });

        (asked.Accepted, asked.Reason, asked.HasControlId).ShouldBe((true, OwnerControlService.Queued, true));
        (workerAsks.StatusCode, desktopTakes.StatusCode).ShouldBe((StatusCode.PermissionDenied, StatusCode.PermissionDenied));
        pendingApprovals.Actions.ShouldBeEmpty();
        (taken.Control.Id, taken.Control.Kind, taken.Control.Owner, taken.Control.Repo, taken.Control.HasNumber, taken.Control.Action)
            .ShouldBe((asked.ControlId, "audit", "acme", "shop", false, "pause"));
        takenAgain.Control.ShouldBeNull();
        (settled.Recorded, settledAgain.Recorded).ShouldBe((true, false));
        var entry = writes.Entries.ShouldHaveSingleItem();
        (entry.FeatureId, entry.Action, entry.Target, entry.Outcome, entry.ApprovedBy, entry.ActionId, entry.ControlStatus)
            .ShouldBe(("github", "Pause audit", "acme/shop", "Paused the audit of acme/shop; an area in progress finishes first.", "owner", asked.ControlId, "applied"));
    }

    [Fact]
    public async Task A_control_that_does_not_apply_is_answered_with_its_reason_and_the_worker_gets_nothing()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var desktopChannel = host.CreateChannel();
        using var workerChannel = host.CreateWorkerChannel();
        var desktop = new GrpcFeatures.FeaturesClient(desktopChannel);

        var answer = await desktop.ControlJobAsync(new ControlJobRequest { Kind = "tests", Owner = "acme", Repo = "shop", Number = 42, Action = "pause" });
        var next = await new WorkerGate.WorkerGateClient(workerChannel).NextJobControlAsync(new NextJobControlRequest());

        (answer.Accepted, answer.Reason, answer.HasControlId).ShouldBe((false, OwnerControlService.TestsStopOnly, false));
        next.Control.ShouldBeNull();
    }

    [Fact]
    public async Task A_review_control_with_its_number_reaches_the_worker_once_and_is_settled_on_its_pull_request()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var desktopChannel = host.CreateChannel();
        using var workerChannel = host.CreateWorkerChannel();
        var desktop = new GrpcFeatures.FeaturesClient(desktopChannel);
        var worker = new WorkerGate.WorkerGateClient(workerChannel);

        var asked = await desktop.ControlJobAsync(new ControlJobRequest { Kind = "review", Owner = "acme", Repo = "shop", Number = 12, Action = "stop" });
        var taken = await worker.NextJobControlAsync(new NextJobControlRequest());
        var takenAgain = await worker.NextJobControlAsync(new NextJobControlRequest());
        var settled = await worker.SettleJobControlAsync(new SettleJobControlRequest { Id = asked.ControlId, Status = "applied", Outcome = "Stopped the review of acme/shop#12." });
        var writes = await desktop.ListWritesAsync(new ListWritesRequest { Limit = 10 });

        (asked.Accepted, asked.Reason).ShouldBe((true, OwnerControlService.Queued), "a review control with its number is accepted");
        (taken.Control.Kind, taken.Control.Owner, taken.Control.Repo, taken.Control.HasNumber, taken.Control.Number, taken.Control.Action)
            .ShouldBe(("review", "acme", "shop", true, 12, "stop"), "the worker gets the review control with its pull request number");
        takenAgain.Control.ShouldBeNull("the control is handed out once");
        settled.Recorded.ShouldBeTrue();
        var entry = writes.Entries.ShouldHaveSingleItem();
        (entry.Action, entry.Target, entry.ControlStatus).ShouldBe(("Stop review", "acme/shop#12", "applied"));
    }

    [Theory]
    [InlineData(null, "stop")]
    [InlineData(0, "pause")]
    [InlineData(12, "restart")]
    public async Task A_review_control_that_does_not_apply_is_answered_with_its_reason_and_the_worker_gets_nothing(int? number, string action)
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var desktopChannel = host.CreateChannel();
        using var workerChannel = host.CreateWorkerChannel();
        var request = new ControlJobRequest { Kind = "review", Owner = "acme", Repo = "shop", Action = action };
        if (number is { } n)
        {
            request.Number = n;
        }

        var answer = await new GrpcFeatures.FeaturesClient(desktopChannel).ControlJobAsync(request);
        var next = await new WorkerGate.WorkerGateClient(workerChannel).NextJobControlAsync(new NextJobControlRequest());

        (answer.Accepted, answer.Reason).ShouldBe((false, action == "restart" ? OwnerControlService.UnknownAction : OwnerControlService.ReviewNeedsNumber), $"a review {action} on number {number?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"} is refused");
        next.Control.ShouldBeNull("the worker gets nothing for a refused control");
    }

    [Fact]
    public async Task A_settle_without_an_id_an_outcome_or_a_status_the_worker_gives_is_refused_as_an_invalid_argument()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var workerChannel = host.CreateWorkerChannel();
        var worker = new WorkerGate.WorkerGateClient(workerChannel);

        var noOutcome = await Should.ThrowAsync<RpcException>(() => worker.SettleJobControlAsync(new SettleJobControlRequest { Id = "x", Status = "applied" }).ResponseAsync);
        var noStatus = await Should.ThrowAsync<RpcException>(() => worker.SettleJobControlAsync(new SettleJobControlRequest { Id = "x", Outcome = "Paused." }).ResponseAsync);

        (noOutcome.StatusCode, noOutcome.Status.Detail).ShouldBe((StatusCode.InvalidArgument, "A control id and an outcome are required."));
        (noStatus.StatusCode, noStatus.Status.Detail).ShouldBe((StatusCode.InvalidArgument, "The status must be applied, not applied or not confirmed."));
    }
}
