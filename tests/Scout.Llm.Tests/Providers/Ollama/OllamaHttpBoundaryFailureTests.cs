using System.Net;
using Microsoft.Extensions.AI;
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
/// Hermetic HTTP-boundary tests. The factory's internal HttpClient seam lets us
/// drive the SK Ollama connector with a stub handler, so the chat and embedding
/// services are exercised without a real Ollama daemon. The contract: a failing
/// boundary surfaces an exception rather than hanging or returning a silent
/// empty result.
/// </summary>
public sealed class OllamaHttpBoundaryFailureTests
{
    private static OllamaLlmProviderFactory CreateFactory(HttpClient httpClient) =>
        new(
            Options.Create(new LlmOptions
            {
                Provider = "Ollama",
                Ollama = new OllamaOptions
                {
                    Endpoint = "http://localhost:11434",
                    Chat = new OllamaChatOptions { Model = "llama3.1" },
                    Embeddings = new EmbeddingModelOptions { Model = "nomic-embed-text" },
                },
            }),
            httpClient);

    private static Kernel BuildKernel(HttpClient httpClient)
    {
        var builder = Kernel.CreateBuilder();
        CreateFactory(httpClient).AddToKernel(builder);
        return builder.Build();
    }

    [Fact]
    public async Task ChatCompletion_Surfaces_Failure_On_ServerError()
    {
        using var handler = new StubHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("boom"),
            });
        using var client = new HttpClient(handler);
        var chat = BuildKernel(client).GetRequiredService<IChatCompletionService>();

        await Should.ThrowAsync<Exception>(() =>
            chat.GetChatMessageContentAsync("hello"));
    }

    [Fact]
    public async Task ChatCompletion_Surfaces_Failure_On_ConnectionDrop()
    {
        using var handler = new StubHttpMessageHandler(
            _ => throw new HttpRequestException("connection reset"));
        using var client = new HttpClient(handler);
        var chat = BuildKernel(client).GetRequiredService<IChatCompletionService>();

        await Should.ThrowAsync<Exception>(() =>
            chat.GetChatMessageContentAsync("hello"));
    }

    [Fact]
    public async Task ChatCompletion_Surfaces_Failure_On_Timeout()
    {
        using var handler = new StubHttpMessageHandler(
            _ => throw new TaskCanceledException("timeout"));
        using var client = new HttpClient(handler);
        var chat = BuildKernel(client).GetRequiredService<IChatCompletionService>();

        await Should.ThrowAsync<Exception>(() =>
            chat.GetChatMessageContentAsync("hello"));
    }

    [Fact]
    public async Task Embedding_Surfaces_Failure_On_ServerError()
    {
        using var handler = new StubHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("boom"),
            });
        using var client = new HttpClient(handler);
        var embeddings = BuildKernel(client)
            .GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

        await Should.ThrowAsync<Exception>(() =>
            embeddings.GenerateAsync(["hello"]));
    }

    [Fact]
    public async Task Embedding_Surfaces_Failure_On_ConnectionDrop()
    {
        using var handler = new StubHttpMessageHandler(
            _ => throw new HttpRequestException("connection reset"));
        using var client = new HttpClient(handler);
        var embeddings = BuildKernel(client)
            .GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

        await Should.ThrowAsync<Exception>(() =>
            embeddings.GenerateAsync(["hello"]));
    }

    [Theory]
    [InlineData("gateway stream", "llama3.1")]
    [InlineData("live translator", "llama3.1")]
    [InlineData("translation model", "other-model")]
    public async Task A_Model_The_Server_Does_Not_Have_Is_Said_By_Name(string caller, string model)
    {
        var stub = new OllamaApiStub((_, _) => OllamaApiStub.Error(
            HttpStatusCode.NotFound, $$"""{"error":"model '{{model}}' not found"}"""));
        var sink = new RecordingUsageSink();
        using var provider = Compose(stub, sink, translationModel: caller == "translation model" ? model : null);

        var failure = await Record.ExceptionAsync(() => caller == "gateway stream"
            ? Drain(provider.GetRequiredService<IChatGateway>(), [])
            : provider.GetRequiredService<ILiveTranslator>().TranslateAsync("Bom dia.", [], "en", CancellationToken.None));

        stub.Requests.ShouldHaveSingleItem().Uri.AbsolutePath.ShouldBe("/api/chat");
        var missing = failure.ShouldBeOfType<LlmModelNotFoundException>();
        missing.Model.ShouldBe(model);
        missing.Message.ShouldNotContain("not found");
        sink.Answers.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, typeof(LlmRateLimitException))]
    [InlineData(HttpStatusCode.Unauthorized, typeof(LlmKeyRejectedException))]
    public async Task A_429_Or_A_401_From_A_Proxy_In_Front_Of_The_Server_Is_A_Rate_Limit_Or_A_Rejected_Key(
        HttpStatusCode status, Type expected)
    {
        var stub = new OllamaApiStub((_, _) => OllamaApiStub.Error(status, """{"error":"refused by the proxy"}"""));
        var sink = new RecordingUsageSink();
        using var provider = Compose(stub, sink);

        var failure = await Record.ExceptionAsync(() => Drain(provider.GetRequiredService<IChatGateway>(), []));

        stub.Requests.ShouldHaveSingleItem().Uri.AbsolutePath.ShouldBe("/api/chat");
        failure.ShouldNotBeNull().GetType().ShouldBe(expected);
        failure.Message.ShouldNotContain("refused by the proxy");
        sink.Answers.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("gateway stream", """{"error":"registry.ollama.ai/library/sample:1b does not support tools"}""", "does not support tools")]
    [InlineData("gateway stream", """{"error":"invalid request"}""", "invalid request")]
    [InlineData("live translator", """{"error":"registry.ollama.ai/library/sample:1b does not support tools"}""", "does not support tools")]
    [InlineData("live translator", """{"error":"invalid request"}""", "invalid request")]
    [InlineData("one-reply service", """{"error":"registry.ollama.ai/library/sample:1b does not support tools"}""", "does not support tools")]
    [InlineData("one-reply service", """{"error":"invalid request"}""", "invalid request")]
    public async Task A_400_From_The_Server_Reaches_The_Caller_As_An_Http_Failure_Without_The_Servers_Text(
        string caller, string body, string serverText)
    {
        var stub = new OllamaApiStub((_, _) => OllamaApiStub.Error(HttpStatusCode.BadRequest, body));
        var sink = new RecordingUsageSink();
        using var provider = Compose(stub, sink);
        var history = new ChatHistory();
        history.AddUserMessage("hello");

        var failure = await Record.ExceptionAsync(() => caller switch
        {
            "gateway stream" => Drain(provider.GetRequiredService<IChatGateway>(), []),
            "live translator" => provider.GetRequiredService<ILiveTranslator>().TranslateAsync("Bom dia.", [], "en", CancellationToken.None),
            _ => provider.GetRequiredService<IChatCompletionService>().GetChatMessageContentAsync(history),
        });

        stub.Requests.ShouldHaveSingleItem().Uri.AbsolutePath.ShouldBe("/api/chat");
        var http = failure.ShouldBeOfType<HttpOperationException>();
        http.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        http.Message.ShouldBe("Ollama answered HTTP 400.");
        http.Message.ShouldNotContain(serverText);
        http.ResponseContent.ShouldBeNull();
        sink.Answers.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("500")]
    [InlineData("reset")]
    [InlineData("timeout")]
    public async Task A_500_A_Reset_And_A_Timeout_Reach_The_Caller_And_No_Answer_Is_Reported(string failing)
    {
        var stub = new OllamaApiStub((_, _) => failing switch
        {
            "500" => OllamaApiStub.Error(HttpStatusCode.InternalServerError, """{"error":"out of memory"}"""),
            "reset" => throw new HttpRequestException("connection reset"),
            _ => throw new TaskCanceledException("timeout"),
        });
        var sink = new RecordingUsageSink();
        using var provider = Compose(stub, sink);
        var chunks = new List<string>();

        var failure = await Record.ExceptionAsync(() => Drain(provider.GetRequiredService<IChatGateway>(), chunks));

        stub.Requests.ShouldHaveSingleItem().Uri.AbsolutePath.ShouldBe("/api/chat");
        switch (failing)
        {
            case "500":
                failure.ShouldBeOfType<HttpOperationException>().StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
                break;
            case "reset":
                failure.ShouldBeOfType<HttpRequestException>().StatusCode.ShouldBeNull();
                break;
            default:
                failure.ShouldNotBeNull().ShouldBeAssignableTo<OperationCanceledException>();
                break;
        }

        chunks.ShouldBeEmpty();
        sink.Answers.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Stream_That_Breaks_After_Its_First_Text_Yields_That_Text_Then_Fails()
    {
        var stub = new OllamaApiStub((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new BreakingStream(OllamaApiStub.TextLines("llama3.1", ["Hello"]).First() + "\n")),
        });
        var sink = new RecordingUsageSink();
        using var provider = Compose(stub, sink);
        var chunks = new List<string>();

        var failure = await Record.ExceptionAsync(() => Drain(provider.GetRequiredService<IChatGateway>(), chunks));

        stub.Requests.ShouldHaveSingleItem().Uri.AbsolutePath.ShouldBe("/api/chat");
        chunks.ShouldBe(["Hello"]);
        failure.ShouldBeOfType<IOException>().Message.ShouldBe("connection reset");
        sink.Answers.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("provider service")]
    [InlineData("gateway stream")]
    public async Task A_Stream_Cancelled_Mid_Answer_Throws_OperationCanceled_And_Is_Not_Reported_As_A_Finished_Answer(string caller)
    {
        var stub = new OllamaApiStub((_, _) => OllamaApiStub.Lines(OllamaApiStub.TextLines("llama3.1", ["Hello", " and", " more."])));
        var sink = new RecordingUsageSink();
        using var provider = Compose(stub, sink);
        using var stop = new CancellationTokenSource();
        var history = new ChatHistory();
        history.AddUserMessage("hello");
        var chunks = new List<string>();

        var failure = await Record.ExceptionAsync(async () =>
        {
            var stream = caller == "gateway stream"
                ? provider.GetRequiredService<IChatGateway>().StreamAsync(new ChatRequest { UserMessage = "hello" }, stop.Token)
                : Texts(provider.GetRequiredService<IChatCompletionService>().GetStreamingChatMessageContentsAsync(history, cancellationToken: stop.Token));
            await foreach (var chunk in stream)
            {
                chunks.Add(chunk);
                await stop.CancelAsync();
            }
        });

        chunks.ShouldBe(["Hello"]);
        failure.ShouldNotBeNull().ShouldBeAssignableTo<OperationCanceledException>();
        sink.Answers.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("streaming service")]
    [InlineData("gateway stream")]
    [InlineData("one-reply service")]
    public async Task An_Error_Line_Inside_A_Started_Answer_Reaches_The_Caller_As_A_Server_Failure_After_The_Text_Already_Sent(string caller)
    {
        const string ErrorLine = """{"error":"llama runner process has terminated"}""";
        var stub = new OllamaApiStub((_, _) => OllamaApiStub.Lines(caller == "one-reply service"
            ? [ErrorLine]
            : [OllamaApiStub.TextLines("llama3.1", ["Hello"]).First(), ErrorLine]));
        var sink = new RecordingUsageSink();
        using var provider = Compose(stub, sink);
        var history = new ChatHistory();
        history.AddUserMessage("hello");
        var chunks = new List<string>();

        var failure = await Record.ExceptionAsync(async () =>
        {
            if (caller == "one-reply service")
            {
                chunks.Add((await provider.GetRequiredService<IChatCompletionService>().GetChatMessageContentAsync(history)).Content ?? string.Empty);
                return;
            }

            var stream = caller == "gateway stream"
                ? provider.GetRequiredService<IChatGateway>().StreamAsync(new ChatRequest { UserMessage = "hello" }, CancellationToken.None)
                : Texts(provider.GetRequiredService<IChatCompletionService>().GetStreamingChatMessageContentsAsync(history));
            await foreach (var chunk in stream)
            {
                chunks.Add(chunk);
            }
        });

        chunks.ShouldBe(caller == "one-reply service" ? [] : ["Hello"]);
        var http = failure.ShouldBeOfType<HttpOperationException>();
        http.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        http.Message.ShouldBe("Ollama stopped answering before the answer was complete.");
        http.Message.ShouldNotContain("llama runner");
        sink.Answers.ShouldBeEmpty();
    }

    private static async IAsyncEnumerable<string> Texts(IAsyncEnumerable<StreamingChatMessageContent> updates)
    {
        await foreach (var update in updates)
        {
            if (!string.IsNullOrEmpty(update.Content))
            {
                yield return update.Content;
            }
        }
    }

    private static async Task Drain(IChatGateway gateway, List<string> chunks)
    {
        await foreach (var chunk in gateway.StreamAsync(new ChatRequest { UserMessage = "hello" }, CancellationToken.None))
        {
            chunks.Add(chunk);
        }
    }

    private static ServiceProvider Compose(OllamaApiStub stub, ILlmUsageSink sink, string? translationModel = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Llm:Provider"] = "Ollama",
                ["Scout:Llm:Ollama:Chat:Model"] = "llama3.1",
                ["Scout:Llm:Translation:Model"] = translationModel,
            })
            .Build();
        var services = new ServiceCollection().AddScoutLlm(configuration);
        services.AddSingleton<ILlmProviderFactory>(sp => new OllamaLlmProviderFactory(
            sp.GetRequiredService<IOptions<LlmOptions>>(), new HttpClient(stub)));
        services.AddSingleton(sink);
        return services.BuildServiceProvider();
    }

    private sealed class RecordingUsageSink : ILlmUsageSink
    {
        public List<LlmUsage> Answers { get; } = [];

        public void Answered(LlmUsage usage) => Answers.Add(usage);

        public void UsageLimitReached(string source)
        {
        }
    }

    private sealed class BreakingStream(string firstLine) : Stream
    {
        private readonly byte[] _first = System.Text.Encoding.UTF8.GetBytes(firstLine);
        private bool _sent;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_sent)
            {
                throw new IOException("connection reset");
            }

            _sent = true;
            _first.CopyTo(buffer);
            return _first.Length;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
