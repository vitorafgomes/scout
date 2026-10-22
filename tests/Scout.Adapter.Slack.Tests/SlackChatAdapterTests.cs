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
        ISlackSocketModeClient? socketModeClient = null)
    {
        var api = Substitute.For<ISlackApiClient>();
        var auth = Substitute.For<IAuthApi>();
        auth.Test(Arg.Any<CancellationToken>())
            .Returns(new AuthTestResponse { UserId = BotUserId, Team = "T1" });
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
}
