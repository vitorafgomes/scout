using Microsoft.Extensions.Options;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.Gemini;

namespace Scout.Llm.Tests.Providers.Gemini;

public sealed class GeminiLlmProviderFactoryValidationTests
{
    private static LlmOptions ValidOptions() => new()
    {
        Provider = "Gemini",
        Gemini = new GeminiOptions
        {
            ApiKey = "test-key",
            Chat = new ChatModelOptions { Model = "gemini-1.5-flash" },
            Embeddings = new EmbeddingModelOptions
            {
                Model = "text-embedding-004",
                MaxBatchSize = 16,
                ExpectedDimensions = 0,
                HttpTimeoutSeconds = 100,
            },
        },
    };

    private static GeminiLlmProviderFactory Create(LlmOptions options)
        => new(Options.Create(options));

    [Fact]
    public void Ctor_Throws_When_Options_Null()
    {
        Should.Throw<ArgumentNullException>(() => new GeminiLlmProviderFactory(null!));
    }

    [Fact]
    public void Ctor_Throws_When_Options_Value_Null()
    {
        Should.Throw<ArgumentException>(() => Create(null!));
    }

    [Fact]
    public void Ctor_Throws_When_Gemini_Null()
    {
        var opts = ValidOptions();
        opts.Gemini = null;

        Should.Throw<ArgumentException>(() => Create(opts))
            .Message.ShouldContain("Gemini");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Ctor_Throws_When_ApiKey_Null_Or_Whitespace(string? apiKey)
    {
        var opts = ValidOptions();
        opts.Gemini!.ApiKey = apiKey!;

        Should.Throw<ArgumentException>(() => Create(opts))
            .Message.ShouldContain("ApiKey");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Ctor_Throws_When_Chat_Model_Null_Or_Whitespace(string? model)
    {
        var opts = ValidOptions();
        opts.Gemini!.Chat.Model = model!;

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
        opts.Gemini!.Embeddings.Model = model!;

        Should.Throw<ArgumentException>(() => Create(opts))
            .Message.ShouldContain("Embeddings.Model");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Ctor_Throws_When_MaxBatchSize_Less_Than_One(int batch)
    {
        var opts = ValidOptions();
        opts.Gemini!.Embeddings.MaxBatchSize = batch;

        Should.Throw<ArgumentException>(() => Create(opts))
            .Message.ShouldContain("MaxBatchSize");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Ctor_Throws_When_HttpTimeoutSeconds_Less_Than_One(int timeout)
    {
        var opts = ValidOptions();
        opts.Gemini!.Embeddings.HttpTimeoutSeconds = timeout;

        Should.Throw<ArgumentException>(() => Create(opts))
            .Message.ShouldContain("HttpTimeoutSeconds");
    }

    [Fact]
    public void Ctor_Throws_When_ExpectedDimensions_Negative()
    {
        var opts = ValidOptions();
        opts.Gemini!.Embeddings.ExpectedDimensions = -1;

        Should.Throw<ArgumentException>(() => Create(opts))
            .Message.ShouldContain("ExpectedDimensions");
    }

    [Fact]
    public void Ctor_With_Valid_Options_Constructs_Successfully()
    {
        Should.NotThrow(() => Create(ValidOptions()));
    }

    [Fact]
    public void Name_Is_Gemini()
    {
        Create(ValidOptions()).Name.ShouldBe("Gemini");
    }
}
