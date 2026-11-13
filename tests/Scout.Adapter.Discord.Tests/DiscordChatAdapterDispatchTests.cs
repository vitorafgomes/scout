using System.Collections.Concurrent;
using System.Net;
using Discord;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scout.Conversation.Access;

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

    [Fact]
    public async Task A_Reply_Cancelled_At_Stop_Stops_The_Typing_Indicator()
    {
        var typing = Substitute.For<IDisposable>();
        var channel = Substitute.For<IDMChannel>();
        channel.EnterTypingState(Arg.Any<RequestOptions>()).Returns(typing);
        var begun = NewSignal();
        await using var adapter = NewAdapter(new RecordingLogger());
        adapter.Arm(
            async (_, sink, ct) =>
            {
                await sink.BeginAsync(ct);
                begun.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            },
            CancellationToken.None);

        await adapter.OnInboundAsync(DirectMessage(), BotUserId, channel);
        await begun.Task.WaitAsync(Generous);
        typing.DidNotReceive().Dispose();

        await adapter.StopAsync(CancellationToken.None);

        typing.Received(1).Dispose();
    }

    [Fact]
    public async Task A_Start_No_Stop_Interrupts_Logs_In_And_Then_Starts_The_Gateway_Once()
    {
        var calls = new List<string>();
        await using var adapter = NewAdapter(new RecordingLogger());
        adapter.Login = () =>
        {
            calls.Add("login");
            return Task.CompletedTask;
        };
        adapter.Connect = () =>
        {
            calls.Add("connect");
            return Task.CompletedTask;
        };

        await adapter.StartAsync((_, _, _) => Task.CompletedTask, CancellationToken.None);

        calls.ShouldBe(["login", "connect"]);
    }

    [Fact]
    public async Task A_Stop_While_The_Login_Is_Pending_Leaves_The_Gateway_Never_Started()
    {
        var login = NewSignal();
        var connects = 0;
        await using var adapter = NewAdapter(new RecordingLogger());
        adapter.Login = () => login.Task;
        adapter.Connect = () =>
        {
            connects++;
            return Task.CompletedTask;
        };

        var start = adapter.StartAsync((_, _, _) => Task.CompletedTask, CancellationToken.None);
        await adapter.StopAsync(CancellationToken.None);
        login.SetResult();
        await start;

        connects.ShouldBe(0);
    }

    [Fact]
    public async Task A_Start_Whose_Token_Is_Cancelled_While_The_Login_Is_Pending_Never_Opens_The_Gateway()
    {
        var login = NewSignal();
        var connects = 0;
        using var startToken = new CancellationTokenSource();
        await using var adapter = NewAdapter(new RecordingLogger());
        adapter.Login = () => login.Task;
        adapter.Connect = () =>
        {
            connects++;
            return Task.CompletedTask;
        };

        var start = adapter.StartAsync((_, _, _) => Task.CompletedTask, startToken.Token);
        await startToken.CancelAsync();
        login.SetResult();
        var thrown = await Record.ExceptionAsync(() => start);

        connects.ShouldBe(0, "a start whose token is cancelled while the login is pending must never open the gateway");
        thrown.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(startToken.Token);
    }

    [Fact]
    public async Task A_Start_Cancelled_During_The_Login_Leaves_The_Adapter_Logged_Out_And_A_Later_Message_Undispatched()
    {
        var login = NewSignal();
        var dispatches = 0;
        var calls = new List<string>();
        using var startToken = new CancellationTokenSource();
        await using var adapter = NewAdapter(new RecordingLogger());
        adapter.Login = () =>
        {
            calls.Add("login");
            return login.Task;
        };
        adapter.Connect = () =>
        {
            calls.Add("connect");
            return Task.CompletedTask;
        };
        adapter.Logout = () =>
        {
            calls.Add("logout");
            return Task.CompletedTask;
        };

        var start = adapter.StartAsync(
            (_, _, _) =>
            {
                Interlocked.Increment(ref dispatches);
                return Task.CompletedTask;
            },
            startToken.Token);
        await startToken.CancelAsync();
        login.SetResult();
        await Should.ThrowAsync<OperationCanceledException>(() => start);
        calls.ShouldBe(["login", "logout"], "a cancelled start must log the client out once the login succeeded");

        await adapter.OnInboundAsync(DirectMessage(), BotUserId, Substitute.For<IMessageChannel>());
        await adapter.StopAsync(CancellationToken.None);

        Volatile.Read(ref dispatches).ShouldBe(0, "a message after a cancelled start must not be dispatched");
    }

    [Fact]
    public async Task A_Logout_That_Fails_On_A_Cancelled_Start_Is_Logged_Once_And_The_Start_Still_Ends_With_The_Cancellation()
    {
        var login = NewSignal();
        var connects = 0;
        var dispatches = 0;
        var failure = new HttpRequestException("503 Service Unavailable", null, HttpStatusCode.ServiceUnavailable);
        var logger = new RecordingLogger();
        using var startToken = new CancellationTokenSource();
        var adapter = NewAdapter(logger);
        adapter.Login = () => login.Task;
        adapter.Connect = () =>
        {
            connects++;
            return Task.CompletedTask;
        };
        adapter.Logout = () => Task.FromException(failure);

        var start = adapter.StartAsync(
            (_, _, _) =>
            {
                Interlocked.Increment(ref dispatches);
                return Task.CompletedTask;
            },
            startToken.Token);
        await startToken.CancelAsync();
        login.SetResult();
        var thrown = await Record.ExceptionAsync(() => start);

        connects.ShouldBe(0, "a start whose logout fails must still never open the gateway");
        thrown.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(startToken.Token);
        logger.Entries.Count(e => e.Level == LogLevel.Warning && ReferenceEquals(e.Exception, failure)).ShouldBe(
            1, "the logout failure must be logged once at warning, never lost");

        await adapter.OnInboundAsync(DirectMessage(), BotUserId, Substitute.For<IMessageChannel>());
        await Should.NotThrowAsync(() => adapter.StopAsync(CancellationToken.None));
        await Should.NotThrowAsync(async () => await adapter.DisposeAsync());
        Volatile.Read(ref dispatches).ShouldBe(0, "the adapter is left stopped, so a later message is not dispatched");
    }

    [Fact]
    public async Task A_Later_Stop_And_Dispose_On_A_Cancelled_Start_Do_Not_Throw_Or_Log_Out_Again()
    {
        var logouts = 0;
        using var startToken = new CancellationTokenSource();
        var adapter = NewAdapter(new RecordingLogger());
        adapter.Login = () => startToken.CancelAsync();
        adapter.Connect = () => Task.CompletedTask;
        adapter.Logout = () =>
        {
            logouts++;
            return Task.CompletedTask;
        };
        await Should.ThrowAsync<OperationCanceledException>(
            () => adapter.StartAsync((_, _, _) => Task.CompletedTask, startToken.Token));

        await Should.NotThrowAsync(() => adapter.StopAsync(CancellationToken.None));
        await Should.NotThrowAsync(async () => await adapter.DisposeAsync());

        logouts.ShouldBe(1, "the cancelled start logs out once; a later stop and dispose find the adapter already stopped");
    }

    [Fact]
    public async Task A_Start_Whose_Token_Is_Cancelled_Before_It_Is_Called_Never_Opens_The_Gateway_And_Logs_Out()
    {
        var calls = new List<string>();
        using var startToken = new CancellationTokenSource();
        await startToken.CancelAsync();
        await using var adapter = NewAdapter(new RecordingLogger());
        adapter.Login = () =>
        {
            calls.Add("login");
            return Task.CompletedTask;
        };
        adapter.Connect = () =>
        {
            calls.Add("connect");
            return Task.CompletedTask;
        };
        adapter.Logout = () =>
        {
            calls.Add("logout");
            return Task.CompletedTask;
        };

        var cancelled = await Should.ThrowAsync<OperationCanceledException>(
            () => adapter.StartAsync((_, _, _) => Task.CompletedTask, startToken.Token));

        cancelled.CancellationToken.ShouldBe(startToken.Token);
        calls.ShouldBe(["login", "logout"]);
    }

    [Fact]
    public async Task A_Start_Whose_Token_Is_Cancelled_As_The_Login_Returns_Never_Opens_The_Gateway_And_Logs_Out()
    {
        var calls = new List<string>();
        using var startToken = new CancellationTokenSource();
        await using var adapter = NewAdapter(new RecordingLogger());
        adapter.Login = async () =>
        {
            calls.Add("login");
            await startToken.CancelAsync();
        };
        adapter.Connect = () =>
        {
            calls.Add("connect");
            return Task.CompletedTask;
        };
        adapter.Logout = () =>
        {
            calls.Add("logout");
            return Task.CompletedTask;
        };

        await Should.ThrowAsync<OperationCanceledException>(
            () => adapter.StartAsync((_, _, _) => Task.CompletedTask, startToken.Token));

        calls.ShouldBe(["login", "logout"]);
    }

    [Fact]
    public async Task A_Stop_And_Then_A_Cancelled_Token_During_The_Login_Fail_The_Start_With_The_Cancellation_And_Never_Open_The_Gateway()
    {
        var login = NewSignal();
        var calls = new List<string>();
        using var startToken = new CancellationTokenSource();
        await using var adapter = NewAdapter(new RecordingLogger());
        adapter.Login = () =>
        {
            calls.Add("login");
            return login.Task;
        };
        adapter.Connect = () =>
        {
            calls.Add("connect");
            return Task.CompletedTask;
        };
        adapter.Logout = () =>
        {
            calls.Add("logout");
            return Task.CompletedTask;
        };

        var start = adapter.StartAsync((_, _, _) => Task.CompletedTask, startToken.Token);
        await adapter.StopAsync(CancellationToken.None);
        await startToken.CancelAsync();
        login.SetResult();

        var cancelled = await Should.ThrowAsync<OperationCanceledException>(() => start);
        cancelled.CancellationToken.ShouldBe(startToken.Token);
        calls.ShouldBe(["login", "logout"]);
    }

    [Fact]
    public async Task A_Start_Token_Cancelled_After_The_Gateway_Started_Cancels_The_Reply_In_Flight_And_Leaves_The_Client_Logged_In()
    {
        var replying = NewSignal();
        var replyCancelled = NewSignal();
        var calls = new List<string>();
        using var startToken = new CancellationTokenSource();
        await using var adapter = NewAdapter(new RecordingLogger());
        adapter.Login = () =>
        {
            calls.Add("login");
            return Task.CompletedTask;
        };
        adapter.Connect = () =>
        {
            calls.Add("connect");
            return Task.CompletedTask;
        };
        adapter.Logout = () =>
        {
            calls.Add("logout");
            return Task.CompletedTask;
        };

        await adapter.StartAsync(
            async (_, _, ct) =>
            {
                replying.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    replyCancelled.TrySetResult();
                    throw;
                }
            },
            startToken.Token);
        await adapter.OnInboundAsync(DirectMessage(), BotUserId, Substitute.For<IMessageChannel>());
        await replying.Task.WaitAsync(Generous);
        await startToken.CancelAsync();

        (await Task.WhenAny(replyCancelled.Task, Task.Delay(Generous))).ShouldBe(
            replyCancelled.Task, "the start token cancels the reply in flight through the lifetime token");
        calls.ShouldBe(["login", "connect"]);
    }

    [Fact]
    public async Task A_Login_Discord_Refuses_While_The_Start_Token_Is_Cancelled_Fails_The_Start_With_The_Refusal()
    {
        var connects = 0;
        using var startToken = new CancellationTokenSource();
        await using var adapter = NewAdapter(new RecordingLogger());
        adapter.Login = async () =>
        {
            await startToken.CancelAsync();
            throw new HttpRequestException("401 Unauthorized", null, HttpStatusCode.Unauthorized);
        };
        adapter.Connect = () =>
        {
            connects++;
            return Task.CompletedTask;
        };

        var failure = await Should.ThrowAsync<HttpRequestException>(
            () => adapter.StartAsync((_, _, _) => Task.CompletedTask, startToken.Token));

        failure.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        connects.ShouldBe(0);
    }

    [Fact]
    public async Task A_Stop_While_The_Gateway_Is_Starting_Waits_For_That_Start_Before_It_Stops_The_Client()
    {
        var connecting = NewSignal();
        var connected = NewSignal();
        await using var adapter = NewAdapter(new RecordingLogger());
        adapter.Login = () => Task.CompletedTask;
        adapter.Connect = () =>
        {
            connecting.SetResult();
            return connected.Task;
        };

        var start = adapter.StartAsync((_, _, _) => Task.CompletedTask, CancellationToken.None);
        await connecting.Task;
        var stop = adapter.StopAsync(CancellationToken.None);

        (await Task.WhenAny(stop, Task.Delay(TimeSpan.FromMilliseconds(500)))).ShouldNotBe(
            stop, "stop must not stop the client while the gateway start is still in flight");
        connected.SetResult();
        await start;
        await stop;
    }

    [Fact]
    public async Task A_Dispose_While_The_Login_Is_Pending_Leaves_The_Gateway_Never_Started_And_The_Start_Returns()
    {
        var login = NewSignal();
        var connects = 0;
        var adapter = NewAdapter(new RecordingLogger());
        adapter.Login = () => login.Task;
        adapter.Connect = () =>
        {
            connects++;
            return Task.CompletedTask;
        };

        var start = adapter.StartAsync((_, _, _) => Task.CompletedTask, CancellationToken.None);
        await adapter.DisposeAsync();
        login.SetResult();

        await Should.NotThrowAsync(() => start);
        connects.ShouldBe(0);
    }

    [Fact]
    public async Task A_Login_Discord_Refuses_Fails_The_Start_And_Never_Starts_The_Gateway()
    {
        var connects = 0;
        await using var adapter = NewAdapter(new RecordingLogger());
        adapter.Login = () => Task.FromException(new HttpRequestException("401 Unauthorized", null, HttpStatusCode.Unauthorized));
        adapter.Connect = () =>
        {
            connects++;
            return Task.CompletedTask;
        };

        var failure = await Should.ThrowAsync<HttpRequestException>(
            () => adapter.StartAsync((_, _, _) => Task.CompletedTask, CancellationToken.None));

        failure.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        connects.ShouldBe(0);
    }

    [Fact]
    public async Task A_Message_Caught_By_A_Stop_Is_Dropped_Quietly_And_Never_Fails()
    {
        const int Rounds = 300;
        var unheard = DirectMessage() with { GuildId = 9999 };
        var channel = Substitute.For<IMessageChannel>();
        var failures = new ConcurrentQueue<Exception>();
        for (var round = 0; round < Rounds; round++)
        {
            await using var adapter = NewAdapter(new RecordingLogger());
            adapter.Arm((_, _, _) => Task.CompletedTask, CancellationToken.None);
            var stopped = 0;
            var arrived = 0;
            var arriving = Task.Run(async () =>
            {
                while (Volatile.Read(ref stopped) == 0)
                {
                    try
                    {
                        await adapter.OnInboundAsync(unheard, BotUserId, channel);
                    }
                    catch (InvalidOperationException ex)
                    {
                        failures.Enqueue(ex);
                    }

                    Volatile.Write(ref arrived, 1);
                }
            });

            SpinWait.SpinUntil(() => Volatile.Read(ref arrived) == 1);
            await adapter.StopAsync(CancellationToken.None);
            Volatile.Write(ref stopped, 1);
            await arriving;
        }

        failures.Select(f => f.Message).ShouldBeEmpty();
    }

    private static DiscordChatAdapter NewAdapter(ILogger<DiscordChatAdapter> logger)
        => new(
            Options.Create(new DiscordOptions { BotToken = "not-a-real-token" }),
            new DiscordMessageTranslator(),
            logger,
            new AccessPolicy(new AccessOptions()));

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
