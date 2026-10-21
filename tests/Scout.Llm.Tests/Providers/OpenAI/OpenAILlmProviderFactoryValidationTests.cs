using Microsoft.Extensions.Options;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.OpenAI;

namespace Scout.Llm.Tests.Providers.OpenAI;

public sealed class OpenAILlmProviderFactoryValidationTests
{
    private static LlmOptions ValidOptions() => new()
    {
        Provider = "OpenAI",
        OpenAI = new OpenAIOptions
        {
            ApiKey = "test-key",
            Chat = new ChatModelOptions { Model = "gpt-4o-mini" },
            Embeddings = new OpenAIEmbeddingOptions { Model = "text-embedding-3-small" },
        },
    };

    private static OpenAILlmProviderFactory Create(LlmOptions options)
        => new(Options.Create(options));

    [Fact]
    public void Ctor_Throws_When_Options_Null()
    {
        Should.Throw<ArgumentNullException>(() => new OpenAILlmProviderFactory(null!));
    }

    [Fact]
    public void Ctor_Throws_When_Options_Value_Null()
    {
        Should.Throw<ArgumentException>(() => Create(null!));
    }

    [Fact]
    public void Ctor_Throws_When_OpenAI_Null()
    {
        var opts = ValidOptions();
        opts.OpenAI = null;

        Should.Throw<ArgumentException>(() => Create(opts))
            .Message.ShouldContain("OpenAI");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Ctor_Throws_When_ApiKey_Null_Or_Whitespace(string? apiKey)
    {
        var opts = ValidOptions();
        opts.OpenAI!.ApiKey = apiKey!;

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
        opts.OpenAI!.Chat.Model = model!;

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
        opts.OpenAI!.Embeddings.Model = model!;

        Should.Throw<ArgumentException>(() => Create(opts))
            .Message.ShouldContain("Embeddings.Model");
    }

    [Fact]
    public void Ctor_Throws_When_Chat_Endpoint_Invalid()
    {
        var opts = ValidOptions();
        opts.OpenAI!.Endpoint = "not-a-uri";

        Should.Throw<ArgumentException>(() => Create(opts))
            .Message.ShouldContain("Endpoint");
    }

    [Fact]
    public void Ctor_Throws_When_Embeddings_Endpoint_Invalid()
    {
        var opts = ValidOptions();
        opts.OpenAI!.Embeddings.Endpoint = "ftp://bad";

        Should.Throw<ArgumentException>(() => Create(opts))
            .Message.ShouldContain("Endpoint");
    }

    [Fact]
    public void Ctor_With_Valid_Options_Constructs_Successfully()
    {
        Should.NotThrow(() => Create(ValidOptions()));
    }

    [Fact]
    public void Ctor_With_Decoupled_Endpoints_Constructs_Successfully()
    {
        var opts = ValidOptions();
        opts.OpenAI!.Endpoint = "https://openrouter.ai/api/v1";
        opts.OpenAI.Embeddings.Endpoint = "https://api.openai.com/v1";
        opts.OpenAI.Embeddings.ApiKey = "embeddings-key";

        Should.NotThrow(() => Create(opts));
    }

    [Fact]
    public void Name_Is_OpenAI()
    {
        Create(ValidOptions()).Name.ShouldBe("OpenAI");
    }
}
