namespace Scout.Adapter.Discord.Tests;

public sealed class DiscordAdapterLifecycleTests
{
    [Fact]
    public void LifetimeToken_Throws_Before_Start()
    {
        using var lifecycle = new DiscordAdapterLifecycle();

        Should.Throw<InvalidOperationException>(() => _ = lifecycle.LifetimeToken);
    }

    [Fact]
    public void Start_Arms_A_Live_LifetimeToken()
    {
        using var lifecycle = new DiscordAdapterLifecycle();

        lifecycle.Start(CancellationToken.None);

        lifecycle.IsRunning.ShouldBeTrue();
        lifecycle.LifetimeToken.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact]
    public async Task StopAsync_Cancels_The_LifetimeToken()
    {
        using var lifecycle = new DiscordAdapterLifecycle();
        lifecycle.Start(CancellationToken.None);
        var token = lifecycle.LifetimeToken;

        var stopped = await lifecycle.StopAsync();

        stopped.ShouldBeTrue();
        token.IsCancellationRequested.ShouldBeTrue();
        lifecycle.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public void Start_Twice_Throws()
    {
        using var lifecycle = new DiscordAdapterLifecycle();
        lifecycle.Start(CancellationToken.None);

        Should.Throw<InvalidOperationException>(() => lifecycle.Start(CancellationToken.None))
            .Message.ShouldContain("already been started");
    }

    [Fact]
    public async Task Start_After_Stop_Is_Terminal_And_Throws()
    {
        using var lifecycle = new DiscordAdapterLifecycle();
        lifecycle.Start(CancellationToken.None);
        await lifecycle.StopAsync();

        Should.Throw<InvalidOperationException>(() => lifecycle.Start(CancellationToken.None))
            .Message.ShouldContain("cannot be restarted");
    }

    [Fact]
    public async Task StopAsync_When_Not_Running_Is_NoOp()
    {
        using var lifecycle = new DiscordAdapterLifecycle();

        (await lifecycle.StopAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task The_Lifetime_Token_Is_Handed_Out_Only_While_Running_And_A_Stopped_Lifecycle_Answers_False_Without_Throwing()
    {
        using var lifecycle = new DiscordAdapterLifecycle();
        lifecycle.TryGetLifetimeToken(out var beforeStart).ShouldBeFalse();
        beforeStart.ShouldBe(CancellationToken.None);

        lifecycle.Start(CancellationToken.None);
        lifecycle.TryGetLifetimeToken(out var running).ShouldBeTrue();
        running.ShouldBe(lifecycle.LifetimeToken);

        await lifecycle.StopAsync();
        lifecycle.TryGetLifetimeToken(out var afterStop).ShouldBeFalse();
        afterStop.ShouldBe(CancellationToken.None);
        running.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public void Host_Token_Cancellation_Trips_The_LifetimeToken()
    {
        using var lifecycle = new DiscordAdapterLifecycle();
        using var hostCts = new CancellationTokenSource();
        lifecycle.Start(hostCts.Token);
        var token = lifecycle.LifetimeToken;

        hostCts.Cancel();

        token.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task The_Started_State_Is_Never_Visible_Before_Its_Lifetime_Token_Exists()
    {
        const int Rounds = 10_000;
        var startedWithoutToken = 0;
        for (var round = 0; round < Rounds; round++)
        {
            using var lifecycle = new DiscordAdapterLifecycle();
            var watching = 0;
            var reader = Task.Run(() =>
            {
                Volatile.Write(ref watching, 1);
                while (!lifecycle.IsRunning)
                {
                    Thread.Yield();
                }

                try
                {
                    _ = lifecycle.LifetimeToken;
                }
                catch (InvalidOperationException)
                {
                    startedWithoutToken++;
                }
            });

            SpinWait.SpinUntil(() => Volatile.Read(ref watching) == 1);
            lifecycle.Start(CancellationToken.None);
            await reader;
        }

        startedWithoutToken.ShouldBe(0);
    }
}
