using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Scout.Contracts;
using Scout.Conversation;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Streaming;
using Shouldly;
using Xunit;

namespace Scout.Host.Console.Tests;

/// <summary>
/// Hermetic tests for <see cref="ConversationWorker"/>: zero adapters stops the
/// host with guidance; one or many adapters are each started with the conductor
/// handler and stopped on shutdown. No real Discord/Slack/Teams.
/// </summary>
public sealed class ConversationWorkerTests
{
    private static ConversationConductor BuildConductor()
    {
        var router = Substitute.For<IConversationRouter>();
        var pump = new StreamPump(new StreamPumpOptions());
        return new ConversationConductor(router, pump, NullScoutRequestContext.Instance);
    }

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

    private static async Task RunExecuteAsync(ConversationWorker worker, CancellationToken token)
    {
        // ExecuteAsync is protected; StartAsync invokes it. Await ExecuteTask so
        // the background body has completed before assertions run.
        await ((IHostedService)worker).StartAsync(token);
        if (worker.ExecuteTask is { } executeTask)
        {
            await executeTask;
        }
    }

    [Fact]
    public async Task NoAdapters_WritesGuidanceToStderr_And_StopsApplication()
    {
        var lifetime = new FakeLifetime();
        var readiness = Substitute.For<IReadinessSignal>();
        var worker = new ConversationWorker(
            [],
            BuildConductor(),
            lifetime,
            readiness,
            NullLogger<ConversationWorker>.Instance);

        var originalError = System.Console.Error;
        var captured = new StringWriter();
        try
        {
            System.Console.SetError(captured);
            await RunExecuteAsync(worker, CancellationToken.None);
        }
        finally
        {
            System.Console.SetError(originalError);
        }

        captured.ToString().ShouldContain("no chat adapter is configured");
        lifetime.StopApplicationCalls.ShouldBe(1);
        readiness.DidNotReceive().Signal();
    }

    [Fact]
    public async Task OneAdapter_Started_With_NonNullHandler_And_CancellableToken()
    {
        var adapter = Substitute.For<IChatAdapter>();
        adapter.Name.Returns("Fake");

        var worker = new ConversationWorker(
            [adapter],
            BuildConductor(),
            new FakeLifetime(),
            Substitute.For<IReadinessSignal>(),
            NullLogger<ConversationWorker>.Instance);

        await RunExecuteAsync(worker, CancellationToken.None);

        var call = adapter.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IChatAdapter.StartAsync));
        var args = call.GetArguments();
        args[0].ShouldNotBeNull();
        ((CancellationToken)args[1]!).CanBeCanceled.ShouldBeTrue();
    }

    [Fact]
    public async Task MultipleAdapters_Each_Started()
    {
        var first = Substitute.For<IChatAdapter>();
        first.Name.Returns("First");
        var second = Substitute.For<IChatAdapter>();
        second.Name.Returns("Second");

        var worker = new ConversationWorker(
            [first, second],
            BuildConductor(),
            new FakeLifetime(),
            Substitute.For<IReadinessSignal>(),
            NullLogger<ConversationWorker>.Instance);

        await RunExecuteAsync(worker, CancellationToken.None);

        await first.Received(1).StartAsync(
            Arg.Any<Func<ChatMessage, IResponseSink, CancellationToken, Task>>(),
            Arg.Any<CancellationToken>());
        await second.Received(1).StartAsync(
            Arg.Any<Func<ChatMessage, IResponseSink, CancellationToken, Task>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AllAdaptersStarted_Signals_Readiness_Once()
    {
        var first = Substitute.For<IChatAdapter>();
        first.Name.Returns("First");
        var second = Substitute.For<IChatAdapter>();
        second.Name.Returns("Second");
        var readiness = Substitute.For<IReadinessSignal>();

        var worker = new ConversationWorker(
            [first, second],
            BuildConductor(),
            new FakeLifetime(),
            readiness,
            NullLogger<ConversationWorker>.Instance);

        await RunExecuteAsync(worker, CancellationToken.None);

        readiness.Received(1).Signal();
        await first.Received(1).StartAsync(
            Arg.Any<Func<ChatMessage, IResponseSink, CancellationToken, Task>>(),
            Arg.Any<CancellationToken>());
        await second.Received(1).StartAsync(
            Arg.Any<Func<ChatMessage, IResponseSink, CancellationToken, Task>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdapterStartThrows_WritesFailureToStderr_StopsApplication_NoCrash_NoReadiness()
    {
        var adapter = Substitute.For<IChatAdapter>();
        adapter.Name.Returns("Faulty");
        adapter.StartAsync(
                Arg.Any<Func<ChatMessage, IResponseSink, CancellationToken, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("login failure"));
        var lifetime = new FakeLifetime();
        var readiness = Substitute.For<IReadinessSignal>();

        var worker = new ConversationWorker(
            [adapter],
            BuildConductor(),
            lifetime,
            readiness,
            NullLogger<ConversationWorker>.Instance);

        var originalError = System.Console.Error;
        var captured = new StringWriter();
        try
        {
            System.Console.SetError(captured);
            // Must not propagate: a failing adapter stops the host gracefully.
            await RunExecuteAsync(worker, CancellationToken.None);
        }
        finally
        {
            System.Console.SetError(originalError);
        }

        captured.ToString().ShouldContain("Faulty");
        captured.ToString().ShouldContain("login failure");
        lifetime.StopApplicationCalls.ShouldBe(1);
        readiness.DidNotReceive().Signal();
    }

    [Fact]
    public async Task StopAsync_Stops_Each_Adapter()
    {
        var first = Substitute.For<IChatAdapter>();
        first.Name.Returns("First");
        var second = Substitute.For<IChatAdapter>();
        second.Name.Returns("Second");

        var worker = new ConversationWorker(
            [first, second],
            BuildConductor(),
            new FakeLifetime(),
            Substitute.For<IReadinessSignal>(),
            NullLogger<ConversationWorker>.Instance);

        await RunExecuteAsync(worker, CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        await first.Received(1).StopAsync(Arg.Any<CancellationToken>());
        await second.Received(1).StopAsync(Arg.Any<CancellationToken>());
    }
}
