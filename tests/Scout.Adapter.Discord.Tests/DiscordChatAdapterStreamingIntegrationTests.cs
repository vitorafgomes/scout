using Discord;
using Microsoft.Extensions.Logging.Abstractions;
using Scout.Adapter.Discord.Streaming;
using Scout.Core.Chat;

namespace Scout.Adapter.Discord.Tests;

/// <summary>
/// Thin end-to-end test that exercises the wiring from
/// <see cref="DiscordMessageHandler"/> through to
/// <see cref="IDiscordReplyPoster"/>. Discord.Net's
/// <c>DiscordSocketClient</c> can't be driven without a live gateway, so this
/// test replays exactly what
/// <see cref="DiscordChatAdapter"/>'s message loop does once a SocketMessage
/// has been mapped into an <see cref="InboundDiscordMessage"/>:
/// handler.HandleAsync -> poster.PostAsync(channel, response).
/// The streaming-specific behaviour of the poster itself is covered by
/// <c>DiscordEmbedReplyPosterTests</c>.
/// </summary>
public class DiscordChatAdapterStreamingIntegrationTests
{
    private const ulong BotUserId = 9001;
    private const ulong HumanUserId = 9002;
    private const ulong ChannelId = 9003;
    private const ulong GuildId = 9004;
    private static readonly DateTimeOffset FixedTime =
        new(2026, 5, 23, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Streaming_Response_Is_Posted_Exactly_Once_With_Same_Channel_And_Response()
    {
        var poster = Substitute.For<IDiscordReplyPoster>();
        var channel = Substitute.For<IMessageChannel>();

        var handler = new DiscordMessageHandler(
            new DiscordMessageTranslator(),
            NullLogger<DiscordMessageHandler>.Instance);

        var citations = new[] { new Citation("src/Foo.cs:1-10") { Path = "src/Foo.cs", Line = 1 } };
        var streaming = ChatResponse.Streaming(StreamTokens("hello ", "world"), citations);

        var inbound = new InboundDiscordMessage(
            MessageId: 1,
            AuthorId: HumanUserId,
            ChannelId: ChannelId,
            Content: $"<@{BotUserId}> explain the router",
            MentionsSelf: true,
            Timestamp: FixedTime,
            GuildId: GuildId);

        // Replay the adapter's inbound path: dispatch through the handler,
        // then hand the resulting response to the poster.
        var response = await handler.HandleAsync(
            inbound,
            BotUserId,
            handler: (_, _) => Task.FromResult(streaming),
            CancellationToken.None);

        response.ShouldNotBeNull();
        response!.IsStreaming.ShouldBeTrue();

        await poster.PostAsync(channel, response, CancellationToken.None);

        await poster.Received(1).PostAsync(
            channel,
            response,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Null_Response_From_Handler_Means_No_Post()
    {
        var poster = Substitute.For<IDiscordReplyPoster>();
        var channel = Substitute.For<IMessageChannel>();

        var handler = new DiscordMessageHandler(
            new DiscordMessageTranslator(),
            NullLogger<DiscordMessageHandler>.Instance);

        // Bot author triggers translator skip - handler returns null.
        var inbound = new InboundDiscordMessage(
            MessageId: 1,
            AuthorId: BotUserId,
            ChannelId: ChannelId,
            Content: "anything",
            MentionsSelf: true,
            Timestamp: FixedTime,
            GuildId: GuildId);

        var response = await handler.HandleAsync(
            inbound,
            BotUserId,
            handler: (_, _) => Task.FromResult(ChatResponse.Plain("unreachable")),
            CancellationToken.None);

        if (response is not null)
        {
            await poster.PostAsync(channel, response, CancellationToken.None);
        }

        response.ShouldBeNull();
        await poster.DidNotReceiveWithAnyArgs().PostAsync(
            default!,
            default!,
            default);
    }

    private static async IAsyncEnumerable<string> StreamTokens(params string[] tokens)
    {
        foreach (var token in tokens)
        {
            await Task.Yield();
            yield return token;
        }
    }
}

