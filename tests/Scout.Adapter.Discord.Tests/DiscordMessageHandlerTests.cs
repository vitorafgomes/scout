using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scout.Core.Chat;

namespace Scout.Adapter.Discord.Tests;

public class DiscordMessageHandlerTests
{
    private const ulong BotUserId = 1111;
    private const ulong HumanUserId = 2222;
    private const ulong ChannelId = 3333;
    private const ulong GuildId = 4444;
    private static readonly DateTimeOffset FixedTime =
        new(2026, 4, 21, 10, 0, 0, TimeSpan.Zero);

    private static DiscordMessageHandler Sut() =>
        new(new DiscordMessageTranslator(),
            NullLogger<DiscordMessageHandler>.Instance);

    private static DiscordMessageHandler Sut(ILogger<DiscordMessageHandler> logger) =>
        new(new DiscordMessageTranslator(), logger);

    private static InboundDiscordMessage ValidInbound(string content = "<@1111> ping") =>
        new(MessageId: 1,
            AuthorId: HumanUserId,
            ChannelId: ChannelId,
            Content: content,
            MentionsSelf: true,
            Timestamp: FixedTime,
            GuildId: GuildId);

    [Fact]
    public async Task Returns_Null_When_Translator_Rejects_Message()
    {
        // Message from the bot itself - translator rejects.
        var inbound = ValidInbound() with { AuthorId = BotUserId };

        var response = await Sut().HandleAsync(
            inbound,
            BotUserId,
            handler: (_, _) => throw new InvalidOperationException("must not run"),
            CancellationToken.None);

        response.ShouldBeNull();
    }

    [Fact]
    public async Task Forwards_Translated_Message_To_Handler()
    {
        ChatMessage? captured = null;

        var response = await Sut().HandleAsync(
            ValidInbound("<@1111> status"),
            BotUserId,
            handler: (msg, _) =>
            {
                captured = msg;
                return Task.FromResult(ChatResponse.Plain("ok"));
            },
            CancellationToken.None);

        captured.ShouldNotBeNull();
        captured!.UserId.ShouldBe(HumanUserId.ToString());
        captured.ChannelId.ShouldBe(ChannelId.ToString());
        captured.Text.ShouldBe("status");
        response.ShouldNotBeNull();
        response!.IsStreaming.ShouldBeFalse();
        response.Text.ShouldBe("ok");
    }

    [Fact]
    public async Task Returns_Response_With_Citations_Untouched()
    {
        // The handler no longer renders citations into text - it returns the
        // ChatResponse as-is so the poster can format it.
        var citations = new[] { new Citation("src/Checkout.cs:10-50") };
        var response = await Sut().HandleAsync(
            ValidInbound(),
            BotUserId,
            handler: (_, _) => Task.FromResult(new ChatResponse(
                "checkout lives here",
                citations)),
            CancellationToken.None);

        response.ShouldNotBeNull();
        response!.IsStreaming.ShouldBeFalse();
        response.Text.ShouldBe("checkout lives here");
        response.Citations.Count.ShouldBe(1);
        response.Citations[0].Label.ShouldBe("src/Checkout.cs:10-50");
    }

    [Fact]
    public async Task Returns_Streaming_Response_Untouched()
    {
        async IAsyncEnumerable<string> Tokens()
        {
            await Task.Yield();
            yield return "hello ";
            yield return "world";
        }

        var streaming = ChatResponse.Streaming(Tokens());

        var response = await Sut().HandleAsync(
            ValidInbound(),
            BotUserId,
            handler: (_, _) => Task.FromResult(streaming),
            CancellationToken.None);

        response.ShouldNotBeNull();
        response!.IsStreaming.ShouldBeTrue();
        response.Stream.ShouldNotBeNull();
        response.Text.ShouldBeNull();
    }

