using Microsoft.Extensions.Options;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.AzureAIInference;

namespace Scout.Llm.Tests.Providers.AzureAIInference;

public sealed class AzureAIInferenceLlmProviderFactoryValidationTests
{
    private static LlmOptions ValidOptions() => new()
    {
        Provider = "AzureAIInference",
        AzureAIInference = new AzureAIInferenceOptions
        {
            ApiKey = "test-key",
            Endpoint = "https://my-inference.services.ai.azure.com/models",
            Chat = new ChatModelOptions { Model = "Phi-3.5-MoE-instruct" },
            Embeddings = new EmbeddingModelOptions { Model = "text-embedding-3-small" },
        },
    };

    private static AzureAIInferenceLlmProviderFactory Create(LlmOptions options)
        => new(Options.Create(options));

    [Fact]
    public void Ctor_Throws_When_Options_Null()
    {
        Should.Throw<ArgumentNullException>(() => new AzureAIInferenceLlmProviderFactory(null!));
    }

    [Fact]
    public void Ctor_Throws_When_Options_Value_Null()
    {
        Should.Throw<ArgumentException>(() => Create(null!));
    }

    [Fact]
    public void Ctor_Throws_When_AzureAIInference_Null()
    {
        var opts = ValidOptions();
        opts.AzureAIInference = null;

        Should.Throw<ArgumentException>(() => Create(opts))
            .Message.ShouldContain("AzureAIInference");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Ctor_Throws_When_ApiKey_Null_Or_Whitespace(string? apiKey)
    {
        var opts = ValidOptions();
        opts.AzureAIInference!.ApiKey = apiKey!;

        Should.Throw<ArgumentException>(() => Create(opts))
            .Message.ShouldContain("ApiKey");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Ctor_Throws_When_Endpoint_Null_Or_Whitespace(string? endpoint)
    {
        var opts = ValidOptions();
        opts.AzureAIInference!.Endpoint = endpoint!;

        Should.Throw<ArgumentException>(() => Create(opts))
            .Message.ShouldContain("Endpoint");
    }

    [Fact]
    public void Ctor_Throws_When_Endpoint_Invalid()
    {
        var opts = ValidOptions();
        opts.AzureAIInference!.Endpoint = "not-a-uri";

        Should.Throw<ArgumentException>(() => Create(opts))
            .Message.ShouldContain("Endpoint");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Ctor_Throws_When_Chat_Model_Null_Or_Whitespace(string? model)
    {
        var opts = ValidOptions();
        opts.AzureAIInference!.Chat.Model = model!;

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
        opts.AzureAIInference!.Embeddings.Model = model!;

        Should.Throw<ArgumentException>(() => Create(opts))
            .Message.ShouldContain("Embeddings.Model");
    }

    [Fact]
    public void Ctor_With_Valid_Options_Constructs_Successfully()
    {
        Should.NotThrow(() => Create(ValidOptions()));
    }

    [Fact]
    public void Name_Is_AzureAIInference()
    {
        Create(ValidOptions()).Name.ShouldBe("AzureAIInference");
    }
}
