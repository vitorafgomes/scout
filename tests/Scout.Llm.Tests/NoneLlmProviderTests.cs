using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Scout.Llm.Abstractions;

namespace Scout.Llm.Tests;

public sealed class NoneLlmProviderTests
{
    private const string FixedMessage =
        "No AI provider is connected. Connect one on the AI Sources screen in Scout.";

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    private static (ServiceProvider provider, CountingHandler handler) Build()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Llm:Provider"] = "None",
            })
            .Build();
        var handler = new CountingHandler();
        var services = new ServiceCollection();
        services.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => handler));
        services.AddScoutLlm(configuration);
        return (services.BuildServiceProvider(), handler);
    }

    [Fact]
    public void Provider_None_Resolves_The_None_Factory_Without_Any_Provider_Settings()
    {
        var (provider, _) = Build();
        using (provider)
        {
            provider.GetRequiredService<ILlmProviderFactory>().Name.ShouldBe("None");
        }
    }

    [Fact]
    public async Task Provider_None_Answers_With_The_Fixed_Message_And_Makes_No_Http_Call()
    {
        var (provider, handler) = Build();
        using (provider)
        {
            var gateway = provider.GetRequiredService<IChatGateway>();
            var chunks = new List<string>();
            await foreach (var chunk in gateway.StreamAsync(new ChatRequest { UserMessage = "hello" }, CancellationToken.None))
            {
                chunks.Add(chunk);
            }

            string.Concat(chunks).ShouldBe(FixedMessage);
            handler.Requests.ShouldBe(0);
        }
    }

    [Fact]
    public async Task Provider_None_Complete_Returns_The_Fixed_Message()
    {
        var (provider, handler) = Build();
        using (provider)
        {
            var gateway = provider.GetRequiredService<IChatGateway>();

            var reply = await gateway.CompleteAsync(new ChatRequest { UserMessage = "hello" }, CancellationToken.None);

            reply.ShouldBe(FixedMessage);
            handler.Requests.ShouldBe(0);
        }
    }

    [Fact]
    public async Task Provider_None_Embeddings_Throw_A_Clear_InvalidOperationException()
    {
        var (provider, handler) = Build();
        using (provider)
        {
            var embedder = provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

            var ex = await Should.ThrowAsync<InvalidOperationException>(
                () => embedder.GenerateAsync(["text"]));

            ex.Message.ShouldBe("No AI provider is connected.");
            handler.Requests.ShouldBe(0);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_Blank_Or_Absent_Provider_Still_Resolves_Ollama(string? configured)
    {
        var values = new Dictionary<string, string?>
        {
            ["Scout:Llm:Ollama:Endpoint"] = "http://localhost:11434",
            ["Scout:Llm:Ollama:Chat:Model"] = "llama3.1",
            ["Scout:Llm:Ollama:Embeddings:Model"] = "nomic-embed-text",
        };
        if (configured is not null)
        {
            values["Scout:Llm:Provider"] = configured;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        using var provider = new ServiceCollection().AddScoutLlm(configuration).BuildServiceProvider();

        provider.GetRequiredService<ILlmProviderFactory>().Name.ShouldBe("Ollama");
    }
}