    [Fact]
    public async Task Returns_Generic_Error_Response_When_Handler_Throws()
    {
        var response = await Sut().HandleAsync(
            ValidInbound(),
            BotUserId,
            handler: (_, _) => throw new InvalidOperationException("boom"),
            CancellationToken.None);

        response.ShouldNotBeNull();
        response!.IsStreaming.ShouldBeFalse();
        response.Text.ShouldNotBeNullOrWhiteSpace();
        response.Text!.ShouldContain("something went wrong");
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

        await act.ShouldThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Null_Handler_Throws()
    {
        Func<Task> act = () => Sut().HandleAsync(
            ValidInbound(),
            BotUserId,
            handler: null!,
            CancellationToken.None);

        await act.ShouldThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Successful_Dispatch_Logs_Inbound_Then_Dispatched_Then_Reply()
    {
        var logger = new RecordingLogger<DiscordMessageHandler>();

        await Sut(logger).HandleAsync(
            ValidInbound(),
            BotUserId,
            handler: (_, _) => Task.FromResult(ChatResponse.Plain("ok")),
            CancellationToken.None);

        var infos = logger.MessagesAt(LogLevel.Information);

        infos.Count(m => m.StartsWith("inbound:", StringComparison.Ordinal)).ShouldBe(1);
        infos.Count(m => m.StartsWith("dispatched:", StringComparison.Ordinal)).ShouldBe(1);
        infos.Count(m => m.StartsWith("reply:", StringComparison.Ordinal)).ShouldBe(1);

        // Order: inbound precedes dispatched precedes reply.
        var inboundIdx = infos.FindIndex(m => m.StartsWith("inbound:", StringComparison.Ordinal));
        var dispatchedIdx = infos.FindIndex(m => m.StartsWith("dispatched:", StringComparison.Ordinal));
        var replyIdx = infos.FindIndex(m => m.StartsWith("reply:", StringComparison.Ordinal));
        inboundIdx.ShouldBeLessThan(dispatchedIdx);
        dispatchedIdx.ShouldBeLessThan(replyIdx);
    }

    [Fact]
    public async Task Translator_Skip_Without_Mention_Logs_NoMention_Reason()
    {
        var logger = new RecordingLogger<DiscordMessageHandler>();
        var inbound = ValidInbound() with { MentionsSelf = false };

        var response = await Sut(logger).HandleAsync(
            inbound,
            BotUserId,
            handler: (_, _) => throw new InvalidOperationException("must not run"),
            CancellationToken.None);

        response.ShouldBeNull();
        var infos = logger.MessagesAt(LogLevel.Information);
        infos.ShouldContain(m => m.StartsWith("inbound:", StringComparison.Ordinal));
        infos.ShouldContain(m => m == "skipped: reason=no-mention");
    }

    [Fact]
    public async Task Translator_Skip_When_Bot_Is_Author_Logs_BotAuthor_Reason()
    {
        var logger = new RecordingLogger<DiscordMessageHandler>();
        var inbound = ValidInbound() with { AuthorId = BotUserId };

        await Sut(logger).HandleAsync(
            inbound,
            BotUserId,
            handler: (_, _) => throw new InvalidOperationException("must not run"),
            CancellationToken.None);

        logger.MessagesAt(LogLevel.Information)
            .ShouldContain(m => m == "skipped: reason=bot-author");
    }

    [Fact]
    public async Task Handler_Exception_Logs_Inbound_Before_Error()
    {
        var logger = new RecordingLogger<DiscordMessageHandler>();

        await Sut(logger).HandleAsync(
            ValidInbound(),
            BotUserId,
            handler: (_, _) => throw new InvalidOperationException("boom"),
            CancellationToken.None);

        logger.MessagesAt(LogLevel.Information)
            .ShouldContain(m => m.StartsWith("inbound:", StringComparison.Ordinal));
        logger.EntriesAt(LogLevel.Error).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Dispatched_Log_Truncates_Content_Over_80_Chars()
    {
        var logger = new RecordingLogger<DiscordMessageHandler>();
        // Translator strips the mention prefix, so the effective text has
        // to exceed 80 chars after stripping.
        var longBody = new string('a', 120);
        var content = $"<@{BotUserId}> {longBody}";

        await Sut(logger).HandleAsync(
            ValidInbound(content),
            BotUserId,
            handler: (_, _) => Task.FromResult(ChatResponse.Plain("ok")),
            CancellationToken.None);

        var dispatched = logger.MessagesAt(LogLevel.Information)
            .Single(m => m.StartsWith("dispatched:", StringComparison.Ordinal));
        dispatched.ShouldContain("...");
        dispatched.ShouldContain("truncated=True");
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public readonly List<(LogLevel Level, string Message, Exception? Exception)> Entries = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception), exception));
        }

        public List<string> MessagesAt(LogLevel level) =>
            Entries.Where(e => e.Level == level).Select(e => e.Message).ToList();

        public List<(LogLevel Level, string Message, Exception? Exception)> EntriesAt(LogLevel level) =>
            Entries.Where(e => e.Level == level).ToList();
    }
}
