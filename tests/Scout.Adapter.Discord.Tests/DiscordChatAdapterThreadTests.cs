using Discord;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Access;

namespace Scout.Adapter.Discord.Tests;

public sealed class DiscordChatAdapterThreadTests
{
    private const ulong BotUserId = 1111;
    private const ulong ChannelId = 3333;
    private const ulong ThreadId = 7777;
    private const ulong GuildId = 9999;
    private const string Question = "why did the nightly deploy of the payments api fail on the second stage";

    private static async Task<ChatMessage> RunAsync(
        IMessageChannel channel, ulong? guildId, bool replyInThreads = true, AccessOptions? access = null)
    {
        ChatMessage? seen = null;
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var adapter = new DiscordChatAdapter(
            Options.Create(new DiscordOptions { BotToken = "not-a-real-token", ReplyInThreads = replyInThreads }),
            new DiscordMessageTranslator(),
            NullLogger<DiscordChatAdapter>.Instance,
            new AccessPolicy(access ?? new AccessOptions()));
        adapter.Arm(
            async (message, sink, ct) =>
            {
                seen = message;
                await sink.BeginAsync(ct);
                await sink.CompleteAsync("The second stage ran out of disk.", [], ct);
                answered.TrySetResult();
            },
            CancellationToken.None);

        var source = Substitute.For<IUserMessage>();
        source.Id.Returns(42UL);
        var inbound = new InboundDiscordMessage(
            MessageId: 42,
            AuthorId: 2222,
            ChannelId: channel.Id,
            Content: $"<@{BotUserId}> {Question}",
            MentionsSelf: true,
            Timestamp: new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero),
            GuildId: guildId);

