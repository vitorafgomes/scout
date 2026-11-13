using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.ChatGptPlan;
using Scout.Llm.Tests.Providers.ChatGptPlan;

namespace Scout.Llm.Tests;

/// <summary>
/// How the end of a stream is read on the branches no other test reaches: a Responses API failure that carries no
/// code is named <c>unknown</c>, and the finish reason a connector puts in the metadata is the last one given.
/// </summary>
public sealed class ProviderStreamEndTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("scout-stream-end-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData(
        """{"type":"response.failed","sequence_number":1,"response":{"id":"resp_1","object":"response","created_at":1700000000,"status":"failed","error":null,"model":"gpt-5.6-sol","output":[],"parallel_tool_calls":true,"tool_choice":"auto","tools":[]}}""",
        "finish=failed ()")]
    [InlineData("""{"type":"error","sequence_number":1,"code":"","message":"secret-provider-detail","param":null}""", "finish=error ()")]
    [InlineData("""{"type":"error","sequence_number":1,"code":null,"message":"secret-provider-detail","param":null}""", "finish=error ()")]
    public async Task A_Responses_Failure_Without_A_Code_Throws_The_Error_Sentence_Naming_Unknown(string terminalEvent, string finish)
    {
        var logger = new RecordingLogger();
        var gateway = await PlanGateway(new ResponsesApiStub((_, _) => ResponsesApiStub.Sse([terminalEvent])), logger);

        var thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => gateway.CompleteAsync(new ChatRequest { UserMessage = "hello" }, CancellationToken.None));

        thrown.Message.ShouldBe("The AI provider ended the answer with an error (unknown).");
        logger.Warnings.ShouldHaveSingleItem().ShouldContain(finish + ",", Case.Sensitive);
    }

    [Fact]
    public async Task The_Finish_Reason_In_The_Metadata_Is_The_Last_One_Given_And_An_Update_Without_One_Keeps_It()
    {
        var logger = new RecordingLogger();
        var gateway = new ChatGateway(
            Yielding(Update("tool_calls"), Update("length"), Update(null)),
            options: Options.Create(new LlmOptions { Provider = "OpenAI" }),
            logger: logger);

        var reply = await gateway.CompleteAsync(new ChatRequest { UserMessage = "hello" }, CancellationToken.None);

        reply.ShouldBeEmpty();
        logger.Warnings.ShouldHaveSingleItem().ShouldContain("(updates=3, finish=length, functionCalls=none)", Case.Sensitive);
    }

    private async Task<ChatGateway> PlanGateway(ResponsesApiStub api, RecordingLogger logger)
    {
        var tokenFile = Path.Combine(_dir, "token.json");
        await File.WriteAllTextAsync(
            tokenFile,
            $$"""{"access_token":"plan-token","expires_at":"{{DateTimeOffset.UtcNow.AddHours(1).UtcDateTime:O}}"}""");
        var options = Options.Create(new LlmOptions
        {
            Provider = "ChatGptPlan",
            ChatGptPlan = new ChatGptPlanOptions { TokenFile = tokenFile, Chat = new ChatModelOptions { Model = "gpt-5.6-sol" } },
        });
        var builder = Kernel.CreateBuilder();
        new ChatGptPlanLlmProviderFactory(options, new HttpClient(api)).AddToKernel(builder);
        var kernel = builder.Build();
        return new ChatGateway(kernel.GetRequiredService<IChatCompletionService>(), kernel, options, logger);
    }

    private static StreamingChatMessageContent Update(string? finishReason)
        => new(
            AuthorRole.Assistant,
            null,
            metadata: finishReason is null ? null : new Dictionary<string, object?> { ["FinishReason"] = finishReason });

    private static IChatCompletionService Yielding(params StreamingChatMessageContent[] updates)
    {
        var chat = Substitute.For<IChatCompletionService>();
        chat.GetStreamingChatMessageContentsAsync(
                Arg.Any<ChatHistory>(),
                Arg.Any<PromptExecutionSettings?>(),
                Arg.Any<Kernel?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => ToAsync(updates, CancellationToken.None));
        return chat;
    }

    private static async IAsyncEnumerable<StreamingChatMessageContent> ToAsync(
        IEnumerable<StreamingChatMessageContent> updates,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var update in updates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return update;
        }
    }

    private sealed class RecordingLogger : ILogger<ChatGateway>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
