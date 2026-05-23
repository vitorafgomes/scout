using NSubstitute;
using Scout.Host.Worker.Providers;

namespace Scout.Host.Worker.Tests.Helpers;

internal static class FakeReadinessProbes
{
    public static IOllamaReadinessProbe AlwaysReady()
    {
        var probe = Substitute.For<IOllamaReadinessProbe>();
        probe.IsReady.Returns(true);
        probe.Current.Returns(OllamaReadinessState.Ready);
        probe.WaitReadyAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        probe.WaitReadyAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        return probe;
    }

    public static IOllamaReadinessProbe NeverReady()
    {
        var probe = Substitute.For<IOllamaReadinessProbe>();
        probe.IsReady.Returns(false);
        probe.Current.Returns(OllamaReadinessState.Failed);
        probe.WaitReadyAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        probe.WaitReadyAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));
        return probe;
    }

    public static IOllamaReadinessProbe Pending(TaskCompletionSource<bool> gate)
    {
        var probe = Substitute.For<IOllamaReadinessProbe>();
        probe.IsReady.Returns(_ => gate.Task.IsCompletedSuccessfully && gate.Task.Result);
        probe.Current.Returns(_ =>
            gate.Task.IsCompleted
                ? (gate.Task.Result ? OllamaReadinessState.Ready : OllamaReadinessState.Failed)
                : OllamaReadinessState.Starting);
        probe.WaitReadyAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var ct = call.Arg<CancellationToken>();
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
        });
        probe.WaitReadyAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var timeout = call.Arg<TimeSpan>();
            var ct = call.Arg<CancellationToken>();
            try
            {
                return await gate.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return false;
            }
        });
        return probe;
    }
}
