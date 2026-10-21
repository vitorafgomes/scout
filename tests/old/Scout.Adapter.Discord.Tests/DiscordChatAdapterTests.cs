using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scout.Adapter.Discord.Streaming;

namespace Scout.Adapter.Discord.Tests;

public class DiscordChatAdapterTests
{
    [Fact]
    public void Name_Is_Discord()
    {
        var adapter = BuildAdapter(botToken: "fake-token");

        adapter.Name.ShouldBe("Discord");
    }

    [Fact]
    public void Ctor_Throws_When_Options_Null()
    {
        Action create = () => new DiscordChatAdapter(
            null!,
            new DiscordMessageHandler(
                new DiscordMessageTranslator(),
                NullLogger<DiscordMessageHandler>.Instance),
            Substitute.For<IDiscordReplyPoster>(),
            NullLogger<DiscordChatAdapter>.Instance);

        create.ShouldThrow<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_Throws_When_MessageHandler_Null()
    {
        var opts = Options.Create(new DiscordOptions { BotToken = "fake-token" });

        Action create = () => new DiscordChatAdapter(
            opts,
            messageHandler: null!,
            Substitute.For<IDiscordReplyPoster>(),
            NullLogger<DiscordChatAdapter>.Instance);

        create.ShouldThrow<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_Throws_When_Poster_Null()
    {
        var opts = Options.Create(new DiscordOptions { BotToken = "fake-token" });

        Action create = () => new DiscordChatAdapter(
            opts,
            new DiscordMessageHandler(
                new DiscordMessageTranslator(),
                NullLogger<DiscordMessageHandler>.Instance),
            poster: null!,
            NullLogger<DiscordChatAdapter>.Instance);

        create.ShouldThrow<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_Throws_When_Logger_Null()
    {
        var opts = Options.Create(new DiscordOptions { BotToken = "fake-token" });

        Action create = () => new DiscordChatAdapter(
            opts,
            new DiscordMessageHandler(
                new DiscordMessageTranslator(),
                NullLogger<DiscordMessageHandler>.Instance),
            Substitute.For<IDiscordReplyPoster>(),
            logger: null!);

        create.ShouldThrow<ArgumentNullException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Ctor_Throws_When_BotToken_Missing(string? token)
    {
        var opts = Options.Create(new DiscordOptions { BotToken = token! });

        Action create = () => new DiscordChatAdapter(
            opts,
            new DiscordMessageHandler(
                new DiscordMessageTranslator(),
                NullLogger<DiscordMessageHandler>.Instance),
            Substitute.For<IDiscordReplyPoster>(),
            NullLogger<DiscordChatAdapter>.Instance);

        create.ShouldThrow<ArgumentException>()
            .Message!.ShouldContain("BotToken");
    }

    [Fact]
    public async Task StartAsync_Throws_When_Handler_Null()
    {
        var adapter = BuildAdapter(botToken: "fake-token");

        Func<Task> start = () => adapter.StartAsync(null!, CancellationToken.None);

        await start.ShouldThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task StopAsync_Is_Idempotent_Before_Start()
    {
        var adapter = BuildAdapter(botToken: "fake-token");

        Func<Task> stop = () => adapter.StopAsync(CancellationToken.None);

        await stop.ShouldNotThrowAsync();
    }

    private static DiscordChatAdapter BuildAdapter(string botToken)
    {
        var opts = Options.Create(new DiscordOptions { BotToken = botToken });
        return new DiscordChatAdapter(
            opts,
            new DiscordMessageHandler(
                new DiscordMessageTranslator(),
                NullLogger<DiscordMessageHandler>.Instance),
            Substitute.For<IDiscordReplyPoster>(),
            NullLogger<DiscordChatAdapter>.Instance);
    }
}
