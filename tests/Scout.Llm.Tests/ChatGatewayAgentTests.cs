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

    // A templated agent whose instruction is purely "{{$persona}}" with no fixed
    // Instructions and no default persona argument: the only way a persona body
    // reaches the chat history is the per-turn KernelArguments["persona"] override
    // forwarded by StreamAgentAsync.
    private static (ChatGateway gateway, Kernel kernel) TemplatedAgentGateway(IChatCompletionService chat)
    {
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton(chat);
        var kernel = builder.Build();
        var config = new PromptTemplateConfig
        {
            Template = "{{$persona}}",
            TemplateFormat = PromptTemplateConfig.SemanticKernelTemplateFormat,
            Name = "Project Expert",
            InputVariables =
            {
                new InputVariable { Name = "persona", AllowDangerouslySetContent = true },
            },
        };
        var agent = new ChatCompletionAgent(config, new KernelPromptTemplateFactory())
        {
            Name = "Project Expert",
            Kernel = kernel,
        };
        return (new ChatGateway(chat, kernel, agent: agent), kernel);
    }

    private static List<string?> SystemMessages(IEnumerable<ChatHistory> captured)
        => captured
            .SelectMany(history => history)
            .Where(message => message.Role == AuthorRole.System)
            .Select(message => message.Content)
            .ToList();

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
        var systemMessages = SystemMessages(captured);

        systemMessages.ShouldContain(systemPrompt);
    }

    [Fact]
    public async Task StreamAsync_With_Templated_Agent_Surfaces_Persona_As_System_Message()
    {
        const string persona = "You are Scout's Code Reviewer. Be terse and cite files.";
        var (chat, captured) = ChatCapturing("ok");
        var (gateway, _) = TemplatedAgentGateway(chat);

        await foreach (var _ in gateway.StreamAsync(
            new ChatRequest { UserMessage = "review this", Persona = persona },
            CancellationToken.None))
        {
        }

        // KernelArguments["persona"] overrides the agent's (absent) default, so the
        // "{{$persona}}" template renders the persona body into the agent instruction,
        // which surfaces as a system message in the captured history.
        SystemMessages(captured).ShouldContain(persona);
    }

    [Fact]
    public async Task StreamAsync_With_Templated_Agent_Surfaces_Both_Persona_And_SystemPrompt()
    {
        const string persona = "You are Scout's Code Reviewer.";
        const string systemPrompt = "Injected skill: prefer small diffs.";
        var (chat, captured) = ChatCapturing("ok");
        var (gateway, _) = TemplatedAgentGateway(chat);

        await foreach (var _ in gateway.StreamAsync(
            new ChatRequest
            {
                UserMessage = "review this",
                Persona = persona,
                SystemPrompt = systemPrompt,
            },
            CancellationToken.None))
        {
        }

        // Persona binds via KernelArguments; SystemPrompt rides AdditionalInstructions.
        // Both reach the agent and surface as distinct system messages.
        var systemMessages = SystemMessages(captured);
        systemMessages.ShouldContain(persona);
        systemMessages.ShouldContain(systemPrompt);
    }
}
