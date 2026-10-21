namespace Scout.Adapter.Slack.Tests;

public sealed class SlackInboundProjectionTests
{
    private static readonly DateTimeOffset FixedTime =
        new(2026, 5, 28, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ProjectInbound_Maps_All_Fields()
    {
        var inbound = SlackChatAdapter.ProjectInbound(
            userId: "U1",
            channelId: "C1",
            text: "hello",
            timestamp: FixedTime,
            messageTs: "1700000000.000100",
            threadTs: "1700000000.000050",
            channelType: "channel",
            subtype: null,
            botId: null,
            appId: null);

        inbound.UserId.ShouldBe("U1");
        inbound.ChannelId.ShouldBe("C1");
        inbound.Text.ShouldBe("hello");
        inbound.Timestamp.ShouldBe(FixedTime);
        inbound.MessageTs.ShouldBe("1700000000.000100");
        inbound.ThreadTs.ShouldBe("1700000000.000050");
        inbound.ChannelType.ShouldBe("channel");
    }

    [Fact]
    public void ProjectInbound_Null_Text_Becomes_Empty_String()
    {
        var inbound = SlackChatAdapter.ProjectInbound(
            userId: "U1", channelId: "C1", text: null, timestamp: FixedTime,
            messageTs: "1.1", threadTs: null, channelType: "im",
            subtype: null, botId: null, appId: null);

        inbound.Text.ShouldBe(string.Empty);
    }

    [Fact]
    public void ProjectInbound_Null_ThreadTs_Models_A_Top_Level_Message()
    {
        var inbound = SlackChatAdapter.ProjectInbound(
            userId: "U1", channelId: "C1", text: "x", timestamp: FixedTime,
            messageTs: "1.1", threadTs: null, channelType: "channel",
            subtype: null, botId: null, appId: null);

        inbound.ThreadTs.ShouldBeNull();
    }

    [Fact]
    public void ProjectInbound_Preserves_Bot_And_App_Ids()
    {
        var inbound = SlackChatAdapter.ProjectInbound(
            userId: "U1", channelId: "C1", text: "x", timestamp: FixedTime,
            messageTs: "1.1", threadTs: null, channelType: "channel",
            subtype: "bot_message", botId: "B1", appId: "A1");

        inbound.BotId.ShouldBe("B1");
        inbound.AppId.ShouldBe("A1");
        inbound.Subtype.ShouldBe("bot_message");
    }
}
