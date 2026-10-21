using System.Runtime.CompilerServices;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Scout.Llm.Abstractions;

namespace Scout.Llm.Tests;

/// <summary>
/// Verifies the gateway opts the chat call into automatic function-calling and
/// hands the composed kernel to the chat-completion service, so the LLM can
/// invoke registered plugins.
/// </summary>
public sealed class ChatGatewayFunctionCallingTests
{
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

    [Fact]
    public void BuildSettings_Carries_FunctionChoiceBehavior_Auto()
    {
        var settings = ChatGateway.BuildSettings(new ChatRequest { UserMessage = "hi" });

        settings.FunctionChoiceBehavior.ShouldNotBeNull();
        var auto = FunctionChoiceBehavior.Auto();
        settings.FunctionChoiceBehavior!.GetType().ShouldBe(auto.GetType());
    }

    [Fact]
    public void BuildSettings_Forwards_Temperature_And_MaxTokens_When_Set()
    {
        var settings = ChatGateway.BuildSettings(
            new ChatRequest { UserMessage = "hi", Temperature = 0.7, MaxTokens = 256 });

        settings.Temperature.ShouldBe(0.7);
        settings.MaxTokens.ShouldBe(256);
        settings.FunctionChoiceBehavior.ShouldNotBeNull();
    }

    [Fact]
    public async Task StreamAsync_Passes_NonNull_Kernel_To_ChatService()
    {
        var kernel = Kernel.CreateBuilder().Build();
        Kernel? captured = null;

        var chat = Substitute.For<IChatCompletionService>();
        chat.GetStreamingChatMessageContentsAsync(
                Arg.Any<ChatHistory>(),
                Arg.Any<PromptExecutionSettings?>(),
                Arg.Any<Kernel?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = call.Arg<Kernel?>();
                return ToAsync([Chunk("ok")], CancellationToken.None);
            });

        var gateway = new ChatGateway(chat, kernel);

        await foreach (var _ in gateway.StreamAsync(
            new ChatRequest { UserMessage = "hi" }, CancellationToken.None))
        {
        }

        captured.ShouldNotBeNull();
        captured.ShouldBeSameAs(kernel);
    }

    [Fact]
    public async Task StreamAsync_Passes_OpenAI_Settings_With_Auto_Behavior_To_ChatService()
    {
        PromptExecutionSettings? captured = null;

        var chat = Substitute.For<IChatCompletionService>();
        chat.GetStreamingChatMessageContentsAsync(
                Arg.Any<ChatHistory>(),
                Arg.Any<PromptExecutionSettings?>(),
                Arg.Any<Kernel?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = call.Arg<PromptExecutionSettings?>();
                return ToAsync([Chunk("ok")], CancellationToken.None);
            });

        var gateway = new ChatGateway(chat, Kernel.CreateBuilder().Build());

        await foreach (var _ in gateway.StreamAsync(
            new ChatRequest { UserMessage = "hi" }, CancellationToken.None))
        {
        }

        var openAi = captured.ShouldBeOfType<OpenAIPromptExecutionSettings>();
        openAi.FunctionChoiceBehavior.ShouldNotBeNull();
    }
}
