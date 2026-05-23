using Scout.Host.Worker.Providers;
using Shouldly;

namespace Scout.Host.Worker.Tests;

public sealed class OllamaReadinessProbeTests
{
    [Fact]
    public void Probe_StartsInUnknown_State()
    {
        var probe = new OllamaReadinessProbe();
        probe.Current.ShouldBe(OllamaReadinessState.Unknown);
        probe.IsReady.ShouldBeFalse();
    }

    [Fact]
    public void MarkStarting_TransitionsFromUnknown()
    {
        var probe = new OllamaReadinessProbe();
        probe.MarkStarting();
        probe.Current.ShouldBe(OllamaReadinessState.Starting);
    }

    [Fact]
    public async Task MarkReady_TransitionsToReady_AndCompletesAllWaiters()
    {
        var probe = new OllamaReadinessProbe();
        var w1 = probe.WaitReadyAsync(CancellationToken.None);
        var w2 = probe.WaitReadyAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        probe.MarkReady();

        await w1;
        (await w2).ShouldBeTrue();
        probe.IsReady.ShouldBeTrue();
        probe.Current.ShouldBe(OllamaReadinessState.Ready);
    }

    [Fact]
    public async Task MarkFailed_CompletesWaiters_WithoutThrowing()
    {
        var probe = new OllamaReadinessProbe();
        var waiter = probe.WaitReadyAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        probe.MarkFailed();
        (await waiter).ShouldBeFalse();
        probe.Current.ShouldBe(OllamaReadinessState.Failed);
        probe.IsReady.ShouldBeFalse();
    }

    [Fact]
    public void MarkReady_IsIdempotent()
    {
        var probe = new OllamaReadinessProbe();
        probe.MarkReady();
        Should.NotThrow(() => probe.MarkReady());
        probe.Current.ShouldBe(OllamaReadinessState.Ready);
    }

    [Fact]
    public async Task MarkFailed_AfterReady_IsNoOp()
    {
        var probe = new OllamaReadinessProbe();
        probe.MarkReady();
        probe.MarkFailed();
        probe.Current.ShouldBe(OllamaReadinessState.Ready);
        (await probe.WaitReadyAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task WaitReadyAsync_HonoursCancellation()
    {
        var probe = new OllamaReadinessProbe();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(50);
        await Should.ThrowAsync<OperationCanceledException>(
            async () => await probe.WaitReadyAsync(cts.Token));
    }

    [Fact]
    public async Task WaitReadyAsync_WithTimeout_ReturnsFalseOnTimeout_WithoutThrowing()
    {
        var probe = new OllamaReadinessProbe();
        var result = await probe.WaitReadyAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None);
        result.ShouldBeFalse();
    }
}
