using Microsoft.Extensions.Options;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.HuggingFace;

namespace Scout.Llm.Tests.Providers.HuggingFace;

public sealed class HuggingFaceLlmProviderFactoryValidationTests
{
    private static LlmOptions ValidOptions() => new()
    {
        Provider = "HuggingFace",
        HuggingFace = new HuggingFaceOptions
        {
            ApiKey = "test-key",
            Chat = new ChatModelOptions { Model = "meta-llama/Llama-3.1-8B-Instruct" },
            Embeddings = new EmbeddingModelOptions { Model = "sentence-transformers/all-MiniLM-L6-v2" },
        },
    };

    private static HuggingFaceLlmProviderFactory Create(LlmOptions options)
        => new(Options.Create(options));

    [Fact]
    public void Ctor_Throws_When_Options_Null()
    {
        Should.Throw<ArgumentNullException>(() => new HuggingFaceLlmProviderFactory(null!));
    }

    [Fact]
    public void Ctor_Throws_When_Options_Value_Null()
    {
        Should.Throw<ArgumentException>(() => Create(null!));
    }

    [Fact]
    public void Ctor_Throws_When_HuggingFace_Null()
    {
        var opts = ValidOptions();
        opts.HuggingFace = null;

        Should.Throw<ArgumentException>(() => Create(opts))
            .Message.ShouldContain("HuggingFace");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Ctor_Throws_When_ApiKey_Null_Or_Whitespace(string? apiKey)
    {
        var opts = ValidOptions();
        opts.HuggingFace!.ApiKey = apiKey!;

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
        opts.HuggingFace!.Chat.Model = model!;

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
        opts.HuggingFace!.Embeddings.Model = model!;

        Should.Throw<ArgumentException>(() => Create(opts))
            .Message.ShouldContain("Embeddings.Model");
    }

    [Fact]
    public void Ctor_Throws_When_Endpoint_Invalid()
    {
        var opts = ValidOptions();
        opts.HuggingFace!.Endpoint = "not-a-uri";

        Should.Throw<ArgumentException>(() => Create(opts))
            .Message.ShouldContain("Endpoint");
    }

    [Fact]
    public void Ctor_With_Valid_Options_Constructs_Successfully()
    {
        Should.NotThrow(() => Create(ValidOptions()));
    }

    [Fact]
    public void Ctor_With_Custom_Endpoint_Constructs_Successfully()
    {
        var opts = ValidOptions();
        opts.HuggingFace!.Endpoint = "https://api-inference.huggingface.co";

        Should.NotThrow(() => Create(opts));
    }

    [Fact]
    public void Name_Is_HuggingFace()
    {
        Create(ValidOptions()).Name.ShouldBe("HuggingFace");
    }
}
