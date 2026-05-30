using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Llm.Abstractions;

namespace Scout.Llm.Tests;

/// <summary>
/// The gateway translates a provider 429 (HttpOperationException with
/// TooManyRequests) into the domain <see cref="LlmRateLimitException"/> so the
/// pump can finalize gracefully. Any other HttpOperationException propagates
/// unchanged. Covers both the chat and agent streaming paths.
/// </summary>
public sealed class ChatGatewayRateLimitTests
{
    private static StreamingChatMessageContent Chunk(string? content)
        => new(AuthorRole.Assistant, content);

    private static async IAsyncEnumerable<StreamingChatMessageContent> ChunkThenThrow(
        StreamingChatMessageContent chunk,
        Exception toThrow,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        yield return chunk;
        throw toThrow;
    }

    private static IChatCompletionService ChatChunkThenThrow(
        StreamingChatMessageContent chunk, Exception toThrow)
    {
        var chat = Substitute.For<IChatCompletionService>();
        chat.GetStreamingChatMessageContentsAsync(
                Arg.Any<ChatHistory>(),
                Arg.Any<PromptExecutionSettings?>(),
                Arg.Any<Kernel?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => ChunkThenThrow(chunk, toThrow, CancellationToken.None));
        return chat;
    }

    private static (ChatGateway gateway, Kernel kernel) AgentGateway(IChatCompletionService chat)
    {
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton(chat);
        var kernel = builder.Build();
        var agent = new ChatCompletionAgent
        {
            Name = "Project Expert",
            Instructions = "Ground answers in project context.",
            Kernel = kernel,
        };
        return (new ChatGateway(chat, kernel, agent: agent), kernel);
    }

    private static ChatRequest Request() => new() { UserMessage = "hello" };

    private static async Task<List<string>> Drain(IAsyncEnumerable<string> stream)
    {
        var delivered = new List<string>();
        await foreach (var chunk in stream)
        {
            delivered.Add(chunk);
        }

        return delivered;
    }

    [Fact]
    public async Task StreamAsync_Translates_429_HttpOperationException_To_LlmRateLimitException()
    {
        var sk = new HttpOperationException(
            HttpStatusCode.TooManyRequests, "rate", "body", null);
        var chat = ChatChunkThenThrow(Chunk("pre"), sk);
        var gateway = new ChatGateway(chat);

        var delivered = new List<string>();
        var ex = await Should.ThrowAsync<LlmRateLimitException>(async () =>
        {
            await foreach (var chunk in gateway.StreamAsync(Request(), CancellationToken.None))
            {
                delivered.Add(chunk);
            }
        });

        ex.ShouldBeOfType<LlmRateLimitException>();
        ex.InnerException.ShouldBeOfType<HttpOperationException>();
        // The chunk emitted before the 429 still reached the consumer.
        delivered.ShouldBe(["pre"]);
    }

    [Fact]
    public async Task StreamAsync_NonRateLimit_HttpOperationException_Propagates_Unchanged()
    {
        var sk = new HttpOperationException(
            HttpStatusCode.InternalServerError, "boom", "body", null);
        var chat = ChatChunkThenThrow(Chunk("pre"), sk);
        var gateway = new ChatGateway(chat);

        // 500 is not a rate limit: the raw SK exception must surface untranslated.
        await Should.ThrowAsync<HttpOperationException>(
            () => Drain(gateway.StreamAsync(Request(), CancellationToken.None)));
    }

    [Fact]
    public async Task StreamAsync_Agent_Translates_429_HttpOperationException_To_LlmRateLimitException()
    {
        var sk = new HttpOperationException(
            HttpStatusCode.TooManyRequests, "rate", "body", null);
        var chat = ChatChunkThenThrow(Chunk("pre"), sk);
        var (gateway, _) = AgentGateway(chat);

        var ex = await Should.ThrowAsync<LlmRateLimitException>(
            () => Drain(gateway.StreamAsync(Request(), CancellationToken.None)));

        ex.InnerException.ShouldBeOfType<HttpOperationException>();
    }
}
