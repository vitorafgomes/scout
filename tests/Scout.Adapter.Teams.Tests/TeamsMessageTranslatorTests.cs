using Microsoft.Bot.Schema;
using Scout.Adapter.Teams;

namespace Scout.Adapter.Teams.Tests;

public sealed class TeamsMessageTranslatorTests
{
    private const string BotId = "28:bot-app-id";
    private const string BotName = "Scout";
    private const string HumanId = "29:human";
    private const string ConversationId = "a:1:1";
    private static readonly DateTimeOffset FixedTime =
        new(2026, 5, 28, 10, 0, 0, TimeSpan.Zero);

    private static TeamsMessageTranslator Sut() => new();

    private static InboundTeamsMessage Inbound(
        string text,
        bool isGroup = false,
        string fromId = HumanId,
        string activityType = ActivityTypes.Message,
        string? recipientMention = BotName) =>
        new(
            ActivityId: "act-1",
            ActivityType: activityType,
            FromId: fromId,
            ConversationId: ConversationId,
            Text: text,
            IsGroup: isGroup,
            RecipientId: BotId,
            RecipientMention: recipientMention,
            Timestamp: FixedTime);

    [Fact]
    public void Rejects_Non_Message_Activities()
    {
        Sut().TryTranslate(Inbound("hi", activityType: ActivityTypes.Typing), out var msg)
            .ShouldBeFalse();
        msg.ShouldBeNull();
    }

    [Fact]
    public void Rejects_Bot_Own_Message()
    {
        Sut().TryTranslate(Inbound("hi", fromId: BotId), out var msg).ShouldBeFalse();
        msg.ShouldBeNull();
    }

    [Fact]
    public void Accepts_OneToOne_Without_Mention()
    {
        Sut().TryTranslate(
            Inbound("is prod healthy?", isGroup: false, recipientMention: BotName),
            out var msg).ShouldBeTrue();
        msg.ShouldNotBeNull();
        msg!.Text.ShouldBe("is prod healthy?");
        msg.UserId.ShouldBe(HumanId);
        msg.ChannelId.ShouldBe(ConversationId);
        msg.Timestamp.ShouldBe(FixedTime);
    }

    [Fact]
    public void Rejects_Group_Message_Without_Mention()
    {
        Sut().TryTranslate(
            Inbound("chatting between humans", isGroup: true),
            out var msg).ShouldBeFalse();
        msg.ShouldBeNull();
    }

    [Fact]
    public void Accepts_Group_Message_With_Bot_Mention_And_Strips_It()
    {
        Sut().TryTranslate(
            Inbound($"<at>{BotName}</at> status aws", isGroup: true),
            out var msg).ShouldBeTrue();
        msg.ShouldNotBeNull();
        msg!.Text.ShouldBe("status aws");
    }

    [Fact]
    public void Strips_Only_Bot_Mention_Keeps_Other_Mentions()
    {
        Sut().TryTranslate(
            Inbound($"<at>{BotName}</at> ping <at>Alice</at>", isGroup: true),
            out var msg).ShouldBeTrue();
        msg!.Text.ShouldBe("ping <at>Alice</at>");
    }

    [Fact]
    public void Rejects_Message_That_Is_Only_The_Mention()
    {
        Sut().TryTranslate(
            Inbound($"<at>{BotName}</at>", isGroup: true),
            out var msg).ShouldBeFalse();
        msg.ShouldBeNull();
    }

    [Fact]
    public void Rejects_Empty_Text()
    {
        Sut().TryTranslate(Inbound("   "), out var msg).ShouldBeFalse();
        msg.ShouldBeNull();
    }

    [Fact]
    public void OneToOne_Sets_IsDirectMessage_True_And_AdapterName()
    {
        Sut().TryTranslate(
            Inbound("is prod healthy?", isGroup: false),
            out var msg).ShouldBeTrue();
        msg!.IsDirectMessage.ShouldBeTrue();
        msg.AdapterName.ShouldBe("Teams");
    }

    [Fact]
    public void Group_Sets_IsDirectMessage_False_And_AdapterName()
    {
        Sut().TryTranslate(
            Inbound($"<at>{BotName}</at> status aws", isGroup: true),
            out var msg).ShouldBeTrue();
        msg!.IsDirectMessage.ShouldBeFalse();
        msg.AdapterName.ShouldBe("Teams");
    }

    [Fact]
    public void ProjectInbound_Maps_Activity_Fields()
    {
        var activity = new Activity
        {
            Id = "act-9",
            Type = ActivityTypes.Message,
            Text = "hello",
            Timestamp = FixedTime,
            From = new ChannelAccount(id: HumanId, name: "Human"),
            Recipient = new ChannelAccount(id: BotId, name: BotName),
            Conversation = new ConversationAccount(id: ConversationId, conversationType: "personal"),
        };

        var inbound = TeamsMessageTranslator.ProjectInbound(activity);

        inbound.ActivityId.ShouldBe("act-9");
        inbound.ActivityType.ShouldBe(ActivityTypes.Message);
        inbound.FromId.ShouldBe(HumanId);
        inbound.RecipientId.ShouldBe(BotId);
        inbound.RecipientMention.ShouldBe(BotName);
        inbound.ConversationId.ShouldBe(ConversationId);
        inbound.Text.ShouldBe("hello");
        inbound.IsGroup.ShouldBeFalse();
        inbound.Timestamp.ShouldBe(FixedTime);
    }

    [Fact]
    public void ProjectInbound_GroupChat_ConversationType_Marks_Group()
    {
        var activity = new Activity
        {
            Type = ActivityTypes.Message,
            Text = "hi",
            From = new ChannelAccount(id: HumanId),
            Recipient = new ChannelAccount(id: BotId, name: BotName),
            Conversation = new ConversationAccount(id: ConversationId, conversationType: "groupChat"),
        };

        TeamsMessageTranslator.ProjectInbound(activity).IsGroup.ShouldBeTrue();
    }

    [Fact]
    public void ProjectInbound_IsGroup_Flag_Marks_Group()
    {
        var activity = new Activity
        {
            Type = ActivityTypes.Message,
            Text = "hi",
            From = new ChannelAccount(id: HumanId),
            Recipient = new ChannelAccount(id: BotId, name: BotName),
            Conversation = new ConversationAccount(isGroup: true, id: ConversationId),
        };

        TeamsMessageTranslator.ProjectInbound(activity).IsGroup.ShouldBeTrue();
    }
}
