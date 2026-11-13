using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;
using Scout.Llm.Guard;
using Scout.Llm.Providers.ChatGptPlan;
using Scout.Llm.Providers.Gemini;
using Scout.Llm.Providers.Ollama;
using Scout.Llm.Providers.OpenAI;
using Scout.Llm.Providers.OpenRouter;
using Scout.Llm.Tests.Guard;
using Scout.Llm.Tests.Providers.ChatGptPlan;
using Scout.Llm.Tests.Providers.Ollama;

namespace Scout.Llm.Tests;

/// <summary>
/// The live translator against each provider the desktop offers, through <c>AddScoutLlm</c> and the provider's real
/// connector over a recording handler: which model the translation request names, and which the chat request names.
/// </summary>
public sealed partial class TranslationModelTests : IDisposable
{
    private const string ChatModel = "chat-model";
    private const string TranslationModel = "translation-model";

    private readonly string _dir = Directory.CreateTempSubdirectory("scout-translation-model-").FullName;

    private string TokenFile => Path.Combine(_dir, "token.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [GeneratedRegex("/models/([^/:]+):")]
    private static partial Regex ModelInPath();

    private sealed record Sent(Uri Uri, JsonObject Body)
    {
        public string? Model
            => Body["model"]?.GetValue<string>()
                ?? (ModelInPath().Match(Uri.AbsolutePath) is { Success: true } path ? path.Groups[1].Value : null);
    }

    private sealed class Recorder(Func<HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Sent(request.RequestUri!, JsonNode.Parse(body)!.AsObject()));
            return answer();
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Recorder AnsweringFor(string provider) => new(() => provider switch
    {
        "Gemini" => Json(
            HttpStatusCode.OK,
            """{"candidates":[{"content":{"parts":[{"text":"1. Good morning."}],"role":"model"},"finishReason":"STOP","index":0}]}"""),
        "ChatGptPlan" => ResponsesApiStub.Sse(ResponsesApiStub.TextEvents("1. Good morning.")),
        "Ollama" => OllamaApiStub.Lines([OllamaApiStub.Done("m", "1. Good morning.")]),
        _ => Json(
            HttpStatusCode.OK,
            """{"id":"c1","object":"chat.completion","created":1,"model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"1. Good morning."},"finish_reason":"stop"}]}"""),
    });

    private ServiceProvider Build(
        string provider,
        string? translationModel,
        HttpMessageHandler handler,
        Action<IServiceCollection>? more = null,
        string? typeSafeKey = null)
    {
        File.WriteAllText(
            TokenFile,
            $$"""{"access_token":"plan-token","expires_at":"{{DateTimeOffset.UtcNow.AddHours(1).UtcDateTime:O}}"}""");
        var services = new ServiceCollection().AddScoutLlm(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Llm:Provider"] = provider,
                ["Scout:Llm:Translation:Model"] = translationModel,
                [$"Scout:Llm:{provider}:ApiKey"] = "provider-key",
                [$"Scout:Llm:{provider}:Chat:Model"] = ChatModel,
                ["Scout:Llm:ChatGptPlan:TokenFile"] = TokenFile,
                ["Scout:Guard:TypeSafe:ApiKey"] = typeSafeKey,
            })
            .Build());
        more?.Invoke(services);
        var http = new HttpClient(handler);
        services.RemoveAll<ILlmProviderFactory>();
        services.AddSingleton<ILlmProviderFactory>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<LlmOptions>>();
            return provider switch
            {
                "OpenAI" => new OpenAILlmProviderFactory(options, http),
                "Gemini" => new GeminiLlmProviderFactory(options, http),
                "Ollama" => new OllamaLlmProviderFactory(options, http),
                "ChatGptPlan" => new ChatGptPlanLlmProviderFactory(options, http),
                "OpenRouter" => new OpenRouterLlmProviderFactory(options, http),
                _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
            };
        });
        return services.BuildServiceProvider();
    }

    private static Task<string> TranslateAsync(IServiceProvider provider)
        => provider.GetRequiredService<ILiveTranslator>().TranslateAsync("Bom dia.", [], "en", CancellationToken.None);

    [Theory]
    [InlineData("OpenAI", null)]
    [InlineData("Gemini", null)]
    [InlineData("Ollama", null)]
    [InlineData("ChatGptPlan", null)]
    [InlineData("OpenAI", "")]
    [InlineData("Ollama", "  ")]
    public async Task Without_A_Translation_Model_The_Translation_Goes_To_The_Chat_Model(string provider, string? translationModel)
    {
        var recorder = AnsweringFor(provider);
        using var services = Build(provider, translationModel, recorder);

        var translation = await TranslateAsync(services);

        translation.ShouldBe("Good morning.");
        recorder.Requests.ShouldHaveSingleItem().Model.ShouldBe(
            ChatModel, "with no translation model chosen, the translation uses the model of the chat");
    }

    [Theory]
    [InlineData("OpenAI")]
    [InlineData("Gemini")]
    [InlineData("Ollama")]
    [InlineData("ChatGptPlan")]
    [InlineData("OpenRouter")]
    public async Task A_Translation_Model_Names_The_Translation_Request_And_The_Chat_Keeps_The_Chat_Model(string provider)
    {
        var recorder = AnsweringFor(provider);
        using var services = Build(provider, TranslationModel, recorder);
        var history = new ChatHistory();
        history.AddUserMessage("Good morning?");

        var translation = await TranslateAsync(services);
        await services.GetRequiredService<IChatCompletionService>().GetChatMessageContentAsync(history);

        translation.ShouldBe("Good morning.");
        recorder.Requests.Select(r => r.Model).ShouldBe(
            [TranslationModel, ChatModel],
            "the translation goes to the translation model chosen, and every chat still goes to the chat model");
    }

    [Fact]
    public void With_Provider_None_A_Translation_Model_Registers_No_Translator()
    {
        using var services = new ServiceCollection()
            .AddScoutLlm(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Scout:Llm:Provider"] = "None",
                    ["Scout:Llm:Translation:Model"] = TranslationModel,
                })
                .Build())
            .BuildServiceProvider();

        services.GetService<ILiveTranslator>().ShouldBeNull("with no provider in use nothing translates, whatever model was chosen");
    }

    [Fact]
    public async Task A_Translation_Model_Under_The_Guard_Never_Reaches_TypeSafe_And_Offers_No_Tools()
    {
        using var typeSafe = StubTypeSafeHandler.Answering(0.9);
        var recorder = AnsweringFor("Ollama");
        using var services = Build(
            "Ollama",
            TranslationModel,
            recorder,
            more: s =>
            {
                s.AddHttpClient<TypeSafeClient>().ConfigurePrimaryHttpMessageHandler(() => typeSafe);
                s.AddSingleton(KernelPluginFactory.CreateFromFunctions(
                    "Ops",
                    [KernelFunctionFactory.CreateFromMethod(() => "deleted", "DeleteCluster")]));
            },
            typeSafeKey: "ts-key");

        await TranslateAsync(services);

        typeSafe.Calls.ShouldBe(0, "no transcript line is sent to TypeSafe");
        var sent = recorder.Requests.ShouldHaveSingleItem();
        sent.Model.ShouldBe(TranslationModel);
        sent.Body.ContainsKey("tools").ShouldBeFalse("the translation model is offered no function");
        services.GetRequiredService<Kernel>().GetRequiredService<IChatCompletionService>()
            .ShouldBeOfType<ReadOnlyGuardChatCompletionService>("the kernel's chat service is still the guard");
    }

    [Fact]
    public async Task A_Translation_Model_On_The_Plan_Sends_No_Tools_And_No_Token_Cap()
    {
        var recorder = AnsweringFor("ChatGptPlan");
        using var services = Build("ChatGptPlan", TranslationModel, recorder);

        await TranslateAsync(services);

        var sent = recorder.Requests.ShouldHaveSingleItem();
        sent.Model.ShouldBe(TranslationModel);
        sent.Body.ContainsKey("tools").ShouldBeFalse();
        sent.Body.ContainsKey("max_output_tokens").ShouldBeFalse("the plan refuses a token cap");
    }

    [Fact]
    public async Task A_Translation_Model_The_Provider_Rejects_Fails_The_Line_Without_Falling_Back_To_The_Chat_Model()
    {
        var recorder = new Recorder(() => Json(
            HttpStatusCode.NotFound,
            """{"error":{"message":"The model `translation-model` does not exist","type":"invalid_request_error","code":"model_not_found"}}"""));
        using var services = Build("OpenAI", TranslationModel, recorder);

        var failure = await Should.ThrowAsync<LlmModelNotFoundException>(() => TranslateAsync(services));

        failure.Model.ShouldBe(TranslationModel, "the live translation queue says the translation model is not installed, by name");
        recorder.Requests.Select(r => r.Model).ShouldBe([TranslationModel], "the line is not sent again to the chat model");
    }
}
