using Microsoft.Extensions.Options;
using Scout.Core.Chat;

namespace Scout.Adapter.Discord.Tests;

public class DiscordChatAdapterTests
{
    [Fact]
    public void Name_Is_Discord()
    {
        var adapter = BuildAdapter(botToken: "fake-token");

        adapter.Name.Should().Be("Discord");
    }

    [Fact]
    public void Ctor_Throws_When_Options_Null()
    {
        Action create = () => new DiscordChatAdapter(null!);

        create.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Ctor_Throws_When_BotToken_Missing(string? token)
    {
        var opts = Options.Create(new DiscordOptions { BotToken = token! });

        Action create = () => new DiscordChatAdapter(opts);

        create.Should().Throw<ArgumentException>()
            .WithMessage("*BotToken*");
    }

    [Fact]
    public async Task StartAsync_Throws_When_Handler_Null()
    {
        var adapter = BuildAdapter(botToken: "fake-token");

        Func<Task> start = () => adapter.StartAsync(null!, CancellationToken.None);

        await start.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task StartAsync_And_StopAsync_Complete_Without_Throwing_In_Scaffold()
    {
        var adapter = BuildAdapter(botToken: "fake-token");
        Func<ChatMessage, CancellationToken, Task<ChatResponse>> handler =
            (_, _) => Task.FromResult(ChatResponse.Plain("ok"));

        Func<Task> start = () => adapter.StartAsync(handler, CancellationToken.None);
        Func<Task> stop = () => adapter.StopAsync(CancellationToken.None);

        await start.Should().NotThrowAsync();
        await stop.Should().NotThrowAsync();
    }

    private static DiscordChatAdapter BuildAdapter(string botToken)
    {
        var opts = Options.Create(new DiscordOptions { BotToken = botToken });
        return new DiscordChatAdapter(opts);
    }
}
