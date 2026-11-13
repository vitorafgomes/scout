using System.Net;
using Discord;

namespace Scout.Adapter.Discord.Tests;

public sealed class DiscordVoiceMessageTests
{
    private const ulong BotUserId = 1111;
    private const string CdnUrl = "https://cdn.discordapp.com/attachments/1/2/voice-message.ogg";
    private static readonly DateTimeOffset FixedTime = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);
    private static readonly InboundVoiceMessage Voice = new(CdnUrl, "audio/ogg", 48_000, TimeSpan.FromSeconds(7.5));

    private static InboundDiscordMessage Inbound(ulong? guildId, bool mentionsSelf = false, bool repliesToSelf = false)
        => new(
            MessageId: 42, AuthorId: 2222, ChannelId: 3333, Content: string.Empty,
            MentionsSelf: mentionsSelf, Timestamp: FixedTime, GuildId: guildId,
            RepliesToSelf: repliesToSelf, Voice: Voice);

    [Fact]
    public async Task A_Voice_Message_In_A_DM_Reaches_The_Core_As_Audio_That_Downloads_From_Its_Url_Only_On_Demand()
    {
        var requested = new List<string>();
        var translator = new DiscordMessageTranslator((url, _) =>
        {
            requested.Add(url);
            return Task.FromResult<Stream>(new MemoryStream([1, 2, 3]));
        });

        translator.TryTranslate(Inbound(guildId: null), BotUserId, out var message).ShouldBeTrue();

        var audio = message.ShouldNotBeNull().Audio.ShouldNotBeNull();
        message.Text.ShouldBe(string.Empty);
        audio.ContentType.ShouldBe("audio/ogg");
        audio.SizeBytes.ShouldBe(48_000);
        audio.Duration.ShouldBe(TimeSpan.FromSeconds(7.5));
        requested.ShouldBeEmpty("nothing is downloaded before the core asks");

        await using var stream = await audio.OpenAsync(CancellationToken.None);
        requested.ShouldBe([CdnUrl]);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void A_Voice_Message_In_A_Server_Channel_Is_Accepted_When_It_Mentions_Or_Replies_To_The_Bot(
        bool mentionsSelf, bool repliesToSelf)
    {
        new DiscordMessageTranslator()
            .TryTranslate(Inbound(9999, mentionsSelf, repliesToSelf), BotUserId, out var message)
            .ShouldBeTrue();
        message.ShouldNotBeNull().Audio.ShouldNotBeNull();
    }

    [Fact]
    public void A_Voice_Message_In_A_Server_Channel_Not_Addressed_To_The_Bot_Is_Ignored()
    {
        new DiscordMessageTranslator()
            .TryTranslate(Inbound(9999), BotUserId, out var message, out var reason)
            .ShouldBeFalse();
        message.ShouldBeNull();
        reason.ShouldBe("no-mention");
    }

    [Fact]
    public void A_Text_Reply_To_The_Bot_Without_A_Mention_Is_Still_Ignored()
    {
        var inbound = Inbound(9999, repliesToSelf: true) with { Voice = null, Content = "and the staging one?" };

        new DiscordMessageTranslator().TryTranslate(inbound, BotUserId, out _).ShouldBeFalse();
    }

    [Fact]
    public void Projection_Carries_The_Voice_Attachment_And_The_Reply_To_The_Bot()
    {
        var inbound = DiscordChatAdapter.ProjectInbound(
            messageId: 1, author: Substitute.For<IUser>(), channelId: 3, content: null, mentionsSelf: false,
            timestamp: FixedTime, guildId: 9, repliesToSelf: true, voice: Voice);

        inbound.RepliesToSelf.ShouldBeTrue();
        inbound.Voice.ShouldBe(Voice);
    }

    [Fact]
    public async Task A_Download_The_Cdn_Answers_With_503_Throws_So_The_Core_Can_Say_So()
    {
        using var client = new HttpClient(new StatusHandler(HttpStatusCode.ServiceUnavailable));

        var failure = await Should.ThrowAsync<HttpRequestException>(
            () => DiscordAttachmentDownloader.OpenAsync(client, CdnUrl, CancellationToken.None));

        failure.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task A_Download_Returns_The_Attachment_Bytes()
    {
        using var client = new HttpClient(new StatusHandler(HttpStatusCode.OK, [7, 8, 9]));

        await using var stream = await DiscordAttachmentDownloader.OpenAsync(client, CdnUrl, CancellationToken.None);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);

        copy.ToArray().ShouldBe(new byte[] { 7, 8, 9 });
    }

    [Fact]
    public async Task The_Attachment_Stream_A_Caller_Disposes_Releases_The_Response_Body()
    {
        var body = new TrackingStream([7, 8, 9]);
        using var client = new HttpClient(new BodyHandler(body));

        var stream = await DiscordAttachmentDownloader.OpenAsync(client, CdnUrl, CancellationToken.None);
        body.Disposed.ShouldBeFalse();
        await stream.DisposeAsync();

        body.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task A_Non_Https_Url_Is_Never_Requested()
    {
        var handler = new StatusHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler);

        await Should.ThrowAsync<ArgumentException>(
            () => DiscordAttachmentDownloader.OpenAsync(client, "http://169.254.169.254/latest", CancellationToken.None));
        handler.Requests.ShouldBe(0);
    }

    private sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class BodyHandler(Stream body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
    }

    private sealed class StatusHandler(HttpStatusCode status, byte[]? body = null) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body ?? []) });
        }
    }
}