        await adapter.OnInboundAsync(inbound, BotUserId, channel, source);
        await answered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await adapter.StopAsync(CancellationToken.None);
        return seen.ShouldNotBeNull();
    }

    private static Task<IUserMessage> AnyPost(IMessageChannel channel)
        => channel.Received().SendMessageAsync(
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Embed>(), Arg.Any<RequestOptions>(),
            Arg.Any<AllowedMentions>(), Arg.Any<MessageReference>(), Arg.Any<MessageComponent>(),
            Arg.Any<ISticker[]>(), Arg.Any<Embed[]>(), Arg.Any<MessageFlags>(), Arg.Any<PollProperties>());

    private static Task<IUserMessage> NoPost(IMessageChannel channel)
        => channel.DidNotReceive().SendMessageAsync(
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Embed>(), Arg.Any<RequestOptions>(),
            Arg.Any<AllowedMentions>(), Arg.Any<MessageReference>(), Arg.Any<MessageComponent>(),
            Arg.Any<ISticker[]>(), Arg.Any<Embed[]>(), Arg.Any<MessageFlags>(), Arg.Any<PollProperties>());

    private static (ITextChannel Channel, IThreadChannel Thread) GuildTextChannel()
    {
        var thread = Substitute.For<IThreadChannel>();
        thread.Id.Returns(ThreadId);
        var channel = Substitute.For<ITextChannel>();
        channel.Id.Returns(ChannelId);
        channel.CreateThreadAsync(
                Arg.Any<string>(), Arg.Any<ThreadType>(), Arg.Any<ThreadArchiveDuration>(),
                Arg.Any<IMessage>(), Arg.Any<bool?>(), Arg.Any<int?>(), Arg.Any<RequestOptions>())
            .Returns(thread);
        return (channel, thread);
    }

    [Fact]
    public async Task A_Mention_In_A_Server_Text_Channel_Opens_A_Thread_On_The_Message_And_Streams_The_Reply_There()
    {
        var (channel, thread) = GuildTextChannel();

        var message = await RunAsync(channel, GuildId);

        await channel.Received(1).CreateThreadAsync(
            "why did the nightly deploy of the payments api fail on the...",
            ThreadType.PublicThread,
            Arg.Any<ThreadArchiveDuration>(),
            Arg.Is<IMessage>(m => m.Id == 42UL),
            Arg.Any<bool?>(), Arg.Any<int?>(), Arg.Any<RequestOptions>());
        message.ChannelId.ShouldBe(ThreadId.ToString(), "memory is kept per thread");
        await AnyPost(thread);
        await NoPost(channel);
    }

    [Fact]
    public async Task When_Discord_Refuses_The_Thread_The_Reply_Goes_To_The_Channel_As_A_Reply()
    {
        var (channel, _) = GuildTextChannel();
        channel.CreateThreadAsync(
                Arg.Any<string>(), Arg.Any<ThreadType>(), Arg.Any<ThreadArchiveDuration>(),
                Arg.Any<IMessage>(), Arg.Any<bool?>(), Arg.Any<int?>(), Arg.Any<RequestOptions>())
            .Returns<IThreadChannel>(_ => throw new HttpRequestException("403 Missing Permissions"));
        var references = new List<MessageReference?>();
        channel.SendMessageAsync(default, default, default, default, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(call =>
            {
                references.Add(call.ArgAt<MessageReference?>(5));
                return Substitute.For<IUserMessage>();
            });

        var message = await RunAsync(channel, GuildId);

        message.ChannelId.ShouldBe(ChannelId.ToString());
        references.ShouldHaveSingleItem().ShouldNotBeNull().MessageId.Value.ShouldBe(42UL);
    }

    [Fact]
    public async Task A_Direct_Message_Gets_No_Thread_And_The_Reply_Stays_In_The_DM()
    {
        var dm = Substitute.For<IDMChannel>();
        dm.Id.Returns(ChannelId);

        var message = await RunAsync(dm, guildId: null);

        message.ChannelId.ShouldBe(ChannelId.ToString());
        await AnyPost(dm);
    }

    [Fact]
    public async Task A_Mention_Inside_An_Existing_Thread_Continues_In_That_Thread()
    {
        var thread = Substitute.For<IThreadChannel>();
        thread.Id.Returns(ThreadId);

        var message = await RunAsync(thread, GuildId);

        await thread.DidNotReceiveWithAnyArgs().CreateThreadAsync(default!, default, default, default, default, default, default);
        message.ChannelId.ShouldBe(ThreadId.ToString());
        await AnyPost(thread);
    }

    [Fact]
    public async Task With_Threads_Turned_Off_The_Reply_Stays_In_The_Channel()
    {
        var (channel, _) = GuildTextChannel();

        var message = await RunAsync(channel, GuildId, replyInThreads: false);

        await channel.DidNotReceiveWithAnyArgs().CreateThreadAsync(default!, default, default, default, default, default, default);
        message.ChannelId.ShouldBe(ChannelId.ToString());
        await AnyPost(channel);
    }

    [Fact]
    public async Task A_Refused_User_Gets_No_Thread_And_The_Reply_Goes_To_The_Channel_As_A_Reply()
    {
        var (channel, thread) = GuildTextChannel();
        var references = new List<MessageReference?>();
        channel.SendMessageAsync(default, default, default, default, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(call =>
            {
                references.Add(call.ArgAt<MessageReference?>(5));
                return Substitute.For<IUserMessage>();
            });

        var message = await RunAsync(channel, GuildId, access: new AccessOptions { AllowedUserIds = ["owner-1"] });

        await channel.DidNotReceiveWithAnyArgs().CreateThreadAsync(default!, default, default, default, default, default, default);
        await NoPost(thread);
        message.ChannelId.ShouldBe(ChannelId.ToString());
        references.ShouldHaveSingleItem().ShouldNotBeNull().MessageId.Value.ShouldBe(42UL);
    }

    [Fact]
    public async Task An_Allowed_User_Still_Gets_A_Thread()
    {
        var (channel, thread) = GuildTextChannel();

        var message = await RunAsync(channel, GuildId, access: new AccessOptions { AllowedUserIds = ["2222"] });

        message.ChannelId.ShouldBe(ThreadId.ToString());
        await AnyPost(thread);
        await NoPost(channel);
    }

    [Fact]
    public async Task A_Conversation_Moved_Into_A_Thread_Carries_The_Thread_As_Its_Origin()
    {
        var (channel, _) = GuildTextChannel();

        var message = await RunAsync(channel, GuildId);

        message.OriginReference.ShouldBe("discord:7777");
    }

    [Fact]
    public async Task A_Reply_That_Stays_In_The_Channel_Carries_The_Channel_And_The_Question_As_Its_Origin()
    {
        var (channel, _) = GuildTextChannel();

        var message = await RunAsync(channel, GuildId, replyInThreads: false);

        message.OriginReference.ShouldBe("discord:3333:42");
    }

    [Theory]
    [InlineData("short question", "short question")]
    [InlineData("", "Voice message")]
    [InlineData("first line\nsecond line", "first line second line")]
    public void The_Thread_Name_Is_The_Question_On_One_Line(string question, string expected)
        => DiscordChatAdapter.ThreadName(question).ShouldBe(expected);
}
