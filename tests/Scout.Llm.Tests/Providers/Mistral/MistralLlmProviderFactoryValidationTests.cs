using Microsoft.Extensions.Options;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.Mistral;

namespace Scout.Llm.Tests.Providers.Mistral;

public sealed class MistralLlmProviderFactoryValidationTests
{
    private static LlmOptions ValidOptions() => new()
    {
        Provider = "Mistral",
        Mistral = new MistralOptions
        {
            ApiKey = "test-key",
            Chat = new ChatModelOptions { Model = "mistral-small-latest" },
            Embeddings = new EmbeddingModelOptions { Model = "mistral-embed" },
        },
    };

    private static MistralLlmProviderFactory Create(LlmOptions options)
        => new(Options.Create(options));

    [Fact]
    public void Ctor_Throws_When_Options_Null()
    {
        Should.Throw<ArgumentNullException>(() => new MistralLlmProviderFactory(null!));
    }

    [Fact]
    public void Ctor_Throws_When_Options_Value_Null()
    {
        Should.Throw<ArgumentException>(() => Create(null!));
    }

    [Fact]
    public void Ctor_Throws_When_Mistral_Null()
    {
        var opts = ValidOptions();
        opts.Mistral = null;

        Should.Throw<ArgumentException>(() => Create(opts))
            .Message.ShouldContain("Mistral");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Ctor_Throws_When_ApiKey_Null_Or_Whitespace(string? apiKey)
    {
        var opts = ValidOptions();
        opts.Mistral!.ApiKey = apiKey!;

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
        opts.Mistral!.Chat.Model = model!;

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
        opts.Mistral!.Embeddings.Model = model!;

        Should.Throw<ArgumentException>(() => Create(opts))
            .Message.ShouldContain("Embeddings.Model");
    }

    [Fact]
    public void Ctor_Throws_When_Endpoint_Invalid()
    {
        var opts = ValidOptions();
        opts.Mistral!.Endpoint = "not-a-uri";

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
        opts.Mistral!.Endpoint = "https://api.mistral.ai";

        Should.NotThrow(() => Create(opts));
    }

    [Fact]
    public void Name_Is_Mistral()
    {
        Create(ValidOptions()).Name.ShouldBe("Mistral");
    }
}
