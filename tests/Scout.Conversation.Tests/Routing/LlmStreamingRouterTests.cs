using Microsoft.Extensions.Options;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Routing;
using Scout.Llm.Abstractions;

namespace Scout.Conversation.Tests.Routing;

public sealed class LlmStreamingRouterTests
{
    private static async IAsyncEnumerable<string> Tokens(params string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            await Task.Yield();
            yield return chunk;
        }
    }

    private static LlmStreamingRouter Router(
        IChatGateway gateway,
        LlmStreamingRouterOptions? options = null)
        => new(gateway, Options.Create(options ?? new LlmStreamingRouterOptions
        {
            SystemPrompt = "sys",
            Temperature = 0.3,
            MaxTokens = 100,
        }));

    [Fact]
    public void Ctor_Throws_When_Gateway_Null()
    {
        Should.Throw<ArgumentNullException>(
            () => new LlmStreamingRouter(null!, Options.Create(new LlmStreamingRouterOptions())));
    }

    [Fact]
    public void Route_Throws_When_Message_Null()
    {
        var gateway = Substitute.For<IChatGateway>();
        Should.Throw<ArgumentNullException>(() => Router(gateway).Route(null!));
    }

    [Fact]
    public async Task Route_Maps_Message_To_Request_And_Forwards_Tokens()
    {
        var gateway = Substitute.For<IChatGateway>();
        ChatRequest? captured = null;
        gateway.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = (ChatRequest)call[0];
                return Tokens("one", "two");
            });

        var message = new ChatMessage("u1", "c1", "what is up");
        var response = Router(gateway).Route(message);

        var collected = new List<string>();
        await foreach (var token in response.Tokens)
        {
            collected.Add(token);
        }

        collected.ShouldBe(["one", "two"]);
        captured.ShouldNotBeNull();
        captured!.UserMessage.ShouldBe("what is up");
        captured.SystemPrompt.ShouldBe("sys");
        captured.Temperature.ShouldBe(0.3);
        captured.MaxTokens.ShouldBe(100);
        response.Citations.ShouldBeEmpty();
    }

    [Fact]
    public void Route_Does_Not_Invoke_Gateway_Until_Enumerated()
    {
        var gateway = Substitute.For<IChatGateway>();
        gateway.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(Tokens("x"));

        _ = Router(gateway).Route(new ChatMessage("u1", "c1", "hi"));

        gateway.DidNotReceive().StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>());
    }
}
