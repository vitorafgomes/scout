using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Core.Chat;
using Scout.Plugin.Chat;

namespace Scout.Plugin.Chat.Tests;

public class LlmFallbackRouterTests
{
    private static IOptions<LlmChatOptions> DefaultOptions(LlmChatOptions? opts = null)
        => Options.Create(opts ?? new LlmChatOptions());

    private static ChatMessage Msg(string text)
        => new(UserId: "u1", ChannelId: "c1", Text: text);

    private static IChatCompletionService ChatYielding(params string?[] chunks)
    {
        var chat = Substitute.For<IChatCompletionService>();
        chat
            .GetStreamingChatMessageContentsAsync(
                Arg.Any<ChatHistory>(),
                Arg.Any<PromptExecutionSettings?>(),
                Arg.Any<Kernel?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => StreamFrom(chunks));
        return chat;
    }

    private static async IAsyncEnumerable<StreamingChatMessageContent> StreamFrom(
        params string?[] chunks)
    {
        foreach (var c in chunks)
        {
            await Task.Yield();
            yield return new StreamingChatMessageContent(AuthorRole.Assistant, c);
        }
    }

    private static async IAsyncEnumerable<StreamingChatMessageContent> Throwing()
    {
        await Task.Yield();
        throw new HttpRequestException("connection refused");
#pragma warning disable CS0162 // Unreachable yield required by iterator contract.
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<StreamingChatMessageContent> CancellableStream(
        IReadOnlyList<string> chunks,
        CancellationToken ct,
        [EnumeratorCancellation] CancellationToken enumeratorCt = default)
    {
        foreach (var c in chunks)
        {
            ct.ThrowIfCancellationRequested();
            enumeratorCt.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new StreamingChatMessageContent(AuthorRole.Assistant, c);
        }
    }

    [Fact]
    public async Task RouteAsync_NullMessage_Throws()
    {
        var router = new LlmFallbackRouter(ChatYielding("ignored"), DefaultOptions());

        Func<Task> act = () => router.RouteAsync(null!, CancellationToken.None);

        await act.ShouldThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void Blank_message_text_is_rejected_by_ChatMessage_contract()
    {
        // The router's defensive IsNullOrWhiteSpace branch can never be
        // exercised through ChatMessage because the contract itself
        // refuses blank Text at construction. Pin the boundary here so
        // the contract remains the single source of truth.
        Action build = () => _ = new ChatMessage(UserId: "u", ChannelId: "c", Text: "   ");

        build.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public async Task RouteAsync_NonEmptyMessage_ReturnsStreamingResponse()
    {
        var router = new LlmFallbackRouter(ChatYielding("hi"), DefaultOptions());

        var response = await router.RouteAsync(Msg("hello"), CancellationToken.None);

        response.ShouldNotBeNull();
        response!.IsStreaming.ShouldBeTrue();
        response.Stream.ShouldNotBeNull();
        response.Text.ShouldBeNull();
        response.Citations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Stream_YieldsAllNonEmptyChunks()
    {
        var router = new LlmFallbackRouter(
            ChatYielding("Hello ", string.Empty, null, "world"),
            DefaultOptions());

        var response = await router.RouteAsync(Msg("hi"), CancellationToken.None);

        var collected = new List<string>();
        await foreach (var token in response!.Stream!)
        {
            collected.Add(token);
        }

        collected.ShouldBe(new[] { "Hello ", "world" });
    }

    [Fact]
    public async Task Stream_ChatCompletionThrows_PropagatesException()
    {
        var chat = Substitute.For<IChatCompletionService>();
        chat
            .GetStreamingChatMessageContentsAsync(
                Arg.Any<ChatHistory>(),
                Arg.Any<PromptExecutionSettings?>(),
                Arg.Any<Kernel?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Throwing());
        var router = new LlmFallbackRouter(chat, DefaultOptions());

        var response = await router.RouteAsync(Msg("hi"), CancellationToken.None);

        await Should.ThrowAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in response!.Stream!)
            {
            }
        });
    }

    [Fact]
    public async Task Stream_PassesSystemPromptToChatHistory()
    {
        var opts = new LlmChatOptions { SystemPrompt = "system-prompt-marker" };
        var chat = Substitute.For<IChatCompletionService>();
        ChatHistory? capturedHistory = null;
        chat
            .GetStreamingChatMessageContentsAsync(
                Arg.Do<ChatHistory>(h => capturedHistory = h),
                Arg.Any<PromptExecutionSettings?>(),
                Arg.Any<Kernel?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => StreamFrom("ok"));
        var router = new LlmFallbackRouter(chat, DefaultOptions(opts));

        var response = await router.RouteAsync(Msg("hi"), CancellationToken.None);
        await foreach (var _ in response!.Stream!)
        {
        }

        capturedHistory.ShouldNotBeNull();
        capturedHistory![0].Role.ShouldBe(AuthorRole.System);
        capturedHistory[0].Content.ShouldBe("system-prompt-marker");
    }

    [Fact]
    public async Task Stream_PassesUserTextToChatHistory()
    {
        var chat = Substitute.For<IChatCompletionService>();
        ChatHistory? capturedHistory = null;
        chat
            .GetStreamingChatMessageContentsAsync(
                Arg.Do<ChatHistory>(h => capturedHistory = h),
                Arg.Any<PromptExecutionSettings?>(),
                Arg.Any<Kernel?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => StreamFrom("ok"));
        var router = new LlmFallbackRouter(chat, DefaultOptions());

        var response = await router.RouteAsync(Msg("user-body"), CancellationToken.None);
        await foreach (var _ in response!.Stream!)
        {
        }

        capturedHistory.ShouldNotBeNull();
        var userTurn = capturedHistory!.Last(m => m.Role == AuthorRole.User);
        userTurn.Content.ShouldBe("user-body");
    }

    [Fact]
    public async Task Stream_RespectsCancellation()
    {
        using var cts = new CancellationTokenSource();
        var chat = Substitute.For<IChatCompletionService>();
        chat
            .GetStreamingChatMessageContentsAsync(
                Arg.Any<ChatHistory>(),
                Arg.Any<PromptExecutionSettings?>(),
                Arg.Any<Kernel?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci => CancellableStream(
                new[] { "a", "b", "c", "d" },
                ci.Arg<CancellationToken>()));
        var router = new LlmFallbackRouter(chat, DefaultOptions());

        var response = await router.RouteAsync(Msg("hi"), cts.Token);

        var collected = new List<string>();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var token in response!.Stream!.WithCancellation(cts.Token))
            {
                collected.Add(token);
                if (collected.Count == 2)
                {
                    cts.Cancel();
                }
            }
        });

        collected.Count.ShouldBeLessThan(4);
    }
}
