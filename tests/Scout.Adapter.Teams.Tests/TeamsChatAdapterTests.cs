using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scout.Adapter.Teams;
using Scout.Conversation.Abstractions;

namespace Scout.Adapter.Teams.Tests;

public sealed class TeamsChatAdapterTests
{
    private const string BotId = "28:bot";
    private const string BotName = "Scout";
    private const string HumanId = "29:human";
    private const string ConversationId = "a:conv";

    private static TeamsChatAdapter Adapter() =>
        new(
            Options.Create(new TeamsOptions { AppId = "app-id" }),
            new TeamsMessageTranslator(),
            TimeProvider.System,
            NullLogger<TeamsChatAdapter>.Instance);

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
}
