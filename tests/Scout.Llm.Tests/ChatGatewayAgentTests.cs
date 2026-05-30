using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Llm.Abstractions;

namespace Scout.Llm.Tests;

/// <summary>
/// When a Project Expert agent is supplied, the gateway must stream the response
/// through the agent (so the grounding instructions and tools apply) while keeping
/// the IAsyncEnumerable&lt;string&gt; contract the pump consumes.
/// </summary>
public sealed class ChatGatewayAgentTests
{
    private static IChatCompletionService ChatReturning(params string?[] contents)
    {
        var chat = Substitute.For<IChatCompletionService>();
        chat.GetStreamingChatMessageContentsAsync(
                Arg.Any<ChatHistory>(),
                Arg.Any<PromptExecutionSettings?>(),
                Arg.Any<Kernel?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => ToAsync(contents, CancellationToken.None));
        return chat;
    }

    // Captures the ChatHistory that reaches the chat service. The agent appends
    // its own Instructions and the AgentInvokeOptions.AdditionalInstructions as
    // system messages, so the captured history is the observable seam for the
    // composed system prompt forwarded by StreamAgentAsync.
    private static (IChatCompletionService chat, List<ChatHistory> captured) ChatCapturing(
        params string?[] contents)
    {
        var captured = new List<ChatHistory>();
        var chat = Substitute.For<IChatCompletionService>();
        chat.GetStreamingChatMessageContentsAsync(
                Arg.Do<ChatHistory>(captured.Add),
                Arg.Any<PromptExecutionSettings?>(),
                Arg.Any<Kernel?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => ToAsync(contents, CancellationToken.None));
        return (chat, captured);
    }

    private static async IAsyncEnumerable<StreamingChatMessageContent> ToAsync(
        IEnumerable<string?> contents,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var content in contents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new StreamingChatMessageContent(AuthorRole.Assistant, content);
        }
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

    [Fact]
    public async Task StreamAsync_Routes_Through_Agent_And_Yields_Content()
    {
        var chat = ChatReturning("hello ", "world");
        var (gateway, _) = AgentGateway(chat);

        var result = new List<string>();
        await foreach (var token in gateway.StreamAsync(
            new ChatRequest { UserMessage = "what is scout?" }, CancellationToken.None))
        {
            result.Add(token);
        }

        string.Concat(result).ShouldBe("hello world");
    }

    [Fact]
    public async Task StreamAsync_With_Agent_Skips_Empty_Content()
    {
        var chat = ChatReturning("a", null, "", "b");
        var (gateway, _) = AgentGateway(chat);

        var result = new List<string>();
        await foreach (var token in gateway.StreamAsync(
            new ChatRequest { UserMessage = "q" }, CancellationToken.None))
        {
            result.Add(token);
        }

        result.ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task StreamAsync_With_Agent_Forwards_Composed_System_Prompt()
    {
        const string systemPrompt = "Base persona + injected skill body.";
        var (chat, captured) = ChatCapturing("ok");
        var (gateway, _) = AgentGateway(chat);

        await foreach (var _ in gateway.StreamAsync(
            new ChatRequest { UserMessage = "what is scout?", SystemPrompt = systemPrompt },
            CancellationToken.None))
        {
        }

        // The composed prompt is forwarded as AgentInvokeOptions.AdditionalInstructions,
        // which the agent surfaces as a system message in the chat history. Without
        // the forwarding (options: null) this system message is absent and the test fails.
        var systemMessages = captured
            .SelectMany(history => history)
            .Where(message => message.Role == AuthorRole.System)
            .Select(message => message.Content)
            .ToList();

        systemMessages.ShouldContain(systemPrompt);
    }
}
