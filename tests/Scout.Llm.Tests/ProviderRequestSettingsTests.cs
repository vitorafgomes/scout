using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.Anthropic;
using Scout.Llm.Providers.AzureAIInference;
using Scout.Llm.Providers.AzureOpenAI;
using Scout.Llm.Providers.ChatGptPlan;
using Scout.Llm.Providers.Gemini;
using Scout.Llm.Providers.HuggingFace;
using Scout.Llm.Providers.Mistral;
using Scout.Llm.Providers.Ollama;
using Scout.Llm.Providers.OpenAI;
using Scout.Llm.Providers.OpenRouter;
using Scout.Llm.Tests.Providers.Anthropic;
using Scout.Llm.Tests.Providers.ChatGptPlan;
using Scout.Llm.Tests.Providers.Ollama;

namespace Scout.Llm.Tests;

/// <summary>
/// What each provider is sent for a turn's temperature, output limit and declared functions, and for a live
/// translation's 400-token cap, read from the request body the real connector writes over a recording handler, with
/// the provider composed through <c>AddScoutLlm</c>: every top-level key of the body, the value of every key that is
/// not the conversation itself, and the names of the tools offered.
/// </summary>
public sealed class ProviderRequestSettingsTests : IDisposable
{
    private static readonly HashSet<string> ConversationKeys =
        new(["messages", "contents", "input", "system", "systemInstruction", "instructions", "model", "tools"], StringComparer.Ordinal);

