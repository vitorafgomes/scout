using Microsoft.Extensions.Logging.Abstractions;
using Scout.Core.Chat;

namespace Scout.Adapter.Discord.Tests;

public class DiscordMessageHandlerTests
{
    private const ulong BotUserId = 1111;
    private const ulong HumanUserId = 2222;
    private const ulong ChannelId = 3333;
    private static readonly DateTimeOffset FixedTime =
        new(2026, 4, 21, 10, 0, 0, TimeSpan.Zero);

    private static DiscordMessageHandler Sut() =>
        new(new DiscordMessageTranslator(),
            NullLogger<DiscordMessageHandler>.Instance);

    private static InboundDiscordMessage ValidInbound(string content = "<@1111> ping") =>
        new(MessageId: 1,
            AuthorId: HumanUserId,
            ChannelId: ChannelId,
            Content: content,
            MentionsSelf: true,
            Timestamp: FixedTime);

    [Fact]
    public async Task Returns_Null_When_Translator_Rejects_Message()
    {
        // Message from the bot itself — translator rejects.
        var inbound = ValidInbound() with { AuthorId = BotUserId };

        var reply = await Sut().HandleAsync(
            inbound,
            BotUserId,
            handler: (_, _) => throw new InvalidOperationException("must not run"),
            CancellationToken.None);

        reply.Should().BeNull();
    }

    [Fact]
    public async Task Forwards_Translated_Message_To_Handler()
    {
        ChatMessage? captured = null;

        var reply = await Sut().HandleAsync(
            ValidInbound("<@1111> status"),
            BotUserId,
            handler: (msg, _) =>
            {
                captured = msg;
                return Task.FromResult(ChatResponse.Plain("ok"));
            },
            CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.UserId.Should().Be(HumanUserId.ToString());
        captured.ChannelId.Should().Be(ChannelId.ToString());
        captured.Text.Should().Be("status");
        reply.Should().Be("ok");
    }

    [Fact]
    public async Task Renders_Response_With_Citations_As_Sources_Footer()
    {
        var reply = await Sut().HandleAsync(
            ValidInbound(),
            BotUserId,
            handler: (_, _) => Task.FromResult(new ChatResponse(
                "checkout lives here",
                ["src/Checkout.cs:10-50"])),
            CancellationToken.None);

        reply.Should().Contain("checkout lives here");
        reply.Should().Contain("Sources:");
        reply.Should().Contain("- src/Checkout.cs:10-50");
    }

    [Fact]
    public async Task Returns_Generic_Error_Reply_When_Handler_Throws()
    {
        var reply = await Sut().HandleAsync(
            ValidInbound(),
            BotUserId,
            handler: (_, _) => throw new InvalidOperationException("boom"),
            CancellationToken.None);

        reply.Should().NotBeNullOrWhiteSpace();
        reply.Should().Contain("something went wrong");
    }

    [Fact]
    public async Task Propagates_Cancellation_Without_Wrapping_In_Error_Reply()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => Sut().HandleAsync(
            ValidInbound(),
            BotUserId,
            handler: (_, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(ChatResponse.Plain("unreachable"));
            },
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Null_Handler_Throws()
    {
        Func<Task> act = () => Sut().HandleAsync(
            ValidInbound(),
            BotUserId,
            handler: null!,
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
