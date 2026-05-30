using Microsoft.Extensions.Options;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.OpenRouter;

namespace Scout.Llm.Tests.Providers.OpenRouter;

public sealed class OpenRouterLlmProviderFactoryValidationTests
{
    private static LlmOptions ValidOptions() => new()
    {
        Provider = "OpenRouter",
        OpenRouter = new OpenRouterOptions
        {
            ApiKey = "test-key",
            Chat = new ChatModelOptions { Model = "openai/gpt-4o-mini" },
            Embeddings = new OpenRouterEmbeddingOptions
            {
                Model = "text-embedding-3-small",
                Endpoint = "https://api.openai.com/v1",
                ApiKey = "embeddings-key",
            },
        },
    };

    private static OpenRouterLlmProviderFactory Create(LlmOptions options)
        => new(Options.Create(options));

    [Fact]
    public void Ctor_Throws_When_Options_Null()
    {
        Should.Throw<ArgumentNullException>(() => new OpenRouterLlmProviderFactory(null!));
    }

    [Fact]
    public void Ctor_Throws_When_Options_Value_Null()
    {
        Should.Throw<ArgumentException>(() => Create(null!));
    }

    [Fact]
    public void Ctor_Throws_When_OpenRouter_Null()
    {
        var opts = ValidOptions();
        opts.OpenRouter = null;

        Should.Throw<ArgumentException>(() => Create(opts))
            .Message.ShouldContain("OpenRouter");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Ctor_Throws_When_ApiKey_Null_Or_Whitespace(string? apiKey)
    {
        var opts = ValidOptions();
        opts.OpenRouter!.ApiKey = apiKey!;

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
        opts.OpenRouter!.Chat.Model = model!;

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
        opts.OpenRouter!.Embeddings.Model = model!;

        Should.Throw<ArgumentException>(() => Create(opts))
            .Message.ShouldContain("Embeddings.Model");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Ctor_Throws_When_Embedding_Endpoint_Null_Or_Whitespace(string? endpoint)
    {
        var opts = ValidOptions();
        opts.OpenRouter!.Embeddings.Endpoint = endpoint!;

        Should.Throw<ArgumentException>(() => Create(opts))
            .Message.ShouldContain("Embeddings.Endpoint");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Ctor_Throws_When_Embedding_ApiKey_Null_Or_Whitespace(string? apiKey)
    {
        var opts = ValidOptions();
        opts.OpenRouter!.Embeddings.ApiKey = apiKey!;

        Should.Throw<ArgumentException>(() => Create(opts))
            .Message.ShouldContain("Embeddings.ApiKey");
    }

    [Fact]
    public void Ctor_Throws_When_Embeddings_Endpoint_Invalid()
    {
        var opts = ValidOptions();
        opts.OpenRouter!.Embeddings.Endpoint = "not-a-uri";

        Should.Throw<ArgumentException>(() => Create(opts))
            .Message.ShouldContain("Endpoint");
    }

    [Fact]
    public void Ctor_With_Valid_Options_Constructs_Successfully()
    {
        Should.NotThrow(() => Create(ValidOptions()));
    }

    [Fact]
    public void Name_Is_OpenRouter()
    {
        Create(ValidOptions()).Name.ShouldBe("OpenRouter");
    }
}
