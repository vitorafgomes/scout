using System.ClientModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Llm.Abstractions;

namespace Scout.Llm.Tests;

/// <summary>
/// The gateway translates transport-level provider-down failures (connection
/// refused, socket error, internal timeout) into the domain
/// <see cref="LlmProviderUnavailableException"/> so the pump can finalize
/// gracefully. Caller cancellation propagates unchanged; reachable-but-erroring
/// responses (5xx) are left to the rate-limit/propagation path. Covers both the
/// chat and agent streaming paths.
/// </summary>
public sealed class ChatGatewayProviderUnavailableTests
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

    private static ChatGateway AgentGateway(IChatCompletionService chat)
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
        return new ChatGateway(chat, kernel, agent: agent);
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
    public async Task StreamAsync_Translates_HttpRequestException_To_ProviderUnavailable()
    {
        var transport = new HttpRequestException("Connection refused");
        var chat = ChatChunkThenThrow(Chunk("pre"), transport);
        var gateway = new ChatGateway(chat);

        var delivered = new List<string>();
        var ex = await Should.ThrowAsync<LlmProviderUnavailableException>(async () =>
        {
            await foreach (var chunk in gateway.StreamAsync(Request(), CancellationToken.None))
            {
                delivered.Add(chunk);
            }
        });

        ex.InnerException.ShouldBeOfType<HttpRequestException>();
        // The chunk emitted before the failure still reached the consumer.
        delivered.ShouldBe(["pre"]);
    }

    [Fact]
    public async Task StreamAsync_Translates_ClientResultException_Wrapping_HttpRequest()
    {
        // The real downed-Ollama shape: ClientResultException -> HttpRequestException
        // -> SocketException(111).
        var socket = new SocketException((int)SocketError.ConnectionRefused);
        var http = new HttpRequestException("Connection refused", socket);
        var transport = new ClientResultException("Connection refused", innerException: http);
        var chat = ChatChunkThenThrow(Chunk("pre"), transport);
        var gateway = new ChatGateway(chat);

        var ex = await Should.ThrowAsync<LlmProviderUnavailableException>(
            () => Drain(gateway.StreamAsync(Request(), CancellationToken.None)));

        ex.InnerException.ShouldBeOfType<ClientResultException>();
    }

    [Fact]
    public async Task StreamAsync_Translates_HttpOperationException_With_Null_Status()
    {
        // No HTTP response at all (null StatusCode) is a transport failure.
        var transport = new HttpOperationException("no response");
        transport.StatusCode.ShouldBeNull();
        var chat = ChatChunkThenThrow(Chunk("pre"), transport);
        var gateway = new ChatGateway(chat);

        var ex = await Should.ThrowAsync<LlmProviderUnavailableException>(
            () => Drain(gateway.StreamAsync(Request(), CancellationToken.None)));

        ex.InnerException.ShouldBeOfType<HttpOperationException>();
    }

    [Fact]
    public async Task StreamAsync_Translates_Internal_Timeout_TaskCanceled()
    {
        // Internal timeout (caller token NOT cancelled) -> provider unreachable.
        var transport = new TaskCanceledException("internal timeout");
        var chat = ChatChunkThenThrow(Chunk("pre"), transport);
        var gateway = new ChatGateway(chat);

        var ex = await Should.ThrowAsync<LlmProviderUnavailableException>(
            () => Drain(gateway.StreamAsync(Request(), CancellationToken.None)));

        ex.InnerException.ShouldBeOfType<TaskCanceledException>();
    }

    [Fact]
    public async Task StreamAsync_CallerCancellation_Propagates_Not_Translated()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var transport = new OperationCanceledException("caller cancelled", cts.Token);
        var chat = ChatChunkThenThrow(Chunk("pre"), transport);
        var gateway = new ChatGateway(chat);

        // Genuine caller cancellation must surface as cancellation, untranslated.
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in gateway.StreamAsync(Request(), cts.Token))
            {
            }
        });
    }

    [Fact]
    public async Task StreamAsync_Agent_Translates_HttpRequestException_To_ProviderUnavailable()
    {
        var transport = new HttpRequestException("Connection refused");
        var chat = ChatChunkThenThrow(Chunk("pre"), transport);
        var gateway = AgentGateway(chat);

        var ex = await Should.ThrowAsync<LlmProviderUnavailableException>(
            () => Drain(gateway.StreamAsync(Request(), CancellationToken.None)));

        ex.InnerException.ShouldBeOfType<HttpRequestException>();
    }

    [Fact]
    public async Task StreamAsync_429_Still_Translates_To_RateLimit_Not_ProviderUnavailable()
    {
        // The 429 path stays first; provider-down translation must not shadow it.
        var sk = new HttpOperationException(
            HttpStatusCode.TooManyRequests, "rate", "body", null);
        var chat = ChatChunkThenThrow(Chunk("pre"), sk);
        var gateway = new ChatGateway(chat);

        await Should.ThrowAsync<LlmRateLimitException>(
            () => Drain(gateway.StreamAsync(Request(), CancellationToken.None)));
    }
}
