using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Scout.Host.Console.Tests;

/// <summary>
/// Hermetic tests for <see cref="ParentWatchdogService"/>: no real stdin and no
/// real process. The parent-liveness source is the injected probe.
/// </summary>
public sealed class ParentWatchdogServiceTests
{
    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();
        public int StopApplicationCalls { get; private set; }
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication()
        {
            StopApplicationCalls++;
            _stopping.Cancel();
        }
    }

    private static ParentWatchdogService Build(IParentLivenessProbe probe, IHostApplicationLifetime lifetime)
        => new(probe, lifetime, NullLogger<ParentWatchdogService>.Instance);

    [Fact]
    public async Task ParentExits_Calls_StopApplication_Once()
    {
        var probe = Substitute.For<IParentLivenessProbe>();
        probe.WaitForParentExitAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var lifetime = new FakeLifetime();
        var service = Build(probe, lifetime);

        await service.StartAsync(CancellationToken.None);
        if (service.ExecuteTask is { } t)
        {
            await t;
        }

        lifetime.StopApplicationCalls.ShouldBe(1);
    }

    [Fact]
    public async Task StoppingToken_FiresFirst_StopsCleanly_Without_StopApplication()
    {
        // Probe blocks until its own token is cancelled (mirrors a live parent).
        var probe = Substitute.For<IParentLivenessProbe>();
        probe.WaitForParentExitAsync(Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ct = ci.Arg<CancellationToken>();
                var tcs = new TaskCompletionSource();
                ct.Register(() => tcs.TrySetCanceled(ct));
                return tcs.Task;
            });
        var lifetime = new FakeLifetime();
        var service = Build(probe, lifetime);

        await service.StartAsync(CancellationToken.None);
        // Normal shutdown: cancels ExecuteAsync's stopping token.
        await service.StopAsync(CancellationToken.None);

        if (service.ExecuteTask is { } t)
        {
            await t;
        }

        lifetime.StopApplicationCalls.ShouldBe(0);
    }
}
