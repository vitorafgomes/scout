using System.Collections.Concurrent;
using Discord;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scout.Conversation.Abstractions;

namespace Scout.Adapter.Discord.Tests;

public sealed class DiscordChatAdapterDispatchTests
{
    private const ulong BotUserId = 1111;
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task The_Gateway_Handler_Returns_Before_The_Conversation_Finishes()
    {
        var entered = NewSignal();
        var release = NewSignal();
        await using var adapter = NewAdapter(new RecordingLogger());
        adapter.Arm(
            async (_, _, ct) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            },
            CancellationToken.None);

        try
        {
            var dispatch = adapter.OnInboundAsync(DirectMessage(), BotUserId, Substitute.For<IMessageChannel>());

            (await Task.WhenAny(dispatch, Task.Delay(Prompt))).ShouldBe(
                dispatch, "the gateway handler must return while the conversation is still running");
            (await Task.WhenAny(entered.Task, Task.Delay(Generous))).ShouldBe(
                entered.Task, "the conversation handler must still be invoked");
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task A_Failing_Conversation_Is_Logged_Never_Unobserved()
    {
        var failure = new InvalidOperationException("conversation failed");
        var logger = new RecordingLogger();
        await using var adapter = NewAdapter(logger);
        adapter.Arm(
            async (_, _, _) =>
            {
                await Task.Yield();
                throw failure;
            },
            CancellationToken.None);

        await Should.NotThrowAsync(
            adapter.OnInboundAsync(DirectMessage(), BotUserId, Substitute.For<IMessageChannel>()));
        await Should.NotThrowAsync(adapter.StopAsync(CancellationToken.None));

        logger.Entries.ShouldContain(
            e => e.Level == LogLevel.Error && ReferenceEquals(e.Exception, failure),
            "a failing conversation must be logged as an error carrying its exception");
    }

    [Fact]
    public async Task Stop_Waits_For_In_Flight_Conversations()
    {
        var cancellationObserved = NewSignal();
        var unwindGate = NewSignal();
        var unwound = NewSignal();
        await using var adapter = NewAdapter(new RecordingLogger());
        adapter.Arm(
            async (_, _, ct) =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                finally
                {
                    cancellationObserved.TrySetResult();
                    await unwindGate.Task;
                    unwound.TrySetResult();
                }
            },
            CancellationToken.None);

        try
        {
            await adapter.OnInboundAsync(DirectMessage(), BotUserId, Substitute.For<IMessageChannel>());

            var stop = adapter.StopAsync(CancellationToken.None);

            (await Task.WhenAny(cancellationObserved.Task, Task.Delay(Generous))).ShouldBe(
                cancellationObserved.Task, "stop must cancel the in-flight conversation through the lifetime token");
            (await Task.WhenAny(stop, Task.Delay(TimeSpan.FromMilliseconds(500)))).ShouldNotBe(
                stop, "stop must not return while a conversation is still unwinding");

            unwindGate.TrySetResult();
            await stop;

            unwound.Task.IsCompletedSuccessfully.ShouldBeTrue(
                "the in-flight conversation must have finished before stop returned");
        }
        finally
        {
            unwindGate.TrySetResult();
        }
    }

    private static DiscordChatAdapter NewAdapter(ILogger<DiscordChatAdapter> logger)
        => new(
            Options.Create(new DiscordOptions { BotToken = "not-a-real-token" }),
            new DiscordMessageTranslator(),
            logger);

    private static InboundDiscordMessage DirectMessage()
        => new(
            MessageId: 42,
            AuthorId: 2222,
            ChannelId: 3333,
            Content: "how is the build?",
            MentionsSelf: false,
            Timestamp: new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero),
            GuildId: null);

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class RecordingLogger : ILogger<DiscordChatAdapter>
    {
        private readonly ConcurrentQueue<(LogLevel Level, Exception? Exception)> _entries = new();

        public IReadOnlyCollection<(LogLevel Level, Exception? Exception)> Entries => _entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => _entries.Enqueue((logLevel, exception));
    }
}
