using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.Ollama;

namespace Scout.Llm.Tests.Providers.Ollama;

/// <summary>
/// The model must see the whole prompt: every chat request to Ollama goes to its native chat endpoint and asks for the
/// configured context length, 16384 tokens unless <c>Scout:Llm:Ollama:Chat:ContextLength</c> says otherwise, so the
/// server never loads the model with its own smaller default and cuts the prompt without saying so.
/// </summary>
public sealed class OllamaContextLengthTests
{
    private const string Marker = "MARKER-7421";
    private const string Question = "Question: what is the code stated in the very first line?";

    private static readonly string LongPrompt = BuildLongPrompt();

    private int _probeCalls;

    [Theory]
    [InlineData("kernel service, streamed", "llama3.1")]
    [InlineData("kernel service, one reply", "llama3.1")]
    [InlineData("own-model service", "other-model")]
    public async Task A_Prompt_Longer_Than_The_Servers_Default_Context_Is_Sent_Whole_With_The_Context_Length_It_Needs(
        string service, string model)
    {
        var stub = new OllamaApiStub((_, request) => OllamaApiStub.Answer(request, model, Marker));
        using var http = new HttpClient(stub);
        var factory = new OllamaLlmProviderFactory(Options.Create(new LlmOptions()), http);
        var history = new ChatHistory();
        history.AddUserMessage(LongPrompt);
        string? reply = null;

        var failure = await Record.ExceptionAsync(async () => reply = service switch
        {
            "kernel service, streamed" => await Streamed(KernelChat(factory), history),
            "kernel service, one reply" => (await KernelChat(factory).GetChatMessageContentAsync(history)).Content,
            _ => (await factory.CreateChatCompletionService("other-model").GetChatMessageContentAsync(history)).Content,
        });

        var sent = stub.Requests.ShouldHaveSingleItem();
        sent.Uri.AbsolutePath.ShouldBe("/api/chat");
        sent.Body["model"]!.GetValue<string>().ShouldBe(model);
        sent.ContextLength.ShouldBe(16384);
        var content = sent.Body["messages"]!.AsArray().ShouldHaveSingleItem()!["content"]!.GetValue<string>();
        content.ShouldStartWith(Marker);
        content.ShouldEndWith(Question);
        content.ShouldBe(LongPrompt);
        failure.ShouldBeNull();
        reply.ShouldBe(Marker);
    }

    [Fact]
    public async Task Every_Model_Call_Of_A_Function_Round_Trip_Asks_For_The_Same_Context_Length()
    {
        var stub = new OllamaApiStub((call, _) => OllamaApiStub.Lines(call == 1
            ? OllamaApiStub.ToolCallLines("llama3.1", "Probe_read", new JsonObject { ["topic"] = "context" })
            : OllamaApiStub.TextLines("llama3.1", ["The probe said ", "hello."])));
        using var provider = Compose(stub, contextLength: null);
        string? reply = null;

        var failure = await Record.ExceptionAsync(async () => reply = await provider.GetRequiredService<IChatGateway>()
            .CompleteAsync(new ChatRequest { UserMessage = "what does the probe say?", Functions = ["Probe.read"] }, CancellationToken.None));

        stub.Requests.Count.ShouldBe(2);
        stub.Requests.ShouldAllBe(request => request.Uri.AbsolutePath == "/api/chat");
        stub.Requests.Select(request => request.ContextLength).ShouldBe([16384, 16384]);
        _probeCalls.ShouldBe(1);
        stub.Requests[1].Messages.ShouldContain("probe result for context");
        failure.ShouldBeNull();
        reply.ShouldBe("The probe said hello.");
    }

    [Fact]
    public async Task The_Context_Length_Is_Read_From_Scout_Llm_Ollama_Chat_ContextLength()
    {
        var stub = new OllamaApiStub((_, request) => OllamaApiStub.Answer(request, "llama3.1", "1. Good morning."));
        using var provider = Compose(stub, contextLength: "32768");
        string? reply = null;
        string? translation = null;

        var failure = await Record.ExceptionAsync(async () =>
        {
            reply = await provider.GetRequiredService<IChatGateway>()
                .CompleteAsync(new ChatRequest { UserMessage = "hello" }, CancellationToken.None);
            translation = await provider.GetRequiredService<ILiveTranslator>()
                .TranslateAsync("Bom dia.", [], "en", CancellationToken.None);
        });

        stub.Requests.Count.ShouldBe(2);
        stub.Requests.ShouldAllBe(request => request.Uri.AbsolutePath == "/api/chat");
        stub.Requests.Select(request => request.ContextLength).ShouldBe([32768, 32768]);
        failure.ShouldBeNull();
        reply.ShouldBe("1. Good morning.");
        translation.ShouldBe("Good morning.");
    }

    private static IChatCompletionService KernelChat(OllamaLlmProviderFactory factory)
    {
        var builder = Kernel.CreateBuilder();
        factory.AddToKernel(builder);
        return builder.Build().GetRequiredService<IChatCompletionService>();
    }

    private static async Task<string> Streamed(IChatCompletionService chat, ChatHistory history)
    {
        var text = new StringBuilder();
        await foreach (var update in chat.GetStreamingChatMessageContentsAsync(history))
        {
            text.Append(update.Content);
        }

        return text.ToString();
    }

    private ServiceProvider Compose(OllamaApiStub stub, string? contextLength)
    {
        var settings = new Dictionary<string, string?> { ["Scout:Llm:Provider"] = "Ollama" };
        if (contextLength is not null)
        {
            settings["Scout:Llm:Ollama:Chat:ContextLength"] = contextLength;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection().AddScoutLlm(configuration);
        services.AddSingleton<ILlmProviderFactory>(sp => new OllamaLlmProviderFactory(
            sp.GetRequiredService<IOptions<LlmOptions>>(), new HttpClient(stub)));
        services.AddSingleton(KernelPluginFactory.CreateFromFunctions(
            "Probe",
            [
                KernelFunctionFactory.CreateFromMethod(
                    (string topic) =>
                    {
                        _probeCalls++;
                        return $"probe result for {topic}";
                    },
                    "read"),
            ]));
        return services.BuildServiceProvider();
    }

    private static string BuildLongPrompt()
    {
        var prompt = new StringBuilder($"{Marker} is the code.\n");
        for (var line = 1; prompt.Length < 40_000; line++)
        {
            prompt.Append("Line ").Append(line).Append(": the warehouse log records one more item in its aisle.\n");
        }

        return prompt.Append(Question).ToString();
    }
}
