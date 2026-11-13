using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Scout.Conversation.Abstractions;

namespace Scout.Adapter.Teams.Tests;

public sealed class TeamsChatAdapterTests
{
    private const string BotId = "28:bot";
    private const string BotName = "Scout";
    private const string HumanId = "29:human";
    private const string ConversationId = "a:conv";

    private static TeamsChatAdapter Adapter(ILogger<TeamsChatAdapter>? logger = null) =>
        new(
            Options.Create(new TeamsOptions { AppId = "app-id" }),
            new TeamsMessageTranslator(),
            TimeProvider.System,
            logger ?? NullLogger<TeamsChatAdapter>.Instance);

    private static ITurnContext TurnFor(Activity activity, List<IActivity> sent)
    {
        var ctx = Substitute.For<ITurnContext>();
        ctx.Activity.Returns(activity);
        ctx.SendActivityAsync(Arg.Any<IActivity>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                sent.Add(call.Arg<IActivity>());
                return Task.FromResult(new ResourceResponse("res-1"));
            });
        return ctx;
    }

    private static Activity MessageActivity(string text, string? conversationType = "personal") =>
        new()
        {
            Id = "act-1",
            Type = ActivityTypes.Message,
            Text = text,
            From = new ChannelAccount(id: HumanId, name: "Human"),
            Recipient = new ChannelAccount(id: BotId, name: BotName),
            Conversation = new ConversationAccount(id: ConversationId, conversationType: conversationType),
            Timestamp = DateTimeOffset.UtcNow,
        };

    [Fact]
    public void Ctor_Throws_When_AppId_Missing()
    {
        Should.Throw<ArgumentException>(() => new TeamsChatAdapter(
            Options.Create(new TeamsOptions { AppId = string.Empty }),
            new TeamsMessageTranslator(),
            TimeProvider.System,
            NullLogger<TeamsChatAdapter>.Instance));
    }

    [Fact]
    public void Name_Is_Teams()
    {
        Adapter().Name.ShouldBe("Teams");
    }

    [Fact]
    public async Task ProcessActivity_Before_Start_Is_NoOp()
    {
        var adapter = Adapter();
        var sent = new List<IActivity>();
        var ctx = TurnFor(MessageActivity("hello"), sent);

        await adapter.ProcessActivityAsync(ctx, CancellationToken.None);

        sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task ProcessActivity_Invokes_Handler_With_Translated_Message_And_Bound_Sink()
    {
        var adapter = Adapter();
        ChatMessage? received = null;

        await adapter.StartAsync((msg, sink, ct) =>
        {
            received = msg;
            // Drive a minimal lifecycle to prove the sink reaches the turn.
            return sink.CompleteAsync("done", Array.Empty<Citation>(), ct);
        }, CancellationToken.None);

        var sent = new List<IActivity>();
        var ctx = TurnFor(MessageActivity("is prod healthy?"), sent);
        await adapter.ProcessActivityAsync(ctx, CancellationToken.None);

        received.ShouldNotBeNull();
        received!.Text.ShouldBe("is prod healthy?");
        received.UserId.ShouldBe(HumanId);
        received.ChannelId.ShouldBe(ConversationId);

        // The sink's send was bound to this turn: the final activity went out.
        sent.ShouldHaveSingleItem();
        sent[0].Type.ShouldBe(ActivityTypes.Message);
    }

    [Fact]
    public async Task ProcessActivity_Ignores_Non_Message_Activity()
    {
        var adapter = Adapter();
        var invoked = false;
        await adapter.StartAsync((_, _, _) =>
        {
            invoked = true;
            return Task.CompletedTask;
        }, CancellationToken.None);

        var activity = MessageActivity("typing");
        activity.Type = ActivityTypes.Typing;
        var sent = new List<IActivity>();
        await adapter.ProcessActivityAsync(TurnFor(activity, sent), CancellationToken.None);

        invoked.ShouldBeFalse();
        sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task ProcessActivity_Group_Without_Mention_Is_Ignored()
    {
        var adapter = Adapter();
        var invoked = false;
        await adapter.StartAsync((_, _, _) =>
        {
            invoked = true;
            return Task.CompletedTask;
        }, CancellationToken.None);

        var activity = MessageActivity("hello team", conversationType: "groupChat");
        var sent = new List<IActivity>();
        await adapter.ProcessActivityAsync(TurnFor(activity, sent), CancellationToken.None);

        invoked.ShouldBeFalse();
    }

    [Fact]
    public async Task ProcessActivity_Handler_Throw_Is_Swallowed()
    {
        var adapter = Adapter();
        await adapter.StartAsync((_, _, _) => throw new InvalidOperationException("boom"),
            CancellationToken.None);

        var sent = new List<IActivity>();
        var ctx = TurnFor(MessageActivity("hi"), sent);

        // A handler failure must not escape the adapter (per-turn try/catch).
        await Should.NotThrowAsync(() => adapter.ProcessActivityAsync(ctx, CancellationToken.None));
    }

    [Fact]
    public async Task A_Group_Mention_Matched_By_Id_Reaches_The_Handler_Without_Its_Tag_When_The_Tag_Differs_From_The_Bots_Name()
    {
        var adapter = Adapter();
        var received = new List<string>();
        await adapter.StartAsync((message, _, _) =>
        {
            received.Add(message.Text);
            return Task.CompletedTask;
        }, CancellationToken.None);
        var activity = MessageActivity("<at>Scout Bot</at> is prod healthy? <at>Alice</at>", conversationType: "groupChat");
        var bot = new Entity();
        bot.SetAs(new Mention(new ChannelAccount(id: BotId, name: "Scout Bot"), "<at>Scout Bot</at>"));
        var alice = new Entity();
        alice.SetAs(new Mention(new ChannelAccount(id: "29:alice", name: "Alice"), "<at>Alice</at>"));
        activity.Entities = [bot, alice];

        await adapter.ProcessActivityAsync(TurnFor(activity, []), CancellationToken.None);

        received.ShouldBe(["is prod healthy? <at>Alice</at>"]);
    }

    [Fact]
    public async Task An_Activity_Whose_Mention_Entity_Cannot_Be_Read_Never_Fails_The_Webhook_And_Is_Logged_With_Its_Id()
    {
        var logger = new RecordingLogger();
        var adapter = Adapter(logger);
        var invoked = false;
        await adapter.StartAsync((_, _, _) =>
        {
            invoked = true;
            return Task.CompletedTask;
        }, CancellationToken.None);
        var activity = MessageActivity("<at>Scout</at> is prod healthy?", conversationType: "groupChat");
        activity.Entities = [new Entity("mention") { Properties = JObject.Parse("""{"mentioned":"not an account","text":"<at>Scout</at>"}""") }];
        var sent = new List<IActivity>();

        await Should.NotThrowAsync(() => adapter.ProcessActivityAsync(TurnFor(activity, sent), CancellationToken.None));

        invoked.ShouldBeFalse();
        sent.ShouldBeEmpty();
        logger.Errors.ShouldBe(["TeamsChatAdapter failed while processing activity act-1"]);
    }

    [Fact]
    public async Task Stop_Then_Process_Is_NoOp_And_Adapter_Is_ReStartable()
    {
        var adapter = Adapter();
        var count = 0;
        await adapter.StartAsync((_, _, _) =>
        {
            count++;
            return Task.CompletedTask;
        }, CancellationToken.None);
        await adapter.StopAsync(CancellationToken.None);

        var sent = new List<IActivity>();
        await adapter.ProcessActivityAsync(TurnFor(MessageActivity("hi"), sent), CancellationToken.None);
        count.ShouldBe(0);

        // Webhook adapter re-arms after stop.
        await Should.NotThrowAsync(() =>
            adapter.StartAsync((_, _, _) => Task.CompletedTask, CancellationToken.None));
    }

    [Fact]
    public async Task A_Message_While_Running_Reaches_The_Handler_Of_That_Start_And_One_After_Stop_Reaches_None_And_Never_Throws()
    {
        var adapter = Adapter();
        var first = new List<string>();
        var second = new List<string>();
        await adapter.StartAsync((message, _, _) =>
        {
            first.Add(message.Text);
            return Task.CompletedTask;
        }, CancellationToken.None);

        await adapter.ProcessActivityAsync(TurnFor(MessageActivity("while running"), []), CancellationToken.None);
        await adapter.StopAsync(CancellationToken.None);
        await Should.NotThrowAsync(() => adapter.ProcessActivityAsync(TurnFor(MessageActivity("after stop"), []), CancellationToken.None));

        first.ShouldBe(["while running"]);
        await adapter.StartAsync((message, _, _) =>
        {
            second.Add(message.Text);
            return Task.CompletedTask;
        }, CancellationToken.None);
        await adapter.ProcessActivityAsync(TurnFor(MessageActivity("after the restart"), []), CancellationToken.None);
        first.ShouldBe(["while running"]);
        second.ShouldBe(["after the restart"]);
    }

    [Fact]
    public async Task A_Start_While_A_Stop_Is_Unwinding_Keeps_Its_Handler_After_That_Stop_Returns()
    {
        var adapter = Adapter();
        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restarted = new List<string>();
        Func<ChatMessage, IResponseSink, CancellationToken, Task> second = (message, _, _) =>
        {
            restarted.Add(message.Text);
            return Task.CompletedTask;
        };
        await adapter.StartAsync(async (_, _, ct) =>
        {
            ct.Register(() => _ = adapter.StartAsync(second, CancellationToken.None));
            inFlight.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }, CancellationToken.None);
        var turn = adapter.ProcessActivityAsync(TurnFor(MessageActivity("first"), []), CancellationToken.None);
        await inFlight.Task;

        await adapter.StopAsync(CancellationToken.None);
        await turn;
        await adapter.ProcessActivityAsync(TurnFor(MessageActivity("after the restart"), []), CancellationToken.None);

        restarted.ShouldBe(["after the restart"]);
    }

    [Fact]
    public async Task Two_Concurrent_Stops_And_A_Start_Never_Leave_A_Running_Adapter_Without_Its_Handler()
    {
        const int Rounds = 10_000;
        var endedRunning = 0;
        var handledWhileRunning = 0;
        for (var round = 0; round < Rounds; round++)
        {
            using var adapter = Adapter();
            var handled = 0;
            Func<ChatMessage, IResponseSink, CancellationToken, Task> handler = (_, _, _) =>
            {
                handled++;
                return Task.CompletedTask;
            };
            await adapter.StartAsync(handler, CancellationToken.None);
            var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var race = Task.WhenAll(
                After(go, () => adapter.StopAsync(CancellationToken.None)),
                After(go, () => adapter.StopAsync(CancellationToken.None)),
                After(go, async () =>
                {
                    for (var attempt = 0; attempt < 50 && !await StartsAsync(adapter, handler); attempt++)
                    {
                    }
                }));

            go.SetResult();
            await race;

            if (await StartsAsync(adapter, handler))
            {
                continue;
            }

            endedRunning++;
            await adapter.ProcessActivityAsync(TurnFor(MessageActivity("after the race"), []), CancellationToken.None);
            handledWhileRunning += handled;
        }

        endedRunning.ShouldBeGreaterThan(0);
        handledWhileRunning.ShouldBe(endedRunning);
    }

    [Fact]
    public async Task A_Turn_Racing_Start_Is_Never_Dropped_For_A_Missing_Handler()
    {
        var adapter = Adapter();
        var received = new List<string>();
        bool? runningWhenTheTurnArrived = null;
        Task? racingTurn = null;
        adapter.Lifecycle.SessionPublished = () =>
        {
            runningWhenTheTurnArrived = adapter.Lifecycle.IsRunning;
            racingTurn = adapter.ProcessActivityAsync(TurnFor(MessageActivity("racing the start"), []), CancellationToken.None);
        };

        await adapter.StartAsync((message, _, _) =>
        {
            received.Add(message.Text);
            return Task.CompletedTask;
        }, CancellationToken.None);
        await racingTurn.ShouldNotBeNull();

        runningWhenTheTurnArrived.ShouldBe(true);
        received.ShouldBe(["racing the start"]);
    }

    private static async Task After(TaskCompletionSource go, Func<Task> act)
    {
        await go.Task.ConfigureAwait(false);
        await act().ConfigureAwait(false);
    }

    private static async Task<bool> StartsAsync(TeamsChatAdapter adapter, Func<ChatMessage, IResponseSink, CancellationToken, Task> handler)
    {
        try
        {
            await adapter.StartAsync(handler, CancellationToken.None);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private sealed class RecordingLogger : ILogger<TeamsChatAdapter>
    {
        public List<string> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error && exception is not null)
            {
                Errors.Add(formatter(state, exception));
            }
        }
    }
}
