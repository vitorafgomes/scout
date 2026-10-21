namespace Scout.Adapter.Discord.Tests;

public sealed class DiscordMessageTranslatorTests
{
    private const ulong BotUserId = 1111;
    private const ulong HumanUserId = 2222;
    private const ulong ChannelId = 3333;
    private static readonly DateTimeOffset FixedTime =
        new(2026, 4, 21, 10, 0, 0, TimeSpan.Zero);

    private static DiscordMessageTranslator Sut() => new();

    [Fact]
    public void TryTranslate_Rejects_Messages_From_The_Bot_Itself()
    {
        var inbound = new InboundDiscordMessage(
            MessageId: 1, AuthorId: BotUserId, ChannelId: ChannelId,
            Content: "<@1111> hi", MentionsSelf: true, Timestamp: FixedTime);

        Sut().TryTranslate(inbound, BotUserId, out var chatMessage).ShouldBeFalse();
        chatMessage.ShouldBeNull();
    }

    [Fact]
    public void TryTranslate_Rejects_Guild_Messages_Without_Mention()
    {
        var inbound = new InboundDiscordMessage(
            MessageId: 1, AuthorId: HumanUserId, ChannelId: ChannelId,
            Content: "chatting between humans", MentionsSelf: false,
            Timestamp: FixedTime, GuildId: 9999);

        Sut().TryTranslate(inbound, BotUserId, out var chatMessage).ShouldBeFalse();
        chatMessage.ShouldBeNull();
    }

    [Fact]
    public void TryTranslate_Accepts_Direct_Messages_Without_Mention()
    {
        var inbound = new InboundDiscordMessage(
            MessageId: 1, AuthorId: HumanUserId, ChannelId: ChannelId,
            Content: "hi scout", MentionsSelf: false,
            Timestamp: FixedTime, GuildId: null);

        Sut().TryTranslate(inbound, BotUserId, out var chatMessage).ShouldBeTrue();
        chatMessage.ShouldNotBeNull();
        chatMessage!.Text.ShouldBe("hi scout");
    }

    [Fact]
    public void TryTranslate_Rejects_Messages_That_Are_Only_A_Mention()
    {
        var inbound = new InboundDiscordMessage(
            MessageId: 1, AuthorId: HumanUserId, ChannelId: ChannelId,
            Content: "<@1111>", MentionsSelf: true, Timestamp: FixedTime);

        Sut().TryTranslate(inbound, BotUserId, out var chatMessage).ShouldBeFalse();
        chatMessage.ShouldBeNull();
    }

    [Fact]
    public void TryTranslate_Strips_Leading_And_Trailing_Bot_Mention()
    {
        var inbound = new InboundDiscordMessage(
            MessageId: 1, AuthorId: HumanUserId, ChannelId: ChannelId,
            Content: "<@1111> is prod azure healthy?",
            MentionsSelf: true, Timestamp: FixedTime);

        Sut().TryTranslate(inbound, BotUserId, out var chatMessage).ShouldBeTrue();
        chatMessage.ShouldNotBeNull();
        chatMessage!.Text.ShouldBe("is prod azure healthy?");
    }

    [Fact]
    public void TryTranslate_Handles_Nickname_Style_Mention_Exclamation_Mark()
    {
        var inbound = new InboundDiscordMessage(
            MessageId: 1, AuthorId: HumanUserId, ChannelId: ChannelId,
            Content: "<@!1111> status aws",
            MentionsSelf: true, Timestamp: FixedTime);

        Sut().TryTranslate(inbound, BotUserId, out var chatMessage).ShouldBeTrue();
        chatMessage!.Text.ShouldBe("status aws");
    }

    [Fact]
    public void TryTranslate_Populates_User_And_Channel_And_Timestamp()
    {
        var inbound = new InboundDiscordMessage(
            MessageId: 1, AuthorId: HumanUserId, ChannelId: ChannelId,
            Content: "<@1111> ping",
            MentionsSelf: true, Timestamp: FixedTime);

        Sut().TryTranslate(inbound, BotUserId, out var chatMessage).ShouldBeTrue();
        chatMessage!.UserId.ShouldBe(HumanUserId.ToString());
        chatMessage.ChannelId.ShouldBe(ChannelId.ToString());
        chatMessage.Timestamp.ShouldBe(FixedTime);
    }

    [Fact]
    public void TryTranslate_Direct_Message_Sets_IsDirectMessage_True_And_AdapterName()
    {
        var inbound = new InboundDiscordMessage(
            MessageId: 1, AuthorId: HumanUserId, ChannelId: ChannelId,
            Content: "hi scout", MentionsSelf: false,
            Timestamp: FixedTime, GuildId: null);

        Sut().TryTranslate(inbound, BotUserId, out var chatMessage).ShouldBeTrue();
        chatMessage!.IsDirectMessage.ShouldBeTrue();
        chatMessage.AdapterName.ShouldBe("Discord");
    }

    [Fact]
    public void TryTranslate_Guild_Message_Sets_IsDirectMessage_False()
    {
        var inbound = new InboundDiscordMessage(
            MessageId: 1, AuthorId: HumanUserId, ChannelId: ChannelId,
            Content: "<@1111> status", MentionsSelf: true,
            Timestamp: FixedTime, GuildId: 9999);

        Sut().TryTranslate(inbound, BotUserId, out var chatMessage).ShouldBeTrue();
        chatMessage!.IsDirectMessage.ShouldBeFalse();
        chatMessage.AdapterName.ShouldBe("Discord");
    }
}
