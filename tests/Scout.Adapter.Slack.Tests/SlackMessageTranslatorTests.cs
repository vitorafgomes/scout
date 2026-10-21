namespace Scout.Adapter.Slack.Tests;

public sealed class SlackMessageTranslatorTests
{
    private const string BotUserId = "UBOT";
    private const string HumanUserId = "UHUMAN";
    private const string ChannelId = "C123";
    private static readonly DateTimeOffset FixedTime =
        new(2026, 4, 21, 10, 0, 0, TimeSpan.Zero);

    private static SlackMessageTranslator Sut() => new();

    private static InboundSlackMessage Channel(
        string text, string? subtype = null, string? botId = null, string? appId = null)
        => new(
            UserId: HumanUserId, ChannelId: ChannelId, Text: text, Timestamp: FixedTime,
            MessageTs: "1700000000.000100", ThreadTs: null, ChannelType: "channel",
            Subtype: subtype, BotId: botId, AppId: appId);

    private static InboundSlackMessage Dm(string text)
        => new(
            UserId: HumanUserId, ChannelId: ChannelId, Text: text, Timestamp: FixedTime,
            MessageTs: "1700000000.000100", ThreadTs: null, ChannelType: "im");

    [Fact]
    public void TryTranslate_Rejects_Messages_From_The_Bot_Itself()
    {
        var inbound = Channel("<@UBOT> hi") with { UserId = BotUserId };

        Sut().TryTranslate(inbound, BotUserId, out var chatMessage).ShouldBeFalse();
        chatMessage.ShouldBeNull();
    }

    [Fact]
    public void TryTranslate_Rejects_Messages_With_BotId_Other_Bots()
    {
        var inbound = Channel("<@UBOT> hi", botId: "B999");

        Sut().TryTranslate(inbound, BotUserId, out var chatMessage, out var reason).ShouldBeFalse();
        chatMessage.ShouldBeNull();
        reason.ShouldBe("bot-author");
    }

    [Fact]
    public void TryTranslate_Rejects_Bot_Message_Subtype()
    {
        var inbound = Channel("<@UBOT> hi", subtype: "bot_message");

        Sut().TryTranslate(inbound, BotUserId, out var chatMessage).ShouldBeFalse();
        chatMessage.ShouldBeNull();
    }

    [Fact]
    public void TryTranslate_Rejects_Non_Bot_Subtypes_Like_Channel_Join()
    {
        var inbound = Channel("<@UBOT> joined", subtype: "channel_join");

        Sut().TryTranslate(inbound, BotUserId, out var chatMessage, out var reason).ShouldBeFalse();
        reason.ShouldBe("subtype");
    }

    [Fact]
    public void TryTranslate_Rejects_Channel_Messages_Without_Mention()
    {
        var inbound = Channel("chatting between humans");

        Sut().TryTranslate(inbound, BotUserId, out var chatMessage, out var reason).ShouldBeFalse();
        chatMessage.ShouldBeNull();
        reason.ShouldBe("no-mention");
    }

    [Fact]
    public void TryTranslate_Accepts_Direct_Messages_Without_Mention()
    {
        var inbound = Dm("hi scout");

        Sut().TryTranslate(inbound, BotUserId, out var chatMessage).ShouldBeTrue();
        chatMessage.ShouldNotBeNull();
        chatMessage!.Text.ShouldBe("hi scout");
    }

    [Fact]
    public void TryTranslate_Rejects_Messages_That_Are_Only_A_Mention()
    {
        var inbound = Channel("<@UBOT>");

        Sut().TryTranslate(inbound, BotUserId, out var chatMessage, out var reason).ShouldBeFalse();
        chatMessage.ShouldBeNull();
        reason.ShouldBe("empty-after-strip");
    }

    [Fact]
    public void TryTranslate_Strips_Leading_Bot_Mention()
    {
        var inbound = Channel("<@UBOT> is prod azure healthy?");

        Sut().TryTranslate(inbound, BotUserId, out var chatMessage).ShouldBeTrue();
        chatMessage.ShouldNotBeNull();
        chatMessage!.Text.ShouldBe("is prod azure healthy?");
    }

    [Fact]
    public void TryTranslate_Handles_Mention_With_Display_Name_Pipe_Form()
    {
        var inbound = Channel("<@UBOT|scout> status aws");

        Sut().TryTranslate(inbound, BotUserId, out var chatMessage).ShouldBeTrue();
        chatMessage!.Text.ShouldBe("status aws");
    }

    [Fact]
    public void TryTranslate_Keeps_Mentions_Of_Other_Users()
    {
        var inbound = Channel("<@UBOT> ping <@UOTHER>");

        Sut().TryTranslate(inbound, BotUserId, out var chatMessage).ShouldBeTrue();
        chatMessage!.Text.ShouldBe("ping <@UOTHER>");
    }

    [Fact]
    public void TryTranslate_Populates_User_And_Channel_And_Timestamp()
    {
        var inbound = Channel("<@UBOT> ping");

        Sut().TryTranslate(inbound, BotUserId, out var chatMessage).ShouldBeTrue();
        chatMessage!.UserId.ShouldBe(HumanUserId);
        chatMessage.ChannelId.ShouldBe(ChannelId);
        chatMessage.Timestamp.ShouldBe(FixedTime);
    }

    [Fact]
    public void TryTranslate_Direct_Message_Sets_IsDirectMessage_True_And_AdapterName()
    {
        var inbound = Dm("hi scout");

        Sut().TryTranslate(inbound, BotUserId, out var chatMessage).ShouldBeTrue();
        chatMessage!.IsDirectMessage.ShouldBeTrue();
        chatMessage.AdapterName.ShouldBe("Slack");
    }

    [Fact]
    public void TryTranslate_Channel_Message_Sets_IsDirectMessage_False()
    {
        var inbound = Channel("<@UBOT> status");

        Sut().TryTranslate(inbound, BotUserId, out var chatMessage).ShouldBeTrue();
        chatMessage!.IsDirectMessage.ShouldBeFalse();
        chatMessage.AdapterName.ShouldBe("Slack");
    }
}
