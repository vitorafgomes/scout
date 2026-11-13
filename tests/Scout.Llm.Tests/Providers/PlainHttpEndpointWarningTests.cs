using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Services;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.AzureAIInference;
using Scout.Llm.Providers.AzureOpenAI;
using Scout.Llm.Providers.HuggingFace;
using Scout.Llm.Providers.Mistral;
using Scout.Llm.Providers.Ollama;
using Scout.Llm.Providers.OpenAI;
using Scout.Llm.Providers.OpenRouter;

namespace Scout.Llm.Tests.Providers;

public sealed class PlainHttpEndpointWarningTests
{
    private const string Key = "sk-must-never-be-logged-4711";

    private static readonly string[] KeyedProviders = ["AzureAIInference", "AzureOpenAI", "HuggingFace", "Mistral", "OpenAI"];

    private static readonly string[] Leaks = [Key, "user", "pw", "secret", "path", "token", "abc", "8080"];

    public static TheoryData<string, string, string> NotThisMachineOverHttp()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var provider in KeyedProviders)
        {
            data.Add(provider, "http://user:pw@llm.example.test:8080/secret/path?token=abc", "llm.example.test");
            data.Add(provider, "http://localhost.example.test/secret/path?token=abc", "localhost.example.test");
            data.Add(provider, "http://10.0.0.7:8080/secret/path?token=abc", "10.0.0.7");
        }

        return data;
    }

    public static TheoryData<string, string> ThisMachineOrHttps()
    {
        var data = new TheoryData<string, string>();
        foreach (var provider in KeyedProviders)
        {
            data.Add(provider, "http://localhost:8080/secret/path?token=abc");
            data.Add(provider, "http://127.10.0.1:8080/secret/path?token=abc");
            data.Add(provider, "http://[::1]:8080/secret/path?token=abc");
            data.Add(provider, "https://user:pw@llm.example.test:8080/secret/path?token=abc");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(NotThisMachineOverHttp))]
    public void A_Key_Sent_Over_Plain_Http_To_Another_Host_Is_Warned_Once_And_The_Provider_Still_Works(
        string provider, string endpoint, string host)
    {
        var log = new List<Entry>();

        var factory = Build(provider, endpoint, log);

        var warning = log.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldBe(
            $"The {provider} provider sends its API key over plain http to {host}, which is not this machine; " +
            "an https endpoint keeps the key encrypted.");
        ChatModelOf(factory).ShouldBe(ChatModel(provider));
    }

    [Theory]
    [MemberData(nameof(NotThisMachineOverHttp))]
    public void The_Warning_Never_Carries_The_Key_Or_The_Url_User_Info_Path_Query_Or_Port(
        string provider, string endpoint, string host)
    {
        var log = new List<Entry>();

        Build(provider, endpoint, log);

        var warning = log.ShouldHaveSingleItem();
        warning.Message.ShouldContain(host);
        var everything = string.Join(" ", warning.State.Select(p => $"{p.Key}={p.Value}").Append(warning.Message));
        foreach (var leak in Leaks)
        {
            everything.ShouldNotContain(leak, Case.Insensitive);
        }
    }

    [Theory]
    [MemberData(nameof(ThisMachineOrHttps))]
    public void An_Https_Or_Loopback_Http_Endpoint_Logs_Nothing_And_The_Provider_Still_Works(
        string provider, string endpoint)
    {
        var log = new List<Entry>();

        var factory = Build(provider, endpoint, log);

        log.ShouldBeEmpty();
        ChatModelOf(factory).ShouldBe(ChatModel(provider));
    }

    [Theory]
    [InlineData("AzureAIInference", "Model")]
    [InlineData("AzureOpenAI", "Deployment")]
    [InlineData("HuggingFace", "Model")]
    [InlineData("Mistral", "Model")]
    [InlineData("OpenAI", "Model")]
    public void The_Host_Log_Receives_The_Warning_When_The_Provider_Is_Chosen_By_Configuration(
        string provider, string modelKey)
    {
        var log = new List<Entry>();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Llm:Provider"] = provider,
                [$"Scout:Llm:{provider}:ApiKey"] = Key,
                [$"Scout:Llm:{provider}:Endpoint"] = "http://llm.example.test/v1",
                [$"Scout:Llm:{provider}:Chat:{modelKey}"] = ChatModel(provider),
                [$"Scout:Llm:{provider}:Embeddings:{modelKey}"] = "an-embedding-model",
            })
            .Build();
        using var services = new ServiceCollection()
            .AddLogging(logging => logging.AddProvider(new RecorderProvider(log)))
            .AddScoutLlm(configuration)
            .BuildServiceProvider();

        services.GetRequiredService<ILlmProviderFactory>().Name.ShouldBe(provider);

        log.ShouldHaveSingleItem().Message.ShouldBe(
            $"The {provider} provider sends its API key over plain http to llm.example.test, which is not this machine; " +
            "an https endpoint keeps the key encrypted.");
    }

    [Theory]
    [InlineData("OpenAI")]
    [InlineData("OpenRouter")]
    public void An_Embeddings_Endpoint_Over_Plain_Http_To_Another_Host_Is_Warned_Once_Without_The_Key_Or_The_Url(string provider)
    {
        var log = new List<Entry>();

        EmbeddingsOver(provider, "http://user:pw@embed.example.test:8080/secret/path?token=abc", log);

        var warning = log.ShouldHaveSingleItem();
        warning.Message.ShouldBe(
            $"The {provider} provider sends its API key over plain http to embed.example.test, which is not this machine; " +
            "an https endpoint keeps the key encrypted.");
        var everything = string.Join(" ", warning.State.Select(p => $"{p.Key}={p.Value}").Append(warning.Message));
        foreach (var leak in Leaks)
        {
            everything.ShouldNotContain(leak, Case.Insensitive);
        }
    }

    [Theory]
    [InlineData("AzureAIInference")]
    [InlineData("AzureOpenAI")]
    [InlineData("HuggingFace")]
    [InlineData("Mistral")]
    [InlineData("OpenAI")]
    [InlineData("Ollama")]
    [InlineData("OpenAI embeddings")]
    [InlineData("OpenRouter embeddings")]
    public void A_Refused_Endpoint_Is_Named_By_Scheme_Host_And_Port_Never_Its_User_Info_Path_Or_Query(string provider)
    {
        const string endpoint = "ftp://user:pw@llm.example.test:2121/v1?token=abc";

        var ex = Should.Throw<ArgumentException>(() => _ = provider switch
        {
            "Ollama" => new OllamaLlmProviderFactory(Options.Create(new LlmOptions { Provider = provider, Ollama = new OllamaOptions { Endpoint = endpoint } })),
            "OpenAI embeddings" => EmbeddingsOver("OpenAI", endpoint, []),
            "OpenRouter embeddings" => EmbeddingsOver("OpenRouter", endpoint, []),
            _ => Build(provider, endpoint, []),
        });

        ex.Message.ShouldContain("'ftp://llm.example.test:2121'");
        foreach (var leak in new[] { Key, "user", "pw", "v1", "token", "abc" })
        {
            ex.Message.ShouldNotContain(leak);
        }
    }

    private static ILlmProviderFactory EmbeddingsOver(string provider, string endpoint, List<Entry> log) => provider == "OpenAI"
        ? new OpenAILlmProviderFactory(
            Options.Create(new LlmOptions
            {
                Provider = provider,
                OpenAI = new OpenAIOptions
                {
                    ApiKey = Key,
                    Chat = new ChatModelOptions { Model = ChatModel(provider) },
                    Embeddings = new OpenAIEmbeddingOptions { Endpoint = endpoint, Model = "text-embedding-3-small" },
                },
            }),
            new Recorder<OpenAILlmProviderFactory>(log))
        : new OpenRouterLlmProviderFactory(
            Options.Create(new LlmOptions
            {
                Provider = provider,
                OpenRouter = new OpenRouterOptions
                {
                    ApiKey = Key,
                    Chat = new ChatModelOptions { Model = "openai/gpt-4o-mini" },
                    Embeddings = new OpenRouterEmbeddingOptions { Endpoint = endpoint, ApiKey = Key, Model = "text-embedding-3-small" },
                },
            }),
            new Recorder<OpenRouterLlmProviderFactory>(log));

    private static string ChatModel(string provider) => provider switch
    {
        "AzureAIInference" => "Phi-3.5-MoE-instruct",
        "AzureOpenAI" => "gpt-4o",
        "HuggingFace" => "meta-llama/Llama-3.1-8B-Instruct",
        "OpenAI" => "gpt-4o-mini",
        _ => "mistral-small-latest",
    };

    private static object? ChatModelOf(ILlmProviderFactory factory)
    {
        var builder = Kernel.CreateBuilder();
        factory.AddToKernel(builder);
        var attributes = builder.Build().GetRequiredService<IChatCompletionService>().Attributes;
        return attributes.TryGetValue("DeploymentName", out var deployment) ? deployment : attributes[AIServiceExtensions.ModelIdKey];
    }

    private static ILlmProviderFactory Build(string provider, string endpoint, List<Entry> log) => provider switch
    {
        "AzureAIInference" => new AzureAIInferenceLlmProviderFactory(
            Options.Create(new LlmOptions
            {
                Provider = provider,
                AzureAIInference = new AzureAIInferenceOptions
                {
                    ApiKey = Key,
                    Endpoint = endpoint,
                    Chat = new ChatModelOptions { Model = ChatModel(provider) },
                    Embeddings = new EmbeddingModelOptions { Model = "text-embedding-3-small" },
                },
            }),
            new Recorder<AzureAIInferenceLlmProviderFactory>(log)),
        "AzureOpenAI" => new AzureOpenAILlmProviderFactory(
            Options.Create(new LlmOptions
            {
                Provider = provider,
                AzureOpenAI = new AzureOpenAIOptions
                {
                    ApiKey = Key,
                    Endpoint = endpoint,
                    Chat = new AzureDeploymentOptions { Deployment = ChatModel(provider) },
                    Embeddings = new AzureDeploymentOptions { Deployment = "text-embedding-3-small" },
                },
            }),
            new Recorder<AzureOpenAILlmProviderFactory>(log)),
        "HuggingFace" => new HuggingFaceLlmProviderFactory(
            Options.Create(new LlmOptions
            {
                Provider = provider,
                HuggingFace = new HuggingFaceOptions
                {
                    ApiKey = Key,
                    Endpoint = endpoint,
                    Chat = new ChatModelOptions { Model = ChatModel(provider) },
                    Embeddings = new EmbeddingModelOptions { Model = "sentence-transformers/all-MiniLM-L6-v2" },
                },
            }),
            new Recorder<HuggingFaceLlmProviderFactory>(log)),
        "Mistral" => new MistralLlmProviderFactory(
            Options.Create(new LlmOptions
            {
                Provider = provider,
                Mistral = new MistralOptions
                {
                    ApiKey = Key,
                    Endpoint = endpoint,
                    Chat = new ChatModelOptions { Model = ChatModel(provider) },
                    Embeddings = new EmbeddingModelOptions { Model = "mistral-embed" },
                },
            }),
            new Recorder<MistralLlmProviderFactory>(log)),
        "OpenAI" => new OpenAILlmProviderFactory(
            Options.Create(new LlmOptions
            {
                Provider = provider,
                OpenAI = new OpenAIOptions
                {
                    ApiKey = Key,
                    Endpoint = endpoint,
                    Chat = new ChatModelOptions { Model = ChatModel(provider) },
                    Embeddings = new OpenAIEmbeddingOptions { Model = "text-embedding-3-small" },
                },
            }),
            new Recorder<OpenAILlmProviderFactory>(log)),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    private sealed record Entry(LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> State);

    private sealed class RecorderProvider(List<Entry> log) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Recorder<object>(log);

        public void Dispose()
        {
        }
    }

    private sealed class Recorder<T>(List<Entry> log) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => log.Add(new Entry(
                logLevel,
                formatter(state, exception),
                state as IReadOnlyList<KeyValuePair<string, object?>> ?? []));
    }
}
