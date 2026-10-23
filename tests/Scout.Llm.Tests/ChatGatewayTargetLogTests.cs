using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;

namespace Scout.Llm.Tests;

public sealed class ChatGatewayTargetLogTests
{
    [Fact]
    public async Task Ollama_Request_Log_Line_Is_Unchanged()
    {
        var options = new LlmOptions
        {
            Provider = "Ollama",
            Ollama = new OllamaOptions
            {
                Endpoint = "http://localhost:11434",
                Chat = new ChatModelOptions { Model = "llama3.1" },
            },
        };

        var message = await LoggedRequestLine(options);

        message.ShouldBe(
            "LLM request -> provider=Ollama endpoint=http://localhost:11434 model=llama3.1 (inputChars=5).");
    }

    [Fact]
    public async Task OpenAI_Request_Log_Line_Describes_OpenAI_And_Never_The_Key()
    {
        var options = new LlmOptions
        {
            Provider = "OpenAI",
            OpenAI = new OpenAIOptions
            {
                ApiKey = "sk-must-not-log",
                Chat = new ChatModelOptions { Model = "gpt-4o-mini" },
            },
        };

        var message = await LoggedRequestLine(options);

        message.ShouldContain("provider=OpenAI");
        message.ShouldContain("endpoint=https://api.openai.com/v1");
        message.ShouldContain("model=gpt-4o-mini");
        message.ShouldNotContain("sk-must-not-log");
        message.ShouldNotContain("11434");
    }

    [Fact]
    public async Task OpenAI_Request_Log_Line_Uses_The_Configured_Endpoint()
    {
        var options = new LlmOptions
        {
            Provider = "OpenAI",
            OpenAI = new OpenAIOptions
            {
                ApiKey = "sk-must-not-log",
                Endpoint = "https://llm.example.test/v1",
                Chat = new ChatModelOptions { Model = "gpt-4o" },
            },
        };

        var message = await LoggedRequestLine(options);

        message.ShouldContain("endpoint=https://llm.example.test/v1");
    }

    [Fact]
    public async Task ChatGptPlan_Request_Log_Line_Describes_The_Plan_And_Never_The_Token_File()
    {
        var options = new LlmOptions
        {
            Provider = "ChatGptPlan",
            ChatGptPlan = new ChatGptPlanOptions
            {
                TokenFile = "/home/user/.config/scout/chatgpt-token.json",
                Chat = new ChatModelOptions { Model = "gpt-5.6-luna" },
            },
        };

        var message = await LoggedRequestLine(options);

        message.ShouldBe(
            "LLM request -> provider=ChatGptPlan endpoint=https://api.openai.com/v1/responses model=gpt-5.6-luna (inputChars=5).");
        message.ShouldNotContain("chatgpt-token.json");
    }

    [Fact]
    public async Task Gemini_Request_Log_Line_Describes_Gemini_And_Never_The_Key()
    {
        var options = new LlmOptions
        {
            Provider = "Gemini",
            Gemini = new GeminiOptions
            {
                ApiKey = "gemini-key-must-not-log",
                Chat = new ChatModelOptions { Model = "gemini-2.5-flash" },
            },
        };

        var message = await LoggedRequestLine(options);

        message.ShouldContain("provider=Gemini");
        message.ShouldContain("endpoint=https://generativelanguage.googleapis.com");
        message.ShouldContain("model=gemini-2.5-flash");
        message.ShouldNotContain("gemini-key-must-not-log");
        message.ShouldNotContain("11434");
    }

    [Fact]
    public async Task Other_Provider_Request_Log_Line_Names_The_Provider_Without_Ollama_Fields()
    {
        var options = new LlmOptions { Provider = "Mistral" };

        var message = await LoggedRequestLine(options);

        message.ShouldContain("provider=Mistral endpoint=unknown model=unknown");
    }

    private static async Task<string> LoggedRequestLine(LlmOptions options)
    {
        var chat = Substitute.For<IChatCompletionService>();
        chat.GetStreamingChatMessageContentsAsync(
                Arg.Any<ChatHistory>(),
                Arg.Any<PromptExecutionSettings?>(),
                Arg.Any<Kernel?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Chunks(CancellationToken.None));
        var logger = new RecordingLogger();
        var gateway = new ChatGateway(chat, options: Options.Create(options), logger: logger);

        await foreach (var _ in gateway.StreamAsync(
            new ChatRequest { UserMessage = "hello" }, CancellationToken.None))
        {
        }

        return logger.Messages.Single(m => m.StartsWith("LLM request", StringComparison.Ordinal));
    }

    private static async IAsyncEnumerable<StreamingChatMessageContent> Chunks(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new StreamingChatMessageContent(AuthorRole.Assistant, "ok");
    }

    private sealed class RecordingLogger : ILogger<ChatGateway>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
