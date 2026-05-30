using System.Runtime.CompilerServices;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Llm.Abstractions;

namespace Scout.Llm.Tests;

public sealed class ChatGatewayTests
{
    private static IChatCompletionService ChatReturning(params StreamingChatMessageContent[] chunks)
    {
        var chat = Substitute.For<IChatCompletionService>();
        chat.GetStreamingChatMessageContentsAsync(
                Arg.Any<ChatHistory>(),
                Arg.Any<PromptExecutionSettings?>(),
                Arg.Any<Kernel?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => ToAsync(chunks, CancellationToken.None));
        return chat;
    }

    private static StreamingChatMessageContent Chunk(string? content)
        => new(AuthorRole.Assistant, content);

    private static async IAsyncEnumerable<StreamingChatMessageContent> ToAsync(
        IEnumerable<StreamingChatMessageContent> chunks,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return chunk;
        }
    }

    private static ChatGateway Gateway(IChatCompletionService chat) => new(chat);

    private static ChatRequest Request() => new() { UserMessage = "hello" };

    [Fact]
    public void Ctor_Throws_When_Chat_Null()
    {
        Should.Throw<ArgumentNullException>(() => new ChatGateway(null!));
    }

    [Fact]
    public async Task StreamAsync_Yields_Chunk_Contents_In_Order()
    {
        var chat = ChatReturning(Chunk("a"), Chunk("b"), Chunk("c"));
        var gateway = Gateway(chat);

        var result = new List<string>();
        await foreach (var chunk in gateway.StreamAsync(Request(), CancellationToken.None))
        {
            result.Add(chunk);
        }

        result.ShouldBe(["a", "b", "c"]);
    }

    [Fact]
    public async Task StreamAsync_Skips_Null_And_Empty_Chunks()
    {
        var chat = ChatReturning(Chunk("a"), Chunk(null), Chunk(""), Chunk("b"));
        var gateway = Gateway(chat);

        var result = new List<string>();
        await foreach (var chunk in gateway.StreamAsync(Request(), CancellationToken.None))
        {
            result.Add(chunk);
        }

        result.ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task CompleteAsync_Returns_Concatenation_Of_Chunks()
    {
        var chat = ChatReturning(Chunk("foo"), Chunk(" "), Chunk("bar"));
        var gateway = Gateway(chat);

        var result = await gateway.CompleteAsync(Request(), CancellationToken.None);

        result.ShouldBe("foo bar");
    }

    [Fact]
    public async Task StreamAsync_Cancellation_Stops_And_Surfaces_OperationCanceled_With_Partial_Chunks()
    {
        using var cts = new CancellationTokenSource();
        var chat = Substitute.For<IChatCompletionService>();
        chat.GetStreamingChatMessageContentsAsync(
                Arg.Any<ChatHistory>(),
                Arg.Any<PromptExecutionSettings?>(),
                Arg.Any<Kernel?>(),
                Arg.Any<CancellationToken>())
            .Returns(call => CancelAfterFirst((CancellationToken)call[3], cts));
        var gateway = Gateway(chat);

        var delivered = new List<string>();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var chunk in gateway.StreamAsync(Request(), cts.Token))
            {
                delivered.Add(chunk);
            }
        });

        delivered.ShouldBe(["first"]);
    }

    private static async IAsyncEnumerable<StreamingChatMessageContent> CancelAfterFirst(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        CancellationTokenSource cts)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return Chunk("first");

        await cts.CancelAsync();
        cancellationToken.ThrowIfCancellationRequested();
        yield return Chunk("never");
    }

    [Fact]
    public async Task StreamAsync_Throws_When_Request_Null()
    {
        var gateway = Gateway(Substitute.For<IChatCompletionService>());

        await Should.ThrowAsync<ArgumentNullException>(async () =>
        {
            await foreach (var _ in gateway.StreamAsync(null!, CancellationToken.None))
            {
            }
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task StreamAsync_Throws_When_UserMessage_Whitespace(string message)
    {
        var gateway = Gateway(Substitute.For<IChatCompletionService>());
        var request = new ChatRequest { UserMessage = message };

        var ex = await Should.ThrowAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in gateway.StreamAsync(request, CancellationToken.None))
            {
            }
        });
        ex.Message.ShouldContain("UserMessage");
    }

    [Fact]
    public async Task CompleteAsync_Throws_When_Request_Null()
    {
        var gateway = Gateway(Substitute.For<IChatCompletionService>());

        await Should.ThrowAsync<ArgumentNullException>(
            () => gateway.CompleteAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task StreamChatAsync_Composes_Persona_And_SystemPrompt_Into_One_System_Message()
    {
        var captured = new List<ChatHistory>();
        var chat = Substitute.For<IChatCompletionService>();
        chat.GetStreamingChatMessageContentsAsync(
                Arg.Do<ChatHistory>(captured.Add),
                Arg.Any<PromptExecutionSettings?>(),
                Arg.Any<Kernel?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => ToAsync([Chunk("ok")], CancellationToken.None));
        var gateway = Gateway(chat);

        await foreach (var _ in gateway.StreamAsync(
            new ChatRequest
            {
                UserMessage = "hello",
                Persona = "You are Scout's Code Reviewer.",
                SystemPrompt = "Injected skill: prefer small diffs.",
            },
            CancellationToken.None))
        {
        }

        var systemMessages = captured
            .SelectMany(history => history)
            .Where(message => message.Role == AuthorRole.System)
            .Select(message => message.Content)
            .ToList();

        systemMessages.ShouldHaveSingleItem();
        systemMessages[0].ShouldBe(
            "You are Scout's Code Reviewer.\n\nInjected skill: prefer small diffs.");
    }
}
