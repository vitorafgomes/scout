using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scout.Conversation;
using Scout.Conversation.Abstractions;
using SlackNet;
using SlackNet.Events;
using SlackNet.WebApi;

namespace Scout.Adapter.Slack.Tests;

public sealed class SlackOriginPosterTests
{
    private const string BotUserId = "UBOT";

    private static (SlackChatAdapter Adapter, IChatApi Chat, List<Message> Posted) Build()
    {
        var api = Substitute.For<ISlackApiClient>();
        var auth = Substitute.For<IAuthApi>();
        auth.Test(Arg.Any<CancellationToken>()).Returns(new AuthTestResponse { UserId = BotUserId, Team = "T1" });
        api.Auth.Returns(auth);
        var posted = new List<Message>();
        var chat = Substitute.For<IChatApi>();
        chat.PostMessage(Arg.Any<Message>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            posted.Add(call.Arg<Message>());
            return new PostMessageResponse { Channel = "C1", Ts = "1.1" };
        });
        api.Chat.Returns(chat);
        var adapter = new SlackChatAdapter(
            api, Substitute.For<ISlackSocketModeClient>(), new SlackMessageTranslator(),
            NullLogger<SlackChatAdapter>.Instance, BotUserId);
        return (adapter, chat, posted);
    }

    private static async Task<SlackChatAdapter> StartedAsync(SlackChatAdapter adapter, Action<ChatMessage>? received = null)
    {
        await adapter.StartAsync((m, _, _) => { received?.Invoke(m); return Task.CompletedTask; }, CancellationToken.None);
        return adapter;
    }

    [Theory]
    [InlineData(null, "slack:C1:1700000000.000200")]
    [InlineData("1700000000.000100", "slack:C1:1700000000.000100")]
    public async Task An_Inbound_Message_Carries_The_Channel_And_The_Reply_Thread_As_Its_Origin(string? threadTs, string expected)
    {
        var (adapter, _, _) = Build();
        ChatMessage? received = null;
        await StartedAsync(adapter, m => received = m);

        await adapter.OnMessageReceivedAsync(new MessageEvent
        {
            User = "UHUMAN",
            Channel = "C1",
            Text = "<@UBOT> start the weekly video",
            Ts = "1700000000.000200",
            ThreadTs = threadTs,
            ChannelType = "channel",
        });

        received.ShouldNotBeNull().OriginReference.ShouldBe(expected);
        await adapter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_Reference_Posts_Plain_Text_Into_Its_Channel_And_Thread()
    {
        var (adapter, _, posted) = Build();
        await StartedAsync(adapter);

        var ok = await new SlackOriginPoster(adapter, NullLogger<SlackOriginPoster>.Instance)
            .PostAsync("C1:1700000000.000100", "Script ready for review.", CancellationToken.None);

        ok.ShouldBeTrue();
        var message = posted.ShouldHaveSingleItem();
        message.Channel.ShouldBe("C1");
        message.ThreadTs.ShouldBe("1700000000.000100");
        message.Text.ShouldBe("Script ready for review.");
        await adapter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Broadcast_Mentions_In_The_Text_Are_Posted_As_Plain_Text()
    {
        var (adapter, _, posted) = Build();
        await StartedAsync(adapter);

        await new SlackOriginPoster(adapter, NullLogger<SlackOriginPoster>.Instance).PostAsync(
            "C1:1.1",
            "Done <!channel> <!here> <!everyone> <!subteam^S123|@ops> & @channel @here",
            CancellationToken.None);

        var text = posted.ShouldHaveSingleItem().Text;
        text.ShouldBe("Done &lt;!channel&gt; &lt;!here&gt; &lt;!everyone&gt; &lt;!subteam^S123|@ops&gt; &amp; @channel @here");
        text.ShouldNotContain("<!");
        await adapter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_Text_Over_The_Reply_Limit_Is_Capped_To_3000_Characters()
    {
        var (adapter, _, posted) = Build();
        await StartedAsync(adapter);

        await new SlackOriginPoster(adapter, NullLogger<SlackOriginPoster>.Instance)
            .PostAsync("C1:1.1", new string('a', 5000), CancellationToken.None);

        var text = posted.ShouldHaveSingleItem().Text;
        text.Length.ShouldBe(3000);
        text.ShouldEndWith("...");
        await adapter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task When_Slack_Refuses_The_Post_It_Returns_False_With_A_Warning_That_Does_Not_Name_The_Channel()
    {
        var (adapter, chat, _) = Build();
        chat.PostMessage(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns<PostMessageResponse>(_ => throw new SlackException(new ErrorResponse { Error = "channel_not_found" }));
        await StartedAsync(adapter);
        var logger = new RecordingLogger<SlackOriginPoster>();

        var ok = await new SlackOriginPoster(adapter, logger).PostAsync("C1:1.1", "Upload finished.", CancellationToken.None);

        ok.ShouldBeFalse();
        var warning = logger.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldNotContain("C1");
        await adapter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task An_Adapter_That_Is_Not_Running_Posts_Nothing()
    {
        var (adapter, _, posted) = Build();

        var ok = await new SlackOriginPoster(adapter, NullLogger<SlackOriginPoster>.Instance)
            .PostAsync("C1:1.1", "Upload finished.", CancellationToken.None);

        ok.ShouldBeFalse();
        posted.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("C1")]
    [InlineData("C1:")]
    [InlineData(":1.1")]
    [InlineData("C1:1.1:x")]
    public async Task A_Payload_That_Is_Not_A_Slack_Reference_Returns_False(string payload)
    {
        var (adapter, _, posted) = Build();
        await StartedAsync(adapter);

        (await new SlackOriginPoster(adapter, NullLogger<SlackOriginPoster>.Instance)
            .PostAsync(payload, "Upload finished.", CancellationToken.None)).ShouldBeFalse();
        posted.ShouldBeEmpty();
        await adapter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task With_The_Slack_Adapter_Configured_A_Slack_Reference_Reaches_The_Slack_Adapter()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{SlackServiceCollectionExtensions.ConfigSection}:BotToken"] = "xoxb-not-real",
                [$"{SlackServiceCollectionExtensions.ConfigSection}:AppLevelToken"] = "xapp-not-real",
            })
            .Build();
        var logger = new RecordingLogger<SlackOriginPoster>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ILogger<SlackOriginPoster>>(logger);
        services.AddSlackAdapter(configuration);
        services.AddSingleton<IOriginNotifier, OriginNotifier>();
        await using var provider = services.BuildServiceProvider();

        var ok = await provider.GetRequiredService<IOriginNotifier>()
            .PostAsync("slack:C1:1.1", "Upload finished.", CancellationToken.None);

        ok.ShouldBeFalse("the adapter was never started");
        logger.Entries.ShouldHaveSingleItem().Message
            .ShouldBe("The Slack adapter is not running; nothing was posted to the originating conversation.");
    }
}
