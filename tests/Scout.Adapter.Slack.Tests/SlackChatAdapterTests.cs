using Microsoft.Extensions.Logging.Abstractions;
using Scout.Conversation.Abstractions;
using SlackNet;
using SlackNet.Events;
using SlackNet.WebApi;

namespace Scout.Adapter.Slack.Tests;

/// <summary>
/// Adapter-level tests with the SlackNet API and socket-mode clients
/// substituted, so no network and no real login. Focuses on the inverted
/// handler contract and the lifetime-token cancellation guarantee on stop.
/// </summary>
public sealed class SlackChatAdapterTests
{
    private const string BotUserId = "UBOT";

    private static (SlackChatAdapter Adapter, ISlackSocketModeClient Socket, IChatApi Chat) Build(
        ISlackSocketModeClient? socketModeClient = null, Exception? authFails = null)
    {
        var api = Substitute.For<ISlackApiClient>();
        var auth = Substitute.For<IAuthApi>();
        auth.Test(Arg.Any<CancellationToken>())
            .Returns(authFails is null
                ? Task.FromResult(new AuthTestResponse { UserId = BotUserId, Team = "T1" })
                : Task.FromException<AuthTestResponse>(authFails));
        api.Auth.Returns(auth);

        var chat = Substitute.For<IChatApi>();
        chat.PostMessage(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(new PostMessageResponse { Channel = "C1", Ts = "1.1" });
        api.Chat.Returns(chat);

        var socket = socketModeClient ?? Substitute.For<ISlackSocketModeClient>();

        var adapter = new SlackChatAdapter(
            api, socket, new SlackMessageTranslator(),
            NullLogger<SlackChatAdapter>.Instance, BotUserId);
        return (adapter, socket, chat);
    }

    private static MessageEvent Message(string text, string channelType = "im", string user = "UHUMAN")
        => new()
        {
            User = user,
            Channel = "C1",
            Text = text,
            Ts = "1700000000.000100",
            ChannelType = channelType,
        };

    [Fact]
    public void Name_Is_Slack()
    {
        var (adapter, _, _) = Build();
        adapter.Name.ShouldBe("Slack");
    }

    [Fact]
    public async Task StartAsync_Connects_Socket_Mode()
    {
        var (adapter, socket, _) = Build();

        await adapter.StartAsync((_, _, _) => Task.CompletedTask, CancellationToken.None);

        await socket.Received(1).Connect(
            Arg.Any<SlackNet.SocketMode.SocketModeConnectionOptions>(), Arg.Any<CancellationToken>());

        await adapter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_Disconnects_Socket_Mode()
    {
        var (adapter, socket, _) = Build();
        await adapter.StartAsync((_, _, _) => Task.CompletedTask, CancellationToken.None);

        await adapter.StopAsync(CancellationToken.None);

        await socket.Received(1).DisconnectAsync();
    }

    [Fact]
    public async Task StopAsync_Waits_For_Socket_Mode_Disconnect_To_Finish()
    {
        var (adapter, socket, _) = Build();
        var disconnect = new TaskCompletionSource();
        socket.DisconnectAsync().Returns(disconnect.Task);
        await adapter.StartAsync((_, _, _) => Task.CompletedTask, CancellationToken.None);

        var stop = adapter.StopAsync(CancellationToken.None);

        stop.IsCompleted.ShouldBeFalse();
        disconnect.SetResult();
        await stop;
    }

    [Fact]
    public async Task StopAsync_When_Socket_Mode_Disconnect_Fails_Surfaces_The_Fault_And_Stops_Handling()
    {
        var (adapter, socket, _) = Build();
        socket.DisconnectAsync().Returns(Task.FromException(new IOException("connection reset")));
        var invoked = false;
        await adapter.StartAsync((_, _, _) => { invoked = true; return Task.CompletedTask; },
            CancellationToken.None);

        var fault = await Should.ThrowAsync<IOException>(() => adapter.StopAsync(CancellationToken.None));
        await adapter.OnMessageReceivedAsync(Message("hello scout"));

        fault.Message.ShouldBe("connection reset");
        invoked.ShouldBeFalse();
    }

    [Fact]
    public async Task StopAsync_When_Socket_Mode_Disconnect_Never_Completes_Returns_On_Cancellation()
    {
        var (adapter, socket, _) = Build();
        socket.DisconnectAsync().Returns(new TaskCompletionSource().Task);
        await adapter.StartAsync((_, _, _) => Task.CompletedTask, CancellationToken.None);
        using var shutdown = new CancellationTokenSource();

        var stop = adapter.StopAsync(shutdown.Token);
        await shutdown.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => stop.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task DisposeAsync_When_Socket_Mode_Disconnect_Fails_Still_Disposes_The_Socket_Client()
    {
        var socket = Substitute.For<ISlackSocketModeClient>();
        socket.DisconnectAsync().Returns(Task.FromException(new IOException("connection reset")));
        var (adapter, _, _) = Build(socket);
        await adapter.StartAsync((_, _, _) => Task.CompletedTask, CancellationToken.None);

        var fault = await Should.ThrowAsync<IOException>(async () => await adapter.DisposeAsync());

        fault.Message.ShouldBe("connection reset");
        await socket.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task Inbound_Dm_Reaches_The_Handler_With_A_Bound_Sink()
    {
        var (adapter, _, _) = Build();
        ChatMessage? received = null;
        IResponseSink? sink = null;
        await adapter.StartAsync((m, s, _) => { received = m; sink = s; return Task.CompletedTask; },
            CancellationToken.None);

        await adapter.OnMessageReceivedAsync(Message("hello scout"));

        received.ShouldNotBeNull();
        received!.Text.ShouldBe("hello scout");
        sink.ShouldBeOfType<Scout.Adapter.Slack.Streaming.SlackResponseSink>();

        await adapter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Inbound_Channel_Message_Binds_Sink_To_Its_Own_Ts()
    {
        var (adapter, _, chat) = Build();
        IResponseSink? sink = null;
        await adapter.StartAsync((_, s, _) => { sink = s; return Task.CompletedTask; },
            CancellationToken.None);

        var message = Message("<@UBOT> hi", channelType: "channel");
        message.Ts = "1700000000.000100";
        await adapter.OnMessageReceivedAsync(message);

        // Driving the sink's first post reveals the thread it is bound to: a
        // non-threaded channel message starts a thread on its own ts.
        await sink.ShouldNotBeNull().BeginAsync(CancellationToken.None);
        await chat.Received(1).PostMessage(
            Arg.Is<Message>(m => m.Channel == "C1" && m.ThreadTs == "1700000000.000100"),
            Arg.Any<CancellationToken>());

        await adapter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Inbound_Threaded_Channel_Message_Binds_Sink_To_Parent_Thread()
    {
        var (adapter, _, chat) = Build();
        IResponseSink? sink = null;
        await adapter.StartAsync((_, s, _) => { sink = s; return Task.CompletedTask; },
            CancellationToken.None);

        var message = Message("<@UBOT> hi", channelType: "channel");
        message.Ts = "1700000000.000200";
        message.ThreadTs = "1700000000.000100";
        await adapter.OnMessageReceivedAsync(message);

        // An already-threaded message replies under the parent thread, not its
        // own ts.
        await sink.ShouldNotBeNull().BeginAsync(CancellationToken.None);
        await chat.Received(1).PostMessage(
            Arg.Is<Message>(m => m.Channel == "C1" && m.ThreadTs == "1700000000.000100"),
            Arg.Any<CancellationToken>());

        await adapter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Inbound_From_The_Bot_Is_Ignored()
    {
        var (adapter, _, _) = Build();
        var invoked = false;
        await adapter.StartAsync((_, _, _) => { invoked = true; return Task.CompletedTask; },
            CancellationToken.None);

        await adapter.OnMessageReceivedAsync(Message("<@UBOT> hi", user: BotUserId));

        invoked.ShouldBeFalse();
        await adapter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_Message_While_Running_Reaches_The_Handler_Of_That_Start_And_One_After_Stop_Reaches_None_And_Never_Throws()
    {
        var (adapter, _, _) = Build();
        var received = new List<string>();
        await adapter.StartAsync((message, _, _) =>
        {
            received.Add(message.Text);
            return Task.CompletedTask;
        }, CancellationToken.None);

        await adapter.OnMessageReceivedAsync(Message("while running"));
        await adapter.StopAsync(CancellationToken.None);
        await Should.NotThrowAsync(() => adapter.OnMessageReceivedAsync(Message("after stop")));

        received.ShouldBe(["while running"]);
    }

    [Fact]
    public async Task A_Start_Whose_Auth_Test_Fails_Surfaces_The_Fault_Never_Connects_Hands_No_Message_To_The_Handler_And_Still_Stops()
    {
        var (adapter, socket, _) = Build(authFails: new HttpRequestException("auth.test timed out"));
        var invoked = false;

        var fault = await Should.ThrowAsync<HttpRequestException>(() => adapter.StartAsync((_, _, _) =>
        {
            invoked = true;
            return Task.CompletedTask;
        }, CancellationToken.None));
        await Should.NotThrowAsync(() => adapter.OnMessageReceivedAsync(Message("hello scout")));
        await adapter.StopAsync(CancellationToken.None);

        fault.Message.ShouldBe("auth.test timed out");
        invoked.ShouldBeFalse();
        await socket.DidNotReceive().Connect(Arg.Any<SlackNet.SocketMode.SocketModeConnectionOptions>(), Arg.Any<CancellationToken>());
        await socket.Received(1).DisconnectAsync();
    }

    [Fact]
    public async Task A_Message_A_Stop_Overtakes_Is_Dropped_Quietly_And_Never_Throws()
    {
        var (adapter, _, _) = Build();
        var received = new List<string>();
        await adapter.StartAsync((message, _, _) =>
        {
            received.Add(message.Text);
            return Task.CompletedTask;
        }, CancellationToken.None);
        var stoppedBeforeTheRead = false;
        adapter.Lifecycle.BeforeSessionRead = () =>
        {
            adapter.Lifecycle.BeforeSessionRead = null;
            adapter.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            stoppedBeforeTheRead = true;
        };

        await Should.NotThrowAsync(() => adapter.OnMessageReceivedAsync(Message("overtaken by the stop")));

        stoppedBeforeTheRead.ShouldBeTrue();
        received.ShouldBeEmpty();
    }

    [Fact]
    public async Task Restart_After_Stop_Is_Terminal_And_Throws()
    {
        var (adapter, _, _) = Build();
        await adapter.StartAsync((_, _, _) => Task.CompletedTask, CancellationToken.None);
        await adapter.StopAsync(CancellationToken.None);

        await Should.ThrowAsync<InvalidOperationException>(
            () => adapter.StartAsync((_, _, _) => Task.CompletedTask, CancellationToken.None));
    }

    [Fact]
    public async Task Lifetime_Token_Is_Cancelled_On_StopAsync_For_In_Flight_Handler()
    {
        var (adapter, _, _) = Build();
        var handlerEntered = new TaskCompletionSource();
        var observedCancellation = new TaskCompletionSource<bool>();

        await adapter.StartAsync(
            async (_, _, token) =>
            {
                handlerEntered.TrySetResult();
                try
                {
                    // Block until the lifetime token trips on StopAsync.
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                    observedCancellation.TrySetResult(true);
                    throw;
                }
            },
            CancellationToken.None);

        // Process a message on a background task; it parks inside the handler.
        var processing = adapter.OnMessageReceivedAsync(Message("scout are you there"));

        await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await adapter.StopAsync(CancellationToken.None);

        (await observedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();
        // The adapter swallows the shutdown cancellation; processing completes.
        await processing.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("auth")]
    [InlineData("connect")]
    public async Task A_Start_Whose_Auth_Or_Connect_Fails_Leaves_The_Adapter_Stopped(string failing)
    {
        var socket = Substitute.For<ISlackSocketModeClient>();
        socket.Connect(Arg.Any<SlackNet.SocketMode.SocketModeConnectionOptions>(), Arg.Any<CancellationToken>())
            .Returns(failing == "connect" ? Task.FromException(new IOException("socket reset")) : Task.CompletedTask);
        var (adapter, _, _) = Build(socket, failing == "auth" ? new HttpRequestException("auth.test timed out") : null);
        var invoked = false;

        var fault = await Should.ThrowAsync<Exception>(() => adapter.StartAsync((_, _, _) =>
        {
            invoked = true;
            return Task.CompletedTask;
        }, CancellationToken.None));
        await adapter.OnMessageReceivedAsync(Message("hello scout"));

        fault.ShouldBeOfType(failing == "auth" ? typeof(HttpRequestException) : typeof(IOException), "the start throws the original fault");
        fault.Message.ShouldBe(failing == "auth" ? "auth.test timed out" : "socket reset");
        adapter.Lifecycle.IsRunning.ShouldBeFalse("a failed start leaves the adapter stopped");
        invoked.ShouldBeFalse("a stopped adapter hands no message to the handler of the failed start");
    }

    [Theory]
    [InlineData("auth")]
    [InlineData("connect")]
    public async Task A_Stop_That_Arrives_During_Start_Wins_And_Leaves_No_Connection_Open(string phase)
    {
        var connected = false;
        var authAnswered = new TaskCompletionSource<AuthTestResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = Substitute.For<ISlackApiClient>();
        var auth = Substitute.For<IAuthApi>();
        auth.Test(Arg.Any<CancellationToken>()).Returns(authAnswered.Task);
        api.Auth.Returns(auth);
        var socket = Substitute.For<ISlackSocketModeClient>();
        socket.Connect(Arg.Any<SlackNet.SocketMode.SocketModeConnectionOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => OpenAsync());
        socket.DisconnectAsync().Returns(_ =>
        {
            connected = false;
            return Task.CompletedTask;
        });
        var adapter = new SlackChatAdapter(api, socket, new SlackMessageTranslator(), NullLogger<SlackChatAdapter>.Instance, BotUserId);
        var invoked = false;

        var start = adapter.StartAsync((_, _, _) =>
        {
            invoked = true;
            return Task.CompletedTask;
        }, CancellationToken.None);
        if (phase == "connect")
        {
            authAnswered.SetResult(new AuthTestResponse { UserId = BotUserId, Team = "T1" });
            await connectEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        var stop = adapter.StopAsync(CancellationToken.None);
        authAnswered.TrySetResult(new AuthTestResponse { UserId = BotUserId, Team = "T1" });
        connectReleased.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(10));
        await start.WaitAsync(TimeSpan.FromSeconds(10));
        await adapter.OnMessageReceivedAsync(Message("hello scout"));

        connected.ShouldBeFalse("the stop won: no Socket Mode connection is left open after it");
        adapter.Lifecycle.IsRunning.ShouldBeFalse();
        invoked.ShouldBeFalse("a stopped adapter hands no message to the handler");

        async Task OpenAsync()
        {
            connectEntered.TrySetResult();
            await connectReleased.Task;
            connected = true;
        }
    }

    [Fact]
    public async Task A_Stop_That_Arrives_While_The_Connect_Is_Still_Pending_Returns_Promptly_And_Leaves_The_Adapter_Stopped()
    {
        var connectToken = CancellationToken.None;
        var connectEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var socket = Substitute.For<ISlackSocketModeClient>();
        socket.Connect(Arg.Any<SlackNet.SocketMode.SocketModeConnectionOptions>(), Arg.Any<CancellationToken>())
            .Returns(call => OpenAsync(call.ArgAt<CancellationToken>(1)));
        var (adapter, _, _) = Build(socket);
        var invoked = false;
        using var shutdown = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        var start = adapter.StartAsync((_, _, _) =>
        {
            invoked = true;
            return Task.CompletedTask;
        }, CancellationToken.None);
        await connectEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stop = adapter.StopAsync(shutdown.Token);

        (await Record.ExceptionAsync(() => stop.WaitAsync(TimeSpan.FromSeconds(10)))).ShouldBeNull(
            "a stop that arrives while the connect is still pending returns promptly, long before its own token fires");
        (await Record.ExceptionAsync(() => start.WaitAsync(TimeSpan.FromSeconds(10)))).ShouldBeNull(
            "the start the stop overtook returns, neither cancelled nor faulted");
        await adapter.OnMessageReceivedAsync(Message("hello scout"));

        adapter.Lifecycle.IsRunning.ShouldBeFalse("the stop leaves the adapter stopped");
        connectToken.IsCancellationRequested.ShouldBeTrue("the stop cancelled the pending connect instead of letting it open");
        await socket.Received(1).DisconnectAsync();
        invoked.ShouldBeFalse("a stopped adapter hands no message to the handler");

        Task OpenAsync(CancellationToken token)
        {
            connectToken = token;
            connectEntered.TrySetResult();
            return Task.Delay(Timeout.Infinite, token);
        }
    }
}
