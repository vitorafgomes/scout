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
            .Returns(call => CancelAfterFirst(cts, (CancellationToken)call[3]));
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
        CancellationTokenSource cts,
        [EnumeratorCancellation] CancellationToken cancellationToken)
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

    private static (IChatCompletionService chat, List<ChatHistory> captured) ChatCapturing(
        params StreamingChatMessageContent[] chunks)
    {
        var captured = new List<ChatHistory>();
        var chat = Substitute.For<IChatCompletionService>();
        chat.GetStreamingChatMessageContentsAsync(
                Arg.Do<ChatHistory>(captured.Add),
                Arg.Any<PromptExecutionSettings?>(),
                Arg.Any<Kernel?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => ToAsync(chunks, CancellationToken.None));
        return (chat, captured);
    }

    private static List<(AuthorRole Role, string? Content)> Turns(IEnumerable<ChatHistory> captured)
        => captured
            .SelectMany(history => history)
            .Select(message => (message.Role, message.Content))
            .ToList();

    [Fact]
    public async Task StreamChatAsync_Replays_History_In_Order_Between_System_And_Current_User()
    {
        var (chat, captured) = ChatCapturing(Chunk("ok"));
        var gateway = Gateway(chat);

        await foreach (var _ in gateway.StreamAsync(
            new ChatRequest
            {
                UserMessage = "current",
                SystemPrompt = "sys",
                History =
                [
                    new ConversationTurn(ConversationRole.User, "q1"),
                    new ConversationTurn(ConversationRole.Assistant, "a1"),
                ],
            },
            CancellationToken.None))
        {
        }

        Turns(captured).ShouldBe(
        [
            (AuthorRole.System, "sys"),
            (AuthorRole.User, "q1"),
            (AuthorRole.Assistant, "a1"),
            (AuthorRole.User, "current"),
        ]);
    }

    [Fact]
    public async Task StreamChatAsync_Empty_History_Is_ByteIdentical_To_Baseline()
    {
        var (chat, captured) = ChatCapturing(Chunk("ok"));
        var gateway = Gateway(chat);

        await foreach (var _ in gateway.StreamAsync(
            new ChatRequest { UserMessage = "hello", SystemPrompt = "sys" },
            CancellationToken.None))
        {
        }

        // No History -> identical to the single-turn shape: system then current user.
        Turns(captured).ShouldBe(
        [
            (AuthorRole.System, "sys"),
            (AuthorRole.User, "hello"),
        ]);
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

    private static readonly byte[] Screenshot = [0xFF, 0xD8, 0xFF, 0xE0, 0x10, 0x4A, 0x46, 0x49];

    [Fact]
    public async Task An_image_on_the_request_becomes_an_image_part_of_the_user_message()
    {
        var (chat, captured) = ChatCapturing(Chunk("a terminal"));
        var gateway = new ChatGateway(chat, media: new MediaCapabilities(false, false, "OpenAI") { Vision = true });

        var answer = await gateway.CompleteAsync(
            new ChatRequest
            {
                UserMessage = "what is on my screen?",
                SystemPrompt = "sys",
                History = [new ConversationTurn(ConversationRole.User, "earlier")],
                Image = new ChatImage("image/jpeg", Screenshot),
            },
            CancellationToken.None);

        answer.ShouldBe("a terminal");
        var history = captured.ShouldHaveSingleItem();
        var user = history[^1];
        user.Role.ShouldBe(AuthorRole.User);
        user.Items.OfType<TextContent>().ShouldHaveSingleItem().Text.ShouldBe("what is on my screen?");
        var image = user.Items.OfType<ImageContent>().ShouldHaveSingleItem();
        image.MimeType.ShouldBe("image/jpeg");
        image.Data!.Value.ToArray().ShouldBe(Screenshot);
        history.Take(history.Count - 1).SelectMany(m => m.Items).OfType<ImageContent>().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_image_with_a_provider_without_vision_throws_before_any_call(bool mediaKnown)
    {
        var chat = ChatReturning(Chunk("never"));
        var gateway = mediaKnown
            ? new ChatGateway(chat, media: new MediaCapabilities(true, true, "Ollama"))
            : new ChatGateway(chat);

        await Should.ThrowAsync<LlmVisionUnavailableException>(() => gateway.CompleteAsync(
            new ChatRequest { UserMessage = "what is on my screen?", Image = new ChatImage("image/jpeg", Screenshot) },
            CancellationToken.None));

        chat.ReceivedCalls().ShouldBeEmpty();
    }
}
