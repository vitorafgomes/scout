using System.ClientModel.Primitives;
using System.ComponentModel;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.ChatGptPlan;

namespace Scout.Llm.Tests.Providers.ChatGptPlan;

/// <summary>
/// The ChatGPT plan provider answers through the real ResponsesClient over a fake
/// HTTP handler, reached the way Scout reaches every model: Semantic Kernel and
/// the chat gateway.
/// </summary>
public sealed class ChatGptPlanProviderTests : IDisposable
{
    private const string Token = "plan-access-token-must-never-surface";
    private const string SignInMessage =
        "ChatGPT sign-in is missing or expired; sign in again on the AI Sources screen.";

    private readonly string _dir = Directory.CreateTempSubdirectory("scout-chatgpt-plan-").FullName;

    private string TokenFile => Path.Combine(_dir, "token.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void WriteToken(string token, DateTimeOffset expiresAt)
        => File.WriteAllText(
            TokenFile,
            $$"""{"access_token":"{{token}}","expires_at":"{{expiresAt.UtcDateTime:O}}"}""");

    private ChatGptPlanLlmProviderFactory Factory(HttpMessageHandler handler)
        => new(
            Options.Create(new LlmOptions
            {
                Provider = "ChatGptPlan",
                ChatGptPlan = new ChatGptPlanOptions { TokenFile = TokenFile },
            }),
            new HttpClient(handler));

    private ChatGateway Gateway(HttpMessageHandler handler, params KernelPlugin[] plugins)
    {
        var builder = Kernel.CreateBuilder();
        Factory(handler).AddToKernel(builder);
        foreach (var plugin in plugins)
        {
            builder.Plugins.Add(plugin);
        }

        var kernel = builder.Build();
        return new ChatGateway(kernel.GetRequiredService<IChatCompletionService>(), kernel);
    }

    private static Task<string> Ask(IChatGateway gateway, string message = "hello")
        => gateway.CompleteAsync(new ChatRequest { UserMessage = message }, CancellationToken.None);

    private static IEnumerable<JsonObject> InputItems(RecordedRequest request)
        => request.Body["input"]!.AsArray().Select(item => item!.AsObject());

    [Fact]
    public async Task Request_Goes_To_Responses_Api_With_Plan_Token_And_Accepted_Shape()
    {
        WriteToken(Token, DateTimeOffset.UtcNow.AddHours(1));
        var api = ResponsesApiStub.Answering("pong");
        var gateway = Gateway(api);

        var answer = await gateway.CompleteAsync(
            new ChatRequest
            {
                UserMessage = "ping",
                Persona = "You are Scout.",
                SystemPrompt = "Answer briefly.",
                History = [new ConversationTurn(ConversationRole.User, "earlier"), new ConversationTurn(ConversationRole.Assistant, "noted")],
                Temperature = 0.2,
                MaxTokens = 64,
            },
            CancellationToken.None);

        answer.ShouldBe("pong");
        var request = api.Requests.ShouldHaveSingleItem();
        request.Path.ShouldBe("/v1/responses");
        request.Authorization.ShouldBe($"Bearer {Token}");
        ((bool?)request.Body["store"]).ShouldBe(false);
        ((bool?)request.Body["stream"]).ShouldBe(true);
        request.Body.ContainsKey("temperature").ShouldBeFalse();
        request.Body.ContainsKey("max_output_tokens").ShouldBeFalse();
        request.Body["instructions"]!.GetValue<string>().ShouldBe("You are Scout.\n\nAnswer briefly.");
        InputItems(request).ShouldNotContain(item => item["role"] != null
            && (item["role"]!.GetValue<string>() == "system" || item["role"]!.GetValue<string>() == "developer"));
    }

    [Fact]
    public async Task Agent_Path_Sends_Agent_And_Request_Instructions_As_Instructions_Not_System_Messages()
    {
        WriteToken(Token, DateTimeOffset.UtcNow.AddHours(1));
        var api = ResponsesApiStub.Answering("pong");
        var builder = Kernel.CreateBuilder();
        Factory(api).AddToKernel(builder);
        var kernel = builder.Build();
        var agent = new ChatCompletionAgent
        {
            Name = "Project Expert",
            Instructions = "Ground answers in project context.",
            Kernel = kernel,
        };
        var gateway = new ChatGateway(
            kernel.GetRequiredService<IChatCompletionService>(), kernel, agent: agent);

        var answer = await gateway.CompleteAsync(
            new ChatRequest { UserMessage = "ping", SystemPrompt = "Answer briefly." },
            CancellationToken.None);

        answer.ShouldBe("pong");
        var request = api.Requests.ShouldHaveSingleItem();
        ((bool?)request.Body["store"]).ShouldBe(false);
        var instructions = request.Body["instructions"]!.GetValue<string>();
        instructions.ShouldContain("Ground answers in project context.");
        instructions.ShouldContain("Answer briefly.");
        InputItems(request).ShouldNotContain(item => item["role"] != null
            && (item["role"]!.GetValue<string>() == "system" || item["role"]!.GetValue<string>() == "developer"));
    }

    [Fact]
    public async Task NonStreaming_Call_Is_Sent_As_A_Stream()
    {
        WriteToken(Token, DateTimeOffset.UtcNow.AddHours(1));
        var api = new ResponsesApiStub((_, request) => (bool?)request.Body["stream"] == true
            ? ResponsesApiStub.Sse(ResponsesApiStub.TextEvents("pong"))
            : ResponsesApiStub.Error(
                HttpStatusCode.BadRequest,
                """{"error":{"message":"Stream must be set to true","type":"invalid_request_error","param":"stream","code":null}}"""));
        var builder = Kernel.CreateBuilder();
        Factory(api).AddToKernel(builder);
        var chat = builder.Build().GetRequiredService<IChatCompletionService>();

        ChatMessageContent? reply = null;
        await Should.NotThrowAsync(async () =>
            reply = await chat.GetChatMessageContentAsync(new ChatHistory("You are Scout.")));

        reply.ShouldNotBeNull().Content.ShouldBe("pong");
    }

    [Fact]
    public async Task Rotated_Token_File_Is_Used_On_The_Next_Request()
    {
        WriteToken("first-token", DateTimeOffset.UtcNow.AddHours(1));
        var api = ResponsesApiStub.Answering("ok");
        var gateway = Gateway(api);
        await Ask(gateway);

        WriteToken("rotated-token", DateTimeOffset.UtcNow.AddHours(2));
        File.SetLastWriteTimeUtc(TokenFile, DateTime.UtcNow.AddMinutes(1));
        await Ask(gateway);

        api.Requests.Select(r => r.Authorization).ShouldBe(["Bearer first-token", "Bearer rotated-token"]);
    }

    [Fact]
    public async Task Token_Close_To_Expiry_Is_Read_Again_Even_When_The_File_Time_Is_Unchanged()
    {
        WriteToken("expiring-token", DateTimeOffset.UtcNow.AddSeconds(30));
        var writtenAt = File.GetLastWriteTimeUtc(TokenFile);
        var api = ResponsesApiStub.Answering("ok");
        var gateway = Gateway(api);
        await Ask(gateway);

        WriteToken("refreshed-token", DateTimeOffset.UtcNow.AddHours(1));
        File.SetLastWriteTimeUtc(TokenFile, writtenAt);
        await Ask(gateway);

        api.Requests.Select(r => r.Authorization).ShouldBe(["Bearer expiring-token", "Bearer refreshed-token"]);
    }

    public static TheoryData<string, string?> BrokenSignIns() => new()
    {
        { "missing", null },
        { "expired", $$"""{"access_token":"{{Token}}","expires_at":"2020-01-01T00:00:00Z"}""" },
        { "unreadable", $"not json {Token}" },
        { "no expiry", $$"""{"access_token":"{{Token}}"}""" },
    };

    [Theory]
    [MemberData(nameof(BrokenSignIns))]
    public async Task Missing_Or_Expired_Sign_In_Fails_With_Clear_Message_And_No_Secret(
        string _, string? fileContents)
    {
        if (fileContents is not null)
        {
            File.WriteAllText(TokenFile, fileContents);
        }

        var api = ResponsesApiStub.Answering("never");
        var gateway = Gateway(api);

        var ex = await Should.ThrowAsync<LlmSignInRequiredException>(() => Ask(gateway));

        ex.Message.ShouldBe(SignInMessage);
        ex.ToString().ShouldNotContain(Token);
        ex.ToString().ShouldNotContain("not json");
        api.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Rejected_Plan_User_Fails_With_Clear_Message_And_No_Secret()
    {
        WriteToken(Token, DateTimeOffset.UtcNow.AddHours(1));
        var api = new ResponsesApiStub((_, _) => ResponsesApiStub.Error(
            HttpStatusCode.Unauthorized,
            """{"error":{"message":"This user cannot use plan sharing.","type":"invalid_request_error","param":null,"code":"subscription_sharing_invalid_user"}}"""));
        var gateway = Gateway(api);

        var ex = await Should.ThrowAsync<LlmSignInRequiredException>(() => Ask(gateway));

        ex.Message.ShouldBe(SignInMessage);
        ex.ToString().ShouldNotContain(Token);
        api.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Plan_Usage_Limit_Surfaces_As_Quota_Exceeded()
    {
        WriteToken(Token, DateTimeOffset.UtcNow.AddHours(1));
        var api = new ResponsesApiStub((_, _) => ResponsesApiStub.Error(
            HttpStatusCode.TooManyRequests,
            """{"error":{"message":"You have reached your ChatGPT plan usage limit.","type":"usage_limit","param":null,"code":"subscription_sharing_usage_limit_exceeded"}}"""));
        var gateway = Gateway(api);

        var ex = await Should.ThrowAsync<LlmQuotaExceededException>(() => Ask(gateway));

        ex.Code.ShouldBe("subscription_sharing_usage_limit_exceeded");
        ex.ToString().ShouldNotContain(Token);
        api.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Kernel_Function_Called_By_The_Model_Runs_And_Its_Result_Is_Sent_Back()
    {
        WriteToken(Token, DateTimeOffset.UtcNow.AddHours(1));
        var clock = new ClockPlugin();
        var api = new ResponsesApiStub((turn, request) => turn == 1
            ? ResponsesApiStub.Sse(ResponsesApiStub.FunctionCallEvents(
                request.Body["tools"]!.AsArray()[0]!["name"]!.GetValue<string>()))
            : ResponsesApiStub.Sse(ResponsesApiStub.TextEvents("It is noon.")));
        var gateway = Gateway(api, KernelPluginFactory.CreateFromObject(clock, "clock"));

        var answer = await Ask(gateway, "What time is it?");

        answer.ShouldBe("It is noon.");
        clock.Calls.ShouldBe(1);
        api.Requests.Count.ShouldBe(2);
        var followUp = api.Requests[1];
        followUp.Body.ContainsKey("previous_response_id").ShouldBeFalse();
        var types = InputItems(followUp).Select(item => item["type"]?.GetValue<string>()).ToList();
        types.ShouldContain("function_call");
        types.ShouldContain("function_call_output");
        InputItems(followUp).Single(item => item["type"]?.GetValue<string>() == "function_call_output")
            ["output"]!.ToJsonString().ShouldContain("12:00");
    }

    [Fact]
    public async Task Encrypted_Reasoning_Is_Requested_And_Replayed_With_The_Tool_Result()
    {
        WriteToken(Token, DateTimeOffset.UtcNow.AddHours(1));
        var api = new ResponsesApiStub((turn, request) => turn == 1
            ? ResponsesApiStub.Sse(ResponsesApiStub.ReasoningThenFunctionCallEvents(
                request.Body["tools"]!.AsArray()[0]!["name"]!.GetValue<string>(), "enc-reasoning-blob"))
            : ResponsesApiStub.Sse(ResponsesApiStub.TextEvents("It is noon.")));
        var gateway = Gateway(api, KernelPluginFactory.CreateFromObject(new ClockPlugin(), "clock"));

        var answer = await Ask(gateway, "What time is it?");

        answer.ShouldBe("It is noon.");
        api.Requests.Count.ShouldBe(2);
        api.Requests[0].Body["include"].ShouldNotBeNull().AsArray()
            .Select(value => value!.GetValue<string>())
            .ShouldContain("reasoning.encrypted_content");
        var replayed = InputItems(api.Requests[1])
            .Where(item => item["type"]?.GetValue<string>() == "reasoning")
            .ShouldHaveSingleItem();
        replayed["encrypted_content"].ShouldNotBeNull().GetValue<string>().ShouldBe("enc-reasoning-blob");
    }

    [Fact]
    public async Task Transient_503_Is_Retried_And_The_Answer_Arrives()
    {
        WriteToken(Token, DateTimeOffset.UtcNow.AddHours(1));
        var api = new ResponsesApiStub((turn, _) => turn == 1
            ? ResponsesApiStub.Error(
                HttpStatusCode.ServiceUnavailable,
                """{"error":{"message":"Service unavailable.","type":"server_error","param":null,"code":null}}""")
            : ResponsesApiStub.Sse(ResponsesApiStub.TextEvents("recovered")));
        var gateway = Gateway(api);

        string? answer = null;
        await Should.NotThrowAsync(async () => answer = await Ask(gateway));

        answer.ShouldBe("recovered");
        api.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Token_File_Locked_During_Replace_Falls_Back_To_The_Unexpired_Cached_Token()
    {
        WriteToken("cached-token", DateTimeOffset.UtcNow.AddSeconds(30));
        var reads = 0;
        var policy = new ChatGptPlanTokenPolicy(
            TokenFile,
            path => ++reads == 1
                ? File.ReadAllText(path)
                : throw new IOException("The process cannot access the file because it is being used by another process."));
        var api = ResponsesApiStub.Answering("ok");
        var pipeline = ClientPipeline.Create(
            new ClientPipelineOptions
            {
                Transport = new HttpClientPipelineTransport(new HttpClient(api)),
                RetryPolicy = new ClientRetryPolicy(0),
            },
            perCallPolicies: [],
            perTryPolicies: [policy],
            beforeTransportPolicies: []);

        await Send(pipeline);
        await Should.NotThrowAsync(() => Send(pipeline));

        reads.ShouldBe(2);
        api.Requests.Select(r => r.Authorization).ShouldBe(["Bearer cached-token", "Bearer cached-token"]);
    }

    private static async Task Send(ClientPipeline pipeline)
    {
        using var message = pipeline.CreateMessage();
        message.Request.Method = "POST";
        message.Request.Uri = new Uri("https://api.openai.com/v1/responses");
        await pipeline.SendAsync(message);
    }

    [Fact]
    public async Task Provider_ChatGptPlan_Answers_Through_The_Gateway_From_Composition()
    {
        WriteToken(Token, DateTimeOffset.UtcNow.AddHours(1));
        var api = ResponsesApiStub.Answering("composed");
        var services = new ServiceCollection().AddScoutLlm(Config(TokenFile));
        services.RemoveAll<ILlmProviderFactory>();
        services.AddSingleton<ILlmProviderFactory>(sp => new ChatGptPlanLlmProviderFactory(
            sp.GetRequiredService<IOptions<LlmOptions>>(), new HttpClient(api)));
        using var provider = services.BuildServiceProvider();

        var answer = await Ask(provider.GetRequiredService<IChatGateway>());

        answer.ShouldBe("composed");
        var request = api.Requests.ShouldHaveSingleItem();
        request.Authorization.ShouldBe($"Bearer {Token}");
        request.Body["model"]!.GetValue<string>().ShouldBe("gpt-5.6-luna");
    }

    [Fact]
    public async Task Provider_ChatGptPlan_Selects_The_Plan_Provider_In_The_Kernel()
    {
        using var provider = new ServiceCollection()
            .AddScoutLlm(Config(Path.Combine(_dir, "not-signed-in.json")))
            .BuildServiceProvider();

        provider.GetRequiredService<ILlmProviderFactory>().Name.ShouldBe("ChatGptPlan");
        var ex = await Should.ThrowAsync<LlmSignInRequiredException>(
            () => Ask(provider.GetRequiredService<IChatGateway>()));
        ex.Message.ShouldBe(SignInMessage);
    }

    [Fact]
    public async Task Embeddings_Fail_At_Once_As_Not_Supported_Without_A_Network_Call()
    {
        WriteToken(Token, DateTimeOffset.UtcNow.AddHours(1));
        var api = ResponsesApiStub.Answering("never");
        var factory = Factory(api);
        var builder = Kernel.CreateBuilder();
        factory.AddToKernel(builder);
        var fromKernel = builder.Build().GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

        var standalone = await Should.ThrowAsync<NotSupportedException>(
            () => factory.CreateEmbeddingGenerator().GenerateVectorAsync("hello"));
        var kernelOne = await Should.ThrowAsync<NotSupportedException>(
            () => fromKernel.GenerateVectorAsync("hello"));

        standalone.Message.ShouldBe("Embeddings are not available with the ChatGPT plan provider.");
        kernelOne.Message.ShouldBe(standalone.Message);
        api.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Factory_Requires_A_Token_File(string? tokenFile)
    {
        var options = new LlmOptions { Provider = "ChatGptPlan" };
        if (tokenFile is not null)
        {
            options.ChatGptPlan = new ChatGptPlanOptions { TokenFile = tokenFile };
        }

        var ex = Should.Throw<ArgumentException>(
            () => new ChatGptPlanLlmProviderFactory(Options.Create(options)));

        ex.Message.ShouldContain("TokenFile");
    }

    private static IConfiguration Config(string tokenFile) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Llm:Provider"] = "ChatGptPlan",
                ["Scout:Llm:ChatGptPlan:TokenFile"] = tokenFile,
            })
            .Build();

    private sealed class ClockPlugin
    {
        public int Calls { get; private set; }

        [KernelFunction("get_time")]
        [Description("Returns the current time.")]
        public string GetTime()
        {
            Calls++;
            return "12:00";
        }
    }
}
