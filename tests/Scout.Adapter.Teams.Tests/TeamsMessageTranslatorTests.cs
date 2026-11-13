using Microsoft.Bot.Schema;

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

    private static Entity Received(Mention mention)
    {
        var entity = new Entity();
        entity.SetAs(mention);
        return entity;
    }

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
        msg.IsLocalOwner.ShouldBeFalse("only the local adapter marks the local owner");
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_Group_Message_Mentioning_Someone_Else_Is_Not_For_The_Bot_When_Its_Display_Name_Is_Absent(string? recipientMention)
    {
        var inbound = Inbound("<at>Alice</at> can you check this?", isGroup: true, recipientMention: recipientMention) with { MentionedIds = ["29:alice"] };

        Sut().TryTranslate(inbound, out var msg).ShouldBeFalse();
        msg.ShouldBeNull();
    }

    [Fact]
    public void A_Group_Message_Mentioning_The_Bot_By_Its_Id_Is_Accepted_When_Its_Display_Name_Is_Absent()
    {
        var inbound = Inbound("<at>Scout</at> is the build green?", isGroup: true, recipientMention: null) with { MentionedIds = ["29:alice", BotId] };

        Sut().TryTranslate(inbound, out var msg).ShouldBeTrue();
        msg!.Text.ShouldBe("is the build green?");
    }

    [Fact]
    public void A_Mention_Accepted_By_Id_Is_Stripped_By_Its_Own_Tag_When_The_Tag_Differs_From_The_Bots_Display_Name()
    {
        var inbound = Inbound("<at>Scout Bot</at> is the build green? <at>Alice</at>", isGroup: true, recipientMention: BotName) with
        {
            MentionedIds = [BotId, "29:alice"],
            RecipientMentionTexts = ["<at>Scout Bot</at>"],
        };

        Sut().TryTranslate(inbound, out var msg).ShouldBeTrue();
        msg!.Text.ShouldBe("is the build green? <at>Alice</at>");
    }

    [Fact]
    public void A_Mention_Entity_Of_The_Bot_That_Carries_No_Tag_Text_Never_Fails_And_The_Name_Tag_Is_Still_Stripped()
    {
        var inbound = Inbound($"<at>{BotName}</at> is the build green?", isGroup: true, recipientMention: BotName) with
        {
            MentionedIds = [BotId],
            RecipientMentionTexts = [string.Empty],
        };

        Sut().TryTranslate(inbound, out var msg).ShouldBeTrue();
        msg!.Text.ShouldBe("is the build green?");
    }

    [Fact]
    public void ProjectInbound_Maps_The_Ids_Of_The_Mention_Entities()
    {
        var activity = new Activity
        {
            Type = ActivityTypes.Message,
            Text = "<at>Scout</at> <at>Alice</at> hi",
            From = new ChannelAccount(id: HumanId),
            Recipient = new ChannelAccount(id: BotId),
            Conversation = new ConversationAccount(isGroup: true, id: ConversationId),
            Entities =
            [
                Received(new Mention(new ChannelAccount(id: BotId, name: BotName), "<at>Scout</at>")),
                Received(new Mention(new ChannelAccount(id: "29:alice", name: "Alice"), "<at>Alice</at>")),
                new Entity("clientInfo"),
            ],
        };

        TeamsMessageTranslator.ProjectInbound(activity).MentionedIds.ShouldBe([BotId, "29:alice"]);
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