    private readonly string _dir = Directory.CreateTempSubdirectory("scout-request-settings-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [MemberData(nameof(Turns))]
    public async Task A_Turn_Sends_Each_Provider_Exactly_The_Temperature_The_Output_Limit_And_The_Tools_Its_Connector_Sends(
        string provider, string path, string expected)
    {
        var recorder = new Recorder(() => Streamed(provider));
        using var services = Build(provider, recorder, agent: path.StartsWith("agent", StringComparison.Ordinal));
        var request = path.EndsWith("bare", StringComparison.Ordinal)
            ? new ChatRequest { UserMessage = "hello" }
            : new ChatRequest { UserMessage = "hello", Temperature = 0.25, MaxTokens = 256, Functions = ["Studio.lookup"] };

        await services.GetRequiredService<IChatGateway>().CompleteAsync(request, CancellationToken.None);

        Wire(recorder.Bodies[0]).ShouldBe(expected);
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public async Task A_Live_Translation_Sends_Each_Provider_Exactly_The_400_Token_Cap_And_No_Tools_Its_Connector_Sends(
        string provider, string expected)
    {
        var recorder = new Recorder(() => Answered(provider));
        using var services = Build(provider, recorder, agent: false);

        await services.GetRequiredService<ILiveTranslator>().TranslateAsync("Bom dia.", [], "en", CancellationToken.None);

        Wire(recorder.Bodies.ShouldHaveSingleItem()).ShouldBe(expected);
    }

    public static TheoryData<string, string, string> Turns() => new()
    {
        { "OpenAI", "direct", """keys=max_completion_tokens,messages,model,stream,stream_options,temperature,tool_choice,tools | max_completion_tokens=256 stream=true stream_options={"include_usage":true} temperature=0.25 tool_choice="auto" | tools=Studio-lookup""" },
        { "OpenAI", "direct-bare", """keys=messages,model,stream,stream_options | stream=true stream_options={"include_usage":true} | tools=absent""" },
        { "OpenAI", "agent", """keys=messages,model,stream,stream_options,tool_choice,tools | stream=true stream_options={"include_usage":true} tool_choice="auto" | tools=Studio-lookup""" },
        { "OpenAI", "agent-bare", """keys=messages,model,stream,stream_options | stream=true stream_options={"include_usage":true} | tools=absent""" },
        { "OpenRouter", "direct", """keys=max_completion_tokens,messages,model,stream,stream_options,temperature,tool_choice,tools | max_completion_tokens=256 stream=true stream_options={"include_usage":true} temperature=0.25 tool_choice="auto" | tools=Studio-lookup""" },
        { "OpenRouter", "direct-bare", """keys=messages,model,stream,stream_options | stream=true stream_options={"include_usage":true} | tools=absent""" },
        { "OpenRouter", "agent", """keys=messages,model,stream,stream_options,tool_choice,tools | stream=true stream_options={"include_usage":true} tool_choice="auto" | tools=Studio-lookup""" },
        { "OpenRouter", "agent-bare", """keys=messages,model,stream,stream_options | stream=true stream_options={"include_usage":true} | tools=absent""" },
        { "Ollama", "direct", """keys=CustomHeaders,messages,model,options,stream,tools | CustomHeaders={} options={"num_ctx":16384,"temperature":0.25,"num_predict":256} stream=true | tools=Studio_lookup""" },
        { "Ollama", "direct-bare", """keys=CustomHeaders,messages,model,options,stream | CustomHeaders={} options={"num_ctx":16384} stream=true | tools=absent""" },
        { "Ollama", "agent", """keys=CustomHeaders,messages,model,options,stream,tools | CustomHeaders={} options={"num_ctx":16384} stream=true | tools=Studio_lookup""" },
        { "Ollama", "agent-bare", """keys=CustomHeaders,messages,model,options,stream | CustomHeaders={} options={"num_ctx":16384} stream=true | tools=absent""" },
        { "AzureOpenAI", "direct", """keys=max_tokens,messages,model,stream,stream_options,temperature,tool_choice,tools | max_tokens=256 stream=true stream_options={"include_usage":true} temperature=0.25 tool_choice="auto" | tools=Studio-lookup""" },
        { "AzureOpenAI", "direct-bare", """keys=messages,model,stream,stream_options | stream=true stream_options={"include_usage":true} | tools=absent""" },
        { "AzureOpenAI", "agent", """keys=messages,model,stream,stream_options,tool_choice,tools | stream=true stream_options={"include_usage":true} tool_choice="auto" | tools=Studio-lookup""" },
        { "AzureOpenAI", "agent-bare", """keys=messages,model,stream,stream_options | stream=true stream_options={"include_usage":true} | tools=absent""" },
        { "AzureAIInference", "direct", """keys=max_tokens,messages,model,stream,temperature,tool_choice,tools | max_tokens=256 stream=true temperature=0.25 tool_choice="auto" | tools=Studio_lookup""" },
        { "AzureAIInference", "direct-bare", """keys=messages,model,stream | stream=true | tools=absent""" },
        { "AzureAIInference", "agent", """keys=messages,model,stream,tool_choice,tools | stream=true tool_choice="auto" | tools=Studio_lookup""" },
        { "AzureAIInference", "agent-bare", """keys=messages,model,stream | stream=true | tools=absent""" },
        { "Mistral", "direct", """keys=max_completion_tokens,messages,model,stream,stream_options,temperature,tool_choice,tools | max_completion_tokens=256 stream=true stream_options={"include_usage":true} temperature=0.25 tool_choice="auto" | tools=Studio-lookup""" },
        { "Mistral", "direct-bare", """keys=messages,model,stream,stream_options | stream=true stream_options={"include_usage":true} | tools=absent""" },
        { "Mistral", "agent", """keys=messages,model,stream,stream_options,tool_choice,tools | stream=true stream_options={"include_usage":true} tool_choice="auto" | tools=Studio-lookup""" },
        { "Mistral", "agent-bare", """keys=messages,model,stream,stream_options | stream=true stream_options={"include_usage":true} | tools=absent""" },
        { "HuggingFace", "direct", """keys=max_completion_tokens,messages,model,stream,stream_options,temperature,tool_choice,tools | max_completion_tokens=256 stream=true stream_options={"include_usage":true} temperature=0.25 tool_choice="auto" | tools=Studio-lookup""" },
        { "HuggingFace", "direct-bare", """keys=messages,model,stream,stream_options | stream=true stream_options={"include_usage":true} | tools=absent""" },
        { "HuggingFace", "agent", """keys=messages,model,stream,stream_options,tool_choice,tools | stream=true stream_options={"include_usage":true} tool_choice="auto" | tools=Studio-lookup""" },
        { "HuggingFace", "agent-bare", """keys=messages,model,stream,stream_options | stream=true stream_options={"include_usage":true} | tools=absent""" },
        { "Gemini", "direct", """keys=contents,generationConfig,tools | generationConfig={} | tools=Studio_lookup""" },
        { "Gemini", "direct-bare", """keys=contents,generationConfig | generationConfig={} | tools=absent""" },
        { "Gemini", "agent", """keys=contents,generationConfig,systemInstruction,tools | generationConfig={} | tools=Studio_lookup""" },
        { "Gemini", "agent-bare", """keys=contents,generationConfig,systemInstruction | generationConfig={} | tools=absent""" },
        { "ChatGptPlan", "direct", """keys=include,input,model,store,stream,tool_choice,tools | include=["reasoning.encrypted_content"] store=false stream=true tool_choice="auto" | tools=Studio_lookup""" },
        { "ChatGptPlan", "direct-bare", """keys=include,input,model,store,stream | include=["reasoning.encrypted_content"] store=false stream=true | tools=absent""" },
        { "ChatGptPlan", "agent", """keys=include,input,instructions,model,store,stream,tool_choice,tools | include=["reasoning.encrypted_content"] store=false stream=true tool_choice="auto" | tools=Studio_lookup""" },
        { "ChatGptPlan", "agent-bare", """keys=include,input,instructions,model,store,stream | include=["reasoning.encrypted_content"] store=false stream=true | tools=absent""" },
        { "Anthropic", "direct", """keys=max_tokens,messages,model,stream,tool_choice,tools | max_tokens=256 stream=true tool_choice={"type":"auto"} | tools=Studio_lookup""" },
        { "Anthropic", "direct-bare", """keys=max_tokens,messages,model,stream | max_tokens=64000 stream=true | tools=absent""" },
        { "Anthropic", "agent", """keys=max_tokens,messages,model,stream,system,tool_choice,tools | max_tokens=64000 stream=true tool_choice={"type":"auto"} | tools=Studio_lookup""" },
        { "Anthropic", "agent-bare", """keys=max_tokens,messages,model,stream,system | max_tokens=64000 stream=true | tools=absent""" },
    };

    public static TheoryData<string, string> Translations() => new()
    {
        { "OpenAI", """keys=max_completion_tokens,messages,model | max_completion_tokens=400 | tools=absent""" },
        { "OpenRouter", """keys=max_completion_tokens,messages,model | max_completion_tokens=400 | tools=absent""" },
        { "Ollama", """keys=CustomHeaders,messages,model,options,stream | CustomHeaders={} options={"num_ctx":16384,"num_predict":400} stream=false | tools=absent""" },
        { "AzureOpenAI", """keys=max_tokens,messages,model | max_tokens=400 | tools=absent""" },
        { "AzureAIInference", """keys=max_tokens,messages,model | max_tokens=400 | tools=absent""" },
        { "Mistral", """keys=max_completion_tokens,messages,model | max_completion_tokens=400 | tools=absent""" },
        { "HuggingFace", """keys=max_completion_tokens,messages,model | max_completion_tokens=400 | tools=absent""" },
        { "Gemini", """keys=contents,generationConfig,systemInstruction | generationConfig={} | tools=absent""" },
        { "ChatGptPlan", """keys=include,input,instructions,model,store,stream | include=["reasoning.encrypted_content"] store=false stream=true | tools=absent""" },
        { "Anthropic", """keys=max_tokens,messages,model,system | max_tokens=400 | tools=absent""" },
    };

    private static string Wire(JsonObject body)
    {
        var keys = string.Join(",", body.Select(property => property.Key).Order(StringComparer.Ordinal));
        var values = body
            .Where(property => !ConversationKeys.Contains(property.Key))
            .OrderBy(property => property.Key, StringComparer.Ordinal)
            .Select(property => $"{property.Key}={property.Value?.ToJsonString() ?? "null"}");
        var tools = body["tools"] is { } offered ? string.Join(",", Names(offered)) : "absent";
        return $"keys={keys} | {string.Join(" ", values)} | tools={tools}";
    }

    private static IEnumerable<string> Names(JsonNode node) => node switch
    {
        JsonObject obj => obj.SelectMany(property => property.Key == "name" && property.Value is JsonValue value
            ? [value.ToJsonString().Trim('"')]
            : property.Value is null ? [] : Names(property.Value)),
        JsonArray array => array.SelectMany(item => item is null ? [] : Names(item)),
        _ => [],
    };

    private ServiceProvider Build(string provider, HttpMessageHandler handler, bool agent)
    {
        var tokenFile = Path.Combine(_dir, "token.json");
        File.WriteAllText(
            tokenFile,
            $$"""{"access_token":"plan-token","expires_at":"{{DateTimeOffset.UtcNow.AddHours(1).UtcDateTime:O}}"}""");
        var services = new ServiceCollection().AddScoutLlm(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Llm:Provider"] = provider,
                [$"Scout:Llm:{provider}:ApiKey"] = "provider-key",
                [$"Scout:Llm:{provider}:Chat:Model"] = "chat-model",
                [$"Scout:Llm:{provider}:Embeddings:Model"] = provider == "OpenRouter" ? null : "embed-model",
                ["Scout:Llm:AzureOpenAI:Endpoint"] = "https://my-resource.openai.azure.com",
                ["Scout:Llm:AzureOpenAI:Chat:Deployment"] = "chat-model",
                ["Scout:Llm:AzureOpenAI:Embeddings:Deployment"] = "embed-model",
                ["Scout:Llm:AzureAIInference:Endpoint"] = "https://my-resource.services.ai.azure.com/models",
                ["Scout:Llm:ChatGptPlan:TokenFile"] = tokenFile,
            })
            .Build());
        services.AddSingleton(KernelPluginFactory.CreateFromFunctions(
            "Studio",
            [
                KernelFunctionFactory.CreateFromMethod(() => "found", "lookup"),
                KernelFunctionFactory.CreateFromMethod(() => "archived", "archive"),
            ]));
        if (agent)
        {
            services.AddSingleton(sp => new ChatCompletionAgent
            {
                Name = "Project Expert",
                Instructions = "Ground answers in project context.",
                Kernel = sp.GetRequiredService<Kernel>(),
                Arguments = new KernelArguments(new OpenAIPromptExecutionSettings { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto() }),
            });
        }

        services.RemoveAll<ILlmProviderFactory>();
        services.AddSingleton<ILlmProviderFactory>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<LlmOptions>>();
            return provider switch
            {
                "OpenAI" => new OpenAILlmProviderFactory(options, new HttpClient(handler)),
                "OpenRouter" => new OpenRouterLlmProviderFactory(options, new HttpClient(handler)),
                "Ollama" => new OllamaLlmProviderFactory(options, new HttpClient(handler)),
                "AzureOpenAI" => new AzureOpenAILlmProviderFactory(
                    options, new HttpClient(handler) { BaseAddress = new Uri("https://my-resource.openai.azure.com/") }),
                "AzureAIInference" => new AzureAIInferenceLlmProviderFactory(
                    options, new HttpClient(handler) { BaseAddress = new Uri("https://my-resource.services.ai.azure.com/") }),
                "Mistral" => new MistralLlmProviderFactory(options, new HttpClient(handler)),
                "HuggingFace" => new HuggingFaceLlmProviderFactory(options, new HttpClient(handler)),
                "Gemini" => new GeminiLlmProviderFactory(options, new HttpClient(handler)),
                "ChatGptPlan" => new ChatGptPlanLlmProviderFactory(options, new HttpClient(handler)),
                "Anthropic" => new AnthropicLlmProviderFactory(options, new HttpClient(handler)),
                _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
            };
        });
        return services.BuildServiceProvider();
    }

    private static HttpResponseMessage Streamed(string provider) => provider switch
    {
        "Gemini" => Sse(["""{"candidates":[{"content":{"role":"model","parts":[{"text":"ok"}]},"finishReason":"STOP","index":0}]}"""]),
        "ChatGptPlan" => ResponsesApiStub.Sse(ResponsesApiStub.TextEvents("ok")),
        "Anthropic" => ClaudeApiStub.Sse(ClaudeApiStub.TextEvents("chat-model", ["ok"])),
        "Ollama" => OllamaApiStub.Lines(OllamaApiStub.TextLines("chat-model", ["ok"])),
        _ => Sse(
        [
            """{"id":"c1","object":"chat.completion.chunk","created":1,"model":"chat-model","choices":[{"index":0,"delta":{"role":"assistant","content":"ok"},"finish_reason":null}]}""",
            """{"id":"c1","object":"chat.completion.chunk","created":1,"model":"chat-model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
            "[DONE]",
        ]),
    };

    private static HttpResponseMessage Answered(string provider) => provider switch
    {
        "Gemini" => Json("""{"candidates":[{"content":{"parts":[{"text":"1. Good morning."}],"role":"model"},"finishReason":"STOP","index":0}]}"""),
        "ChatGptPlan" => ResponsesApiStub.Sse(ResponsesApiStub.TextEvents("1. Good morning.")),
        "Anthropic" => ClaudeApiStub.Message("chat-model", "1. Good morning."),
        "Ollama" => OllamaApiStub.Lines([OllamaApiStub.Done("chat-model", "1. Good morning.")]),
        _ => Json("""{"id":"c1","object":"chat.completion","created":1,"model":"chat-model","choices":[{"index":0,"message":{"role":"assistant","content":"1. Good morning."},"finish_reason":"stop"}]}"""),
    };

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Sse(IEnumerable<string> chunks)
        => new(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NetworkLikeStream(Encoding.UTF8.GetBytes(
                string.Concat(chunks.Select(chunk => $"data: {chunk}\n\n")))))
            {
                Headers = { { "Content-Type", "text/event-stream" } },
            },
        };

    private sealed class NetworkLikeStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class Recorder(Func<HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<JsonObject> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
            Bodies.Add(JsonNode.Parse(body)!.AsObject());
            return answer();
        }
    }
}
