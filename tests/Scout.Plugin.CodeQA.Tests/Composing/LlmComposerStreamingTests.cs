using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Plugin.CodeQA.Composing;
using Scout.Plugin.CodeQA.Domain;
using Scout.Plugin.CodeQA.Storage;

namespace Scout.Plugin.CodeQA.Tests.Composing;

public class LlmComposerStreamingTests
{
    private static VectorSearchResult MakeHit(
        string path = "src/A.cs",
        int start = 1,
        int end = 5,
        string content = "class A {}",
        double score = 0.8)
    {
        var embedding = new ReadOnlyMemory<float>(new[] { 0.1f, 0.2f });
        var chunk = new CodeChunk("id-" + path + "-" + start, path, start, end, content, embedding);
        return new VectorSearchResult(chunk, score);
    }

    private static IOptions<CodeQAOptions> DefaultOptions()
    {
        var opts = new CodeQAOptions();
        opts.Retrieval.MaxContextTokens = 4000;
        opts.Chunking.ApproxBytesPerToken = 4;
        opts.Composer.Temperature = 0.2;
        opts.Composer.MaxOutputTokens = 512;
        opts.Composer.Model = string.Empty;
        return Options.Create(opts);
    }

    private static IChatCompletionService ChatStreaming(params string?[] chunks)
    {
        var chat = Substitute.For<IChatCompletionService>();
        chat
            .GetStreamingChatMessageContentsAsync(
                Arg.Any<ChatHistory>(),
                Arg.Any<PromptExecutionSettings?>(),
                Arg.Any<Kernel?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Stream(chunks));
        return chat;
    }

    private static async IAsyncEnumerable<StreamingChatMessageContent> Stream(
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
#pragma warning disable CS0162 // Unreachable code required to satisfy iterator contract.
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
    public async Task ComposeStreamingAsync_YieldsChunksInOrder()
    {
        var composer = new LlmComposer(
            DefaultOptions(),
            ChatStreaming("Hel", "lo ", "world"));

        var collected = new List<string>();
        await foreach (var token in composer.ComposeStreamingAsync(
            "q", new[] { MakeHit() }, CancellationToken.None))
        {
            collected.Add(token);
        }

        collected.ShouldBe(new[] { "Hel", "lo ", "world" });
    }

    [Fact]
    public async Task ComposeStreamingAsync_SkipsNullOrEmptyChunks()
    {
        var composer = new LlmComposer(
            DefaultOptions(),
            ChatStreaming("A", string.Empty, null, "B"));

        var collected = new List<string>();
        await foreach (var token in composer.ComposeStreamingAsync(
            "q", new[] { MakeHit() }, CancellationToken.None))
        {
            collected.Add(token);
        }

        collected.ShouldBe(new[] { "A", "B" });
    }

    [Fact]
    public async Task ComposeStreamingAsync_ChatServiceThrows_PropagatesException()
    {
        var chat = Substitute.For<IChatCompletionService>();
        chat
            .GetStreamingChatMessageContentsAsync(
                Arg.Any<ChatHistory>(),
                Arg.Any<PromptExecutionSettings?>(),
                Arg.Any<Kernel?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Throwing());
        var composer = new LlmComposer(DefaultOptions(), chat);

        await Should.ThrowAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in composer.ComposeStreamingAsync(
                "q", new[] { MakeHit() }, CancellationToken.None))
            {
            }
        });
    }

    [Fact]
    public async Task ComposeStreamingAsync_CancelledMidStream_StopsYielding()
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
        var composer = new LlmComposer(DefaultOptions(), chat);

        var collected = new List<string>();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var token in composer.ComposeStreamingAsync(
                "q", new[] { MakeHit() }, cts.Token))
            {
                collected.Add(token);
                if (collected.Count == 2)
                {
                    cts.Cancel();
                }
            }
        });

        // Got the first two tokens before cancellation kicked in.
        collected.Count.ShouldBeLessThan(4);
    }

    [Fact]
    public async Task ComposeStreamingAsync_NullQuestion_Throws()
    {
        var composer = new LlmComposer(DefaultOptions(), ChatStreaming("x"));

        await Should.ThrowAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in composer.ComposeStreamingAsync(
                null!, new[] { MakeHit() }, CancellationToken.None))
            {
            }
        });
    }

    [Fact]
    public async Task ComposeStreamingAsync_NullContext_Throws()
    {
        var composer = new LlmComposer(DefaultOptions(), ChatStreaming("x"));

        await Should.ThrowAsync<ArgumentNullException>(async () =>
        {
            await foreach (var _ in composer.ComposeStreamingAsync(
                "q", null!, CancellationToken.None))
            {
            }
        });
    }
}
