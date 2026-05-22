using Discord;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Scout.Adapter.Discord.Streaming;
using Scout.Core.Chat;

namespace Scout.Adapter.Discord.Tests.Streaming;

public class DiscordEmbedReplyPosterTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(750);
    private const string Cursor = "▌";

    private static DiscordEmbedReplyPoster CreateSut(out FakeTimeProvider time)
    {
        time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 23, 0, 0, 0, TimeSpan.Zero));
        return new DiscordEmbedReplyPoster(time, NullLogger<DiscordEmbedReplyPoster>.Instance);
    }

    private static IMessageChannel CreateChannel(out IUserMessage posted, out List<string> editedDescriptions)
    {
        var channel = Substitute.For<IMessageChannel>();
        posted = Substitute.For<IUserMessage>();

        var captured = new List<string>();

        // Return the substituted IUserMessage from SendMessageAsync(...). The
        // signature is rich, so use Any-matchers on all parameters.
        channel.SendMessageAsync(
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<Embed>(),
                Arg.Any<RequestOptions>(),
                Arg.Any<AllowedMentions>(),
                Arg.Any<MessageReference>(),
                Arg.Any<MessageComponent>(),
                Arg.Any<ISticker[]>(),
                Arg.Any<Embed[]>(),
                Arg.Any<MessageFlags>(),
                Arg.Any<PollProperties>())
            .Returns(Task.FromResult(posted));

        // Capture every ModifyAsync(Action<MessageProperties>) invocation,
        // apply the lambda to a fresh MessageProperties, and record the
        // resulting embed description.
        posted.ModifyAsync(Arg.Do<Action<MessageProperties>>(action =>
        {
            var props = new MessageProperties();
            action(props);
            if (props.Embed.IsSpecified && props.Embed.Value is { } embed)
            {
                captured.Add(embed.Description ?? string.Empty);
            }
        }), Arg.Any<RequestOptions>()).Returns(Task.CompletedTask);

        editedDescriptions = captured;
        return channel;
    }

    private static async IAsyncEnumerable<string> Stream(params string[] chunks)
    {
        foreach (var c in chunks)
        {
            await Task.Yield();
            yield return c;
        }
    }

    private static async IAsyncEnumerable<string> StreamThenThrow(params string[] chunks)
    {
        foreach (var c in chunks)
        {
            await Task.Yield();
            yield return c;
        }
        throw new InvalidOperationException("upstream broke");
    }

    [Fact]
    public async Task PostAsync_PlainResponse_CallsSendMessageAsyncString()
    {
        var sut = CreateSut(out _);
        var channel = CreateChannel(out _, out _);

        var response = ChatResponse.Plain(
            "all systems nominal",
            new[] { new Citation("docs/status.md") });

        await sut.PostAsync(channel, response, CancellationToken.None);

        // Plain path uses the (string) overload; assert the text matches the
        // translator output (body + Sources footer).
        await channel.Received(1).SendMessageAsync(
            Arg.Is<string>(s =>
                s.Contains("all systems nominal")
                && s.Contains("Sources:")
                && s.Contains("- docs/status.md")),
            Arg.Any<bool>(),
            Arg.Any<Embed>(),
            Arg.Any<RequestOptions>(),
            Arg.Any<AllowedMentions>(),
            Arg.Any<MessageReference>(),
            Arg.Any<MessageComponent>(),
            Arg.Any<ISticker[]>(),
            Arg.Any<Embed[]>(),
            Arg.Any<MessageFlags>(),
            Arg.Any<PollProperties>());
    }

    [Fact]
    public async Task PostAsync_StreamingResponse_PostsInitialEmbed_ThenEdits()
    {
        var sut = CreateSut(out var time);
        var channel = CreateChannel(out var posted, out var edits);

        var response = ChatResponse.Streaming(Stream("hello", " ", "world"));

        // Advance time between chunks so the throttler edits on subsequent
        // appends rather than coalescing into a single final edit.
        var postTask = Task.Run(async () =>
        {
            // Start posting; throttler's initial append schedules the first edit.
            await sut.PostAsync(channel, response, CancellationToken.None);
        });

        // Let chunks flow; advance the FakeTimeProvider past the throttle
        // window a few times to allow intermediate edits to fire.
        while (!postTask.IsCompleted)
        {
            time.Advance(Interval + TimeSpan.FromMilliseconds(10));
            await Task.Yield();
        }

        await postTask;

        // Initial embed posted exactly once.
        await channel.Received(1).SendMessageAsync(
            Arg.Any<string>(),
            Arg.Any<bool>(),
            Arg.Is<Embed>(e => e != null && e.Description == Cursor),
            Arg.Any<RequestOptions>(),
            Arg.Any<AllowedMentions>(),
            Arg.Any<MessageReference>(),
            Arg.Any<MessageComponent>(),
            Arg.Any<ISticker[]>(),
            Arg.Any<Embed[]>(),
            Arg.Any<MessageFlags>(),
            Arg.Any<PollProperties>());

        edits.ShouldNotBeEmpty();
        edits[^1].ShouldBe("hello world");
    }

    [Fact]
    public async Task PostAsync_StreamingResponse_AppendsCitationFooter()
    {
        var sut = CreateSut(out var time);
        var channel = CreateChannel(out _, out var edits);

        var citations = new[]
        {
            new Citation("src/CheckoutService.cs:42"),
            new Citation("docs/checkout.md:1"),
        };
        var response = ChatResponse.Streaming(Stream("body"), citations);

        var postTask = sut.PostAsync(channel, response, CancellationToken.None);
        while (!postTask.IsCompleted)
        {
            time.Advance(Interval + TimeSpan.FromMilliseconds(10));
            await Task.Yield();
        }
        await postTask;

        var final = edits[^1];
        final.ShouldContain("body");
        final.ShouldContain("Sources:");
        final.ShouldContain("- src/CheckoutService.cs:42");
        final.ShouldContain("- docs/checkout.md:1");
    }

    [Fact]
    public async Task PostAsync_StreamingResponse_StreamThrows_FinalizesWithErrorFooter()
    {
        var sut = CreateSut(out var time);
        var channel = CreateChannel(out _, out var edits);

        var response = ChatResponse.Streaming(StreamThenThrow("partial-1 ", "partial-2"));

        var postTask = sut.PostAsync(channel, response, CancellationToken.None);
        while (!postTask.IsCompleted)
        {
            time.Advance(Interval + TimeSpan.FromMilliseconds(10));
            await Task.Yield();
        }
        await postTask;

        var final = edits[^1];
        final.ShouldContain("partial-1 ");
        final.ShouldContain("partial-2");
        final.ShouldContain("(connection error)");
    }

    [Fact]
    public async Task PostAsync_StreamingResponse_NoCitations_NoSourcesFooter()
    {
        var sut = CreateSut(out var time);
        var channel = CreateChannel(out _, out var edits);

        var response = ChatResponse.Streaming(Stream("just text"));

        var postTask = sut.PostAsync(channel, response, CancellationToken.None);
        while (!postTask.IsCompleted)
        {
            time.Advance(Interval + TimeSpan.FromMilliseconds(10));
            await Task.Yield();
        }
        await postTask;

        var final = edits[^1];
        final.ShouldBe("just text");
        final.ShouldNotContain("Sources:");
    }

    [Fact]
    public async Task PostAsync_StreamingResponse_ExceedsMaxLength_TruncatesToTail()
    {
        var sut = CreateSut(out var time);
        var channel = CreateChannel(out _, out var edits);

        // One huge chunk well beyond the 4096 description cap.
        var huge = new string('x', 5000);
        var response = ChatResponse.Streaming(Stream(huge));

        var postTask = sut.PostAsync(channel, response, CancellationToken.None);
        while (!postTask.IsCompleted)
        {
            time.Advance(Interval + TimeSpan.FromMilliseconds(10));
            await Task.Yield();
        }
        await postTask;

        var final = edits[^1];
        final.Length.ShouldBeLessThanOrEqualTo(DiscordEmbedReplyPoster.MaxEmbedDescriptionLength);
        final.ShouldStartWith("... (truncated)");
        final.ShouldEndWith("x");
    }

    [Fact]
    public async Task PostAsync_CancelledMidStream_StopsCleanly()
    {
        var sut = CreateSut(out var time);
        var channel = CreateChannel(out _, out var edits);

        using var cts = new CancellationTokenSource();

        // Stream that emits a chunk, waits, then emits another. We cancel
        // between chunks so the foreach loop observes the cancellation.
        async IAsyncEnumerable<string> SlowStream(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return "first";
            await Task.Delay(50, ct);
            yield return "second";
        }

        var response = ChatResponse.Streaming(SlowStream());
        var postTask = sut.PostAsync(channel, response, cts.Token);
        await Task.Yield();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            while (!postTask.IsCompleted)
            {
                time.Advance(Interval + TimeSpan.FromMilliseconds(10));
                await Task.Yield();
            }
            await postTask;
        });

        // No edits should ever contain "second" — cancellation aborts before
        // the second chunk is appended.
        edits.ShouldAllBe(e => !e.Contains("second"));
    }
}
