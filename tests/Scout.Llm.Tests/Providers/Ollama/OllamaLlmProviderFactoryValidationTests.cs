using Microsoft.Extensions.Options;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.Ollama;

namespace Scout.Llm.Tests.Providers.Ollama;

public sealed class OllamaLlmProviderFactoryValidationTests
{
    private static LlmOptions ValidOptions() => new()
    {
        Provider = "Ollama",
        Ollama = new OllamaOptions
        {
            Endpoint = "http://localhost:11434",
            Chat = new ChatModelOptions { Model = "llama3.1" },
            Embeddings = new EmbeddingModelOptions
            {
                Model = "nomic-embed-text",
                MaxBatchSize = 16,
                ExpectedDimensions = 0,
                HttpTimeoutSeconds = 100,
            },
        },
    };

    private static OllamaLlmProviderFactory Create(LlmOptions options)
        => new(Options.Create(options));

    [Fact]
    public void Ctor_Throws_When_Options_Null()
    {
        Should.Throw<ArgumentNullException>(() => new OllamaLlmProviderFactory(null!));
    }

    [Fact]
    public void Ctor_Throws_When_Options_Value_Null()
    {
        Should.Throw<ArgumentException>(() => Create(null!));
    }

    [Fact]
    public void Ctor_Throws_When_Ollama_Null()
    {
        var opts = ValidOptions();
        opts.Ollama = null!;

        Should.Throw<ArgumentException>(() => Create(opts))
            .Message.ShouldContain("Ollama");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Ctor_Throws_When_Endpoint_Null_Or_Whitespace(string? endpoint)
    {
        var opts = ValidOptions();
        opts.Ollama.Endpoint = endpoint!;

        Should.Throw<ArgumentException>(() => Create(opts))
            .Message.ShouldContain("Endpoint");
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("localhost:11434")]
    [InlineData("/relative/path")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com")]
    public void Ctor_Throws_When_Endpoint_Not_Absolute_Http_Or_Https(string endpoint)
    {
        var opts = ValidOptions();
        opts.Ollama.Endpoint = endpoint;

        Should.Throw<ArgumentException>(() => Create(opts))
            .Message.ShouldContain("not a valid absolute http or https URI");
    }

    [Theory]
    [InlineData("http://localhost:11434")]
    [InlineData("https://ollama.internal")]
    [InlineData("http://127.0.0.1:11434/")]
    public void Ctor_Accepts_Valid_Http_And_Https_Endpoints(string endpoint)
    {
        var opts = ValidOptions();
        opts.Ollama.Endpoint = endpoint;

        Should.NotThrow(() => Create(opts));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Ctor_Throws_When_Chat_Model_Null_Or_Whitespace(string? model)
    {
        var opts = ValidOptions();
        opts.Ollama.Chat.Model = model!;

        Should.Throw<ArgumentException>(() => Create(opts))
            .Message.ShouldContain("Chat.Model");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Ctor_Throws_When_Embedding_Model_Null_Or_Whitespace(string? model)
    {
        var opts = ValidOptions();
        opts.Ollama.Embeddings.Model = model!;

        Should.Throw<ArgumentException>(() => Create(opts))
            .Message.ShouldContain("Embeddings.Model");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Ctor_Throws_When_MaxBatchSize_Less_Than_One(int batch)
    {
        var opts = ValidOptions();
        opts.Ollama.Embeddings.MaxBatchSize = batch;

        Should.Throw<ArgumentException>(() => Create(opts))
            .Message.ShouldContain("MaxBatchSize");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Ctor_Throws_When_HttpTimeoutSeconds_Less_Than_One(int timeout)
    {
        var opts = ValidOptions();
        opts.Ollama.Embeddings.HttpTimeoutSeconds = timeout;

        Should.Throw<ArgumentException>(() => Create(opts))
            .Message.ShouldContain("HttpTimeoutSeconds");
    }

    [Fact]
    public void Ctor_Throws_When_ExpectedDimensions_Negative()
    {
        var opts = ValidOptions();
        opts.Ollama.Embeddings.ExpectedDimensions = -1;

        Should.Throw<ArgumentException>(() => Create(opts))
            .Message.ShouldContain("ExpectedDimensions");
    }

    [Fact]
    public void Ctor_With_Valid_Options_Constructs_Successfully()
    {
        Should.NotThrow(() => Create(ValidOptions()));
    }

    [Fact]
    public void Name_Is_Ollama()
    {
        Create(ValidOptions()).Name.ShouldBe("Ollama");
    }
}
