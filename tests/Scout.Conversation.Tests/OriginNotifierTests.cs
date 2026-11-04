using Microsoft.Extensions.Logging;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Tests.Fakes;

namespace Scout.Conversation.Tests;

public sealed class OriginNotifierTests
{
    private sealed class RecordingPoster(string kind, Func<string, Task<bool>>? post = null) : IOriginPoster
    {
        public List<(string Payload, string Text)> Posts { get; } = [];

        public string Kind => kind;

        public Task<bool> PostAsync(string payload, string text, CancellationToken ct)
        {
            Posts.Add((payload, text));
            return post?.Invoke(payload) ?? Task.FromResult(true);
        }
    }

    [Fact]
    public async Task A_Reference_Goes_To_The_Poster_Of_Its_Adapter_With_The_Payload_After_The_Prefix()
    {
        var discord = new RecordingPoster("discord");
        var slack = new RecordingPoster("slack");
        var notifier = new OriginNotifier([discord, slack]);

        var posted = await notifier.PostAsync("slack:C1:1700000000.000100", "Script ready for review.", CancellationToken.None);

        posted.ShouldBeTrue();
        slack.Posts.ShouldBe([("C1:1700000000.000100", "Script ready for review.")]);
        discord.Posts.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("teams:19:abc@thread.skype")]
    [InlineData("whatsapp:123")]
    [InlineData("discord")]
    [InlineData(":3333")]
    [InlineData("")]
    [InlineData(null)]
    public async Task A_Reference_No_Running_Adapter_Can_Resolve_Returns_False(string? reference)
    {
        var discord = new RecordingPoster("discord");
        var logger = new RecordingLogger<OriginNotifier>();

        var posted = await new OriginNotifier([discord], logger).PostAsync(reference!, "Upload finished.", CancellationToken.None);

        posted.ShouldBeFalse();
        discord.Posts.ShouldBeEmpty();
        logger.Entries.ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Warning);
    }

    [Fact]
    public async Task A_Poster_That_Throws_Returns_False_With_A_Warning_Instead_Of_Throwing()
    {
        var logger = new RecordingLogger<OriginNotifier>();
        var failing = new RecordingPoster("discord", _ => throw new HttpRequestException("connection reset"));

        var posted = await new OriginNotifier([failing], logger).PostAsync("discord:3333:42", "Upload finished.", CancellationToken.None);

        posted.ShouldBeFalse();
        var warning = logger.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldNotContain("3333");
    }

    [Fact]
    public async Task A_Poster_That_Refuses_Returns_False_And_Leaves_The_Warning_To_The_Poster()
    {
        var refusing = new RecordingPoster("local", _ => Task.FromResult(false));
        var logger = new RecordingLogger<OriginNotifier>();

        var posted = await new OriginNotifier([refusing], logger).PostAsync("local:session", "Upload finished.", CancellationToken.None);

        posted.ShouldBeFalse();
        refusing.Posts.ShouldBe([("session", "Upload finished.")]);
        logger.Entries.ShouldBeEmpty("the poster already logged why it refused");
    }
}
