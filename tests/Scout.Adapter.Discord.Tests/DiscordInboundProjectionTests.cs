namespace Scout.Adapter.Discord.Tests;

public sealed class DiscordInboundProjectionTests
{
    private static readonly DateTimeOffset FixedTime =
        new(2026, 5, 28, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ProjectInbound_Maps_All_Fields()
    {
        var inbound = DiscordChatAdapter.ProjectInbound(
            messageId: 10,
            authorId: 20,
            channelId: 30,
            content: "hello",
            mentionsSelf: true,
            timestamp: FixedTime,
            guildId: 40);

        inbound.MessageId.ShouldBe(10UL);
        inbound.AuthorId.ShouldBe(20UL);
        inbound.ChannelId.ShouldBe(30UL);
        inbound.Content.ShouldBe("hello");
        inbound.MentionsSelf.ShouldBeTrue();
        inbound.Timestamp.ShouldBe(FixedTime);
        inbound.GuildId.ShouldBe(40UL);
    }

    [Fact]
    public void ProjectInbound_MentionsSelf_False_Is_Preserved()
    {
        var inbound = DiscordChatAdapter.ProjectInbound(
            messageId: 1, authorId: 2, channelId: 3,
            content: "x", mentionsSelf: false, timestamp: FixedTime, guildId: 9);

        inbound.MentionsSelf.ShouldBeFalse();
    }

    [Fact]
    public void ProjectInbound_Null_GuildId_Models_A_DM()
    {
        var inbound = DiscordChatAdapter.ProjectInbound(
            messageId: 1, authorId: 2, channelId: 3,
            content: "x", mentionsSelf: false, timestamp: FixedTime, guildId: null);

        inbound.GuildId.ShouldBeNull();
    }

    [Fact]
    public void ProjectInbound_Null_Content_Becomes_Empty_String()
    {
        var inbound = DiscordChatAdapter.ProjectInbound(
            messageId: 1, authorId: 2, channelId: 3,
            content: null, mentionsSelf: true, timestamp: FixedTime, guildId: 9);

        inbound.Content.ShouldBe(string.Empty);
    }
}
