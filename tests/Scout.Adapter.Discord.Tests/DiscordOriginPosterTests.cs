using System.Net;
using Discord;
using Discord.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scout.Conversation;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Access;

namespace Scout.Adapter.Discord.Tests;

public sealed class DiscordOriginPosterTests
{
    private const ulong ChannelId = 3333;
    private const ulong ThreadId = 7777;

    private sealed record Sent(string Text, AllowedMentions? Mentions, MessageReference? ReplyTo);

    private static (IMessageChannel Channel, List<Sent> Sent) Channel(ulong id)
    {
        var sent = new List<Sent>();
        var channel = Substitute.For<IMessageChannel>();
        channel.Id.Returns(id);
        channel.SendMessageAsync(default, default, default, default, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(call =>
            {
                sent.Add(new Sent(call.ArgAt<string>(0), call.ArgAt<AllowedMentions?>(4), call.ArgAt<MessageReference?>(5)));
                return Substitute.For<IUserMessage>();
            });
        return (channel, sent);
    }

    private static DiscordOriginPoster Poster(
        Dictionary<ulong, IMessageChannel> channels, RecordingLogger<DiscordOriginPoster>? logger = null)
        => new(
            (id, _) => Task.FromResult(channels.TryGetValue(id, out var channel) ? channel : null),
            (ILogger?)logger ?? NullLogger.Instance);

    [Fact]
    public async Task A_Channel_Reference_Posts_Plain_Text_Into_That_Channel_As_A_Reply_To_The_Question()
    {
        var (channel, sent) = Channel(ChannelId);

        var posted = await Poster(new() { [ChannelId] = channel }).PostAsync("3333:42", "Script ready for review.", CancellationToken.None);

        posted.ShouldBeTrue();
        var message = sent.ShouldHaveSingleItem();
        message.Text.ShouldBe("Script ready for review.");
        message.ReplyTo.ShouldNotBeNull().MessageId.Value.ShouldBe(42UL);
        message.ReplyTo.FailIfNotExists.Value.ShouldBeFalse("a deleted question must not stop the post");
        message.Mentions.ShouldBe(AllowedMentions.None, "a progress post never pings anyone");
    }

    [Fact]
    public async Task A_Thread_Reference_Posts_Into_That_Thread_Without_A_Reply()
    {
        var (thread, sent) = Channel(ThreadId);
        var (channel, inChannel) = Channel(ChannelId);

        var posted = await Poster(new() { [ThreadId] = thread, [ChannelId] = channel }).PostAsync("7777", "Upload finished.", CancellationToken.None);

        posted.ShouldBeTrue();
        sent.ShouldHaveSingleItem().ReplyTo.ShouldBeNull();
        inChannel.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Text_Over_2000_Characters_Is_Capped_To_2000()
    {
        var (channel, sent) = Channel(ChannelId);

        await Poster(new() { [ChannelId] = channel }).PostAsync("3333:42", new string('a', 2500), CancellationToken.None);

        var text = sent.ShouldHaveSingleItem().Text;
        text.Length.ShouldBe(2000);
        text.ShouldEndWith("...");
    }

    [Fact]
    public async Task A_Missing_Channel_Returns_False_With_A_Warning_That_Does_Not_Name_It()
    {
        var logger = new RecordingLogger<DiscordOriginPoster>();

        var posted = await Poster([], logger).PostAsync("3333:42", "Upload finished.", CancellationToken.None);

        posted.ShouldBeFalse();
        var warning = logger.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldNotContain("3333");
    }

    [Fact]
    public async Task When_Discord_Refuses_The_Post_It_Returns_False_With_A_Warning()
    {
        var logger = new RecordingLogger<DiscordOriginPoster>();
        var channel = Substitute.For<IMessageChannel>();
        channel.SendMessageAsync(default, default, default, default, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs<IUserMessage>(_ => throw new HttpException(
                HttpStatusCode.NotFound, Substitute.For<IRequest>(), DiscordErrorCode.UnknownChannel));

        var posted = await Poster(new() { [ChannelId] = channel }, logger).PostAsync("3333:42", "Upload finished.", CancellationToken.None);

        posted.ShouldBeFalse();
        var warning = logger.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldNotContain("3333");
    }

    [Fact]
    public async Task When_Discord_Refuses_The_Channel_Lookup_It_Returns_False_With_A_Warning()
    {
        var logger = new RecordingLogger<DiscordOriginPoster>();
        var poster = new DiscordOriginPoster(
            (_, _) => throw new HttpException(HttpStatusCode.Forbidden, Substitute.For<IRequest>(), DiscordErrorCode.MissingPermissions),
            logger);

        var posted = await poster.PostAsync("3333:42", "Upload finished.", CancellationToken.None);

        posted.ShouldBeFalse();
        var warning = logger.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldNotContain("3333");
    }

    [Theory]
    [InlineData("")]
    [InlineData("general")]
    [InlineData("3333:42:1")]
    [InlineData("3333:x")]
    public async Task A_Payload_That_Is_Not_A_Discord_Reference_Returns_False(string payload)
    {
        var (channel, sent) = Channel(ChannelId);

        (await Poster(new() { [ChannelId] = channel }).PostAsync(payload, "Upload finished.", CancellationToken.None))
            .ShouldBeFalse();
        sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_Adapter_That_Is_Not_Running_Resolves_No_Channel_So_Nothing_Is_Posted()
    {
        await using var adapter = new DiscordChatAdapter(
            Options.Create(new DiscordOptions { BotToken = "not-a-real-token" }),
            new DiscordMessageTranslator(),
            NullLogger<DiscordChatAdapter>.Instance,
            new AccessPolicy(new AccessOptions()));

        var posted = await new DiscordOriginPoster(adapter, NullLogger<DiscordOriginPoster>.Instance)
            .PostAsync("3333:42", "Upload finished.", CancellationToken.None);

        posted.ShouldBeFalse();
    }

    [Fact]
    public async Task With_The_Discord_Adapter_Configured_A_Discord_Reference_Reaches_The_Discord_Adapter()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{DiscordServiceCollectionExtensions.ConfigSection}:BotToken"] = "not-a-real-token",
            })
            .Build();
        var services = new ServiceCollection();
        var logger = new RecordingLogger<DiscordOriginPoster>();
        services.AddLogging();
        services.AddSingleton<ILogger<DiscordOriginPoster>>(logger);
        services.AddDiscordAdapter(configuration);
        services.AddSingleton<IOriginNotifier, OriginNotifier>();
        await using var provider = services.BuildServiceProvider();

        var posted = await provider.GetRequiredService<IOriginNotifier>()
            .PostAsync("discord:3333:42", "Upload finished.", CancellationToken.None);

        posted.ShouldBeFalse("the adapter was never started");
        logger.Entries.ShouldHaveSingleItem().Message
            .ShouldBe("The originating Discord conversation is not reachable; nothing was posted.");
    }
}
