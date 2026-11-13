using System.Runtime.CompilerServices;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Scout.Llm.Abstractions;

namespace Scout.Llm.Tests;

/// <summary>
/// Verifies the gateway opts the chat call into automatic function-calling when the
/// turn declares a function and hands the chat-completion service a per-turn kernel
/// holding only those functions, so the LLM can invoke exactly the declared set.
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
    public void BuildSettings_Carries_FunctionChoiceBehavior_Auto_Only_When_The_Turn_Offers_Functions()
    {
        var offered = Scout.Llm.Providers.ProviderSettings.BuildSettings(new ChatRequest { UserMessage = "hi" }, offerFunctions: true);
        var none = Scout.Llm.Providers.ProviderSettings.BuildSettings(new ChatRequest { UserMessage = "hi" }, offerFunctions: false);

        offered.FunctionChoiceBehavior.ShouldNotBeNull().GetType().ShouldBe(FunctionChoiceBehavior.Auto().GetType());
        none.FunctionChoiceBehavior.ShouldBeNull();
    }

    [Fact]
    public void BuildSettings_Forwards_Temperature_And_MaxTokens_When_Set()
    {
        var settings = Scout.Llm.Providers.ProviderSettings.BuildSettings(
            new ChatRequest { UserMessage = "hi", Temperature = 0.7, MaxTokens = 256 }, offerFunctions: true);

        settings.Temperature.ShouldBe(0.7);
        settings.MaxTokens.ShouldBe(256);
        settings.FunctionChoiceBehavior.ShouldNotBeNull();
    }

    [Fact]
    public async Task StreamAsync_Passes_The_ChatService_A_Turn_Kernel_Holding_Only_The_Declared_Functions()
    {
        var kernel = Kernel.CreateBuilder().Build();
        kernel.Plugins.AddFromFunctions("GitHub",
        [
            KernelFunctionFactory.CreateFromMethod(() => "issue", "get_issue"),
            KernelFunctionFactory.CreateFromMethod(() => "repos", "list_repositories"),
        ]);
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
            new ChatRequest { UserMessage = "hi", Functions = ["GitHub.get_issue"] }, CancellationToken.None))
        {
        }

        captured.ShouldNotBeNull().ShouldNotBeSameAs(kernel);
        captured.Plugins.GetFunctionsMetadata().Select(f => $"{f.PluginName}.{f.Name}").ShouldBe(["GitHub.get_issue"]);
        kernel.Plugins.GetFunctionsMetadata().Count.ShouldBe(2);
    }

    [Fact]
    public async Task StreamAsync_Passes_OpenAI_Settings_With_Auto_Behavior_When_A_Declared_Function_Exists()
    {
        PromptExecutionSettings? captured = null;
        var kernel = Kernel.CreateBuilder().Build();
        kernel.Plugins.AddFromFunctions("GitHub", [KernelFunctionFactory.CreateFromMethod(() => "issue", "get_issue")]);

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

        var gateway = new ChatGateway(chat, kernel);

        await foreach (var _ in gateway.StreamAsync(
            new ChatRequest { UserMessage = "hi", Functions = ["GitHub.get_issue"] }, CancellationToken.None))
        {
        }

        var openAi = captured.ShouldBeOfType<OpenAIPromptExecutionSettings>();
        openAi.FunctionChoiceBehavior.ShouldNotBeNull();
    }
}
