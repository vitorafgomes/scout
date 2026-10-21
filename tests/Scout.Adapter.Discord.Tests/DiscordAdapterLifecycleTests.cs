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
    public void Host_Token_Cancellation_Trips_The_LifetimeToken()
    {
        using var lifecycle = new DiscordAdapterLifecycle();
        using var hostCts = new CancellationTokenSource();
        lifecycle.Start(hostCts.Token);
        var token = lifecycle.LifetimeToken;

        hostCts.Cancel();

        token.IsCancellationRequested.ShouldBeTrue();
    }
}
