using Scout.Conversation.Abstractions;

namespace Scout.Adapter.Teams.Tests;

public sealed class TeamsAdapterLifecycleTests
{
    private static readonly Func<ChatMessage, IResponseSink, CancellationToken, Task> Handler = (_, _, _) => Task.CompletedTask;

    [Fact]
    public async Task The_Lifecycle_Reads_As_Running_Only_With_The_Handler_Of_That_Start_Before_A_Start_After_A_Stop_And_After_A_Restart()
    {
        using var lifecycle = new TeamsAdapterLifecycle();
        Func<ChatMessage, IResponseSink, CancellationToken, Task> first = (_, _, _) => Task.CompletedTask;
        Func<ChatMessage, IResponseSink, CancellationToken, Task> second = (_, _, _) => Task.CompletedTask;
        lifecycle.TryGetSession(out var beforeStart, out _).ShouldBeFalse();
        beforeStart.ShouldBeNull();

        lifecycle.Start(first, CancellationToken.None);
        lifecycle.TryGetSession(out var started, out var token).ShouldBeTrue();
        started.ShouldBeSameAs(first);
        token.ShouldBe(lifecycle.LifetimeToken);

        await lifecycle.StopAsync();
        lifecycle.TryGetSession(out var stopped, out _).ShouldBeFalse();
        stopped.ShouldBeNull();

        lifecycle.Start(second, CancellationToken.None);
        lifecycle.TryGetSession(out var restarted, out _).ShouldBeTrue();
        restarted.ShouldBeSameAs(second);
    }

    [Fact]
    public void LifetimeToken_Throws_Before_Start()
    {
        using var lifecycle = new TeamsAdapterLifecycle();
        Should.Throw<InvalidOperationException>(() => _ = lifecycle.LifetimeToken);
    }

    [Fact]
    public void Start_Arms_A_Live_LifetimeToken()
    {
        using var lifecycle = new TeamsAdapterLifecycle();
        lifecycle.Start(Handler, CancellationToken.None);

        lifecycle.IsRunning.ShouldBeTrue();
        lifecycle.LifetimeToken.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact]
    public async Task StopAsync_Cancels_The_LifetimeToken()
    {
        using var lifecycle = new TeamsAdapterLifecycle();
        lifecycle.Start(Handler, CancellationToken.None);
        var token = lifecycle.LifetimeToken;

        (await lifecycle.StopAsync()).ShouldBeTrue();

        token.IsCancellationRequested.ShouldBeTrue();
        lifecycle.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public void TryGetSession_False_Before_Start()
    {
        using var lifecycle = new TeamsAdapterLifecycle();
        lifecycle.TryGetSession(out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryGetSession_Returns_Live_Token_While_Running()
    {
        using var lifecycle = new TeamsAdapterLifecycle();
        lifecycle.Start(Handler, CancellationToken.None);

        lifecycle.TryGetSession(out _, out var token).ShouldBeTrue();
        token.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact]
    public async Task TryGetSession_False_After_Stop_No_Throw()
    {
        using var lifecycle = new TeamsAdapterLifecycle();
        lifecycle.Start(Handler, CancellationToken.None);
        await lifecycle.StopAsync();

        lifecycle.TryGetSession(out _, out var token).ShouldBeFalse();
        token.ShouldBe(default(CancellationToken));
    }

    [Fact]
    public void Start_Twice_Throws()
    {
        using var lifecycle = new TeamsAdapterLifecycle();
        lifecycle.Start(Handler, CancellationToken.None);

        Should.Throw<InvalidOperationException>(() => lifecycle.Start(Handler, CancellationToken.None))
            .Message.ShouldContain("already been started");
    }

    [Fact]
    public async Task Start_After_Stop_Is_Allowed_Webhook_Is_ReArmable()
    {
        using var lifecycle = new TeamsAdapterLifecycle();
        lifecycle.Start(Handler, CancellationToken.None);
        await lifecycle.StopAsync();

        Should.NotThrow(() => lifecycle.Start(Handler, CancellationToken.None));
        lifecycle.IsRunning.ShouldBeTrue();
    }

    [Fact]
    public async Task StopAsync_When_Not_Running_Is_NoOp()
    {
        using var lifecycle = new TeamsAdapterLifecycle();
        (await lifecycle.StopAsync()).ShouldBeFalse();
    }

    [Fact]
    public void Host_Token_Cancellation_Trips_The_LifetimeToken()
    {
        using var lifecycle = new TeamsAdapterLifecycle();
        using var hostCts = new CancellationTokenSource();
        lifecycle.Start(Handler, hostCts.Token);
        var token = lifecycle.LifetimeToken;

        hostCts.Cancel();

        token.IsCancellationRequested.ShouldBeTrue();
    }
}
