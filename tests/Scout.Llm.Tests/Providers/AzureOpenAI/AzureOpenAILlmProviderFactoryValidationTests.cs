using Microsoft.Extensions.Options;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.AzureOpenAI;

namespace Scout.Llm.Tests.Providers.AzureOpenAI;

public sealed class AzureOpenAILlmProviderFactoryValidationTests
{
    private static LlmOptions ValidOptions() => new()
    {
        Provider = "AzureOpenAI",
        AzureOpenAI = new AzureOpenAIOptions
        {
            ApiKey = "test-key",
            Endpoint = "https://my-aoai.openai.azure.com",
            Chat = new AzureDeploymentOptions { Deployment = "gpt-4o" },
            Embeddings = new AzureDeploymentOptions { Deployment = "text-embedding-3-small" },
        },
    };

    private static AzureOpenAILlmProviderFactory Create(LlmOptions options)
        => new(Options.Create(options));

    [Fact]
    public void Ctor_Throws_When_Options_Null()
    {
        Should.Throw<ArgumentNullException>(() => new AzureOpenAILlmProviderFactory(null!));
    }

    [Fact]
    public void Ctor_Throws_When_Options_Value_Null()
    {
        Should.Throw<ArgumentException>(() => Create(null!));
    }

    [Fact]
    public void Ctor_Throws_When_AzureOpenAI_Null()
    {
        var opts = ValidOptions();
        opts.AzureOpenAI = null;

        Should.Throw<ArgumentException>(() => Create(opts))
            .Message.ShouldContain("AzureOpenAI");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Ctor_Throws_When_ApiKey_Null_Or_Whitespace(string? apiKey)
    {
        var opts = ValidOptions();
        opts.AzureOpenAI!.ApiKey = apiKey!;

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
        opts.AzureOpenAI!.Endpoint = endpoint!;

        Should.Throw<ArgumentException>(() => Create(opts))
            .Message.ShouldContain("Endpoint");
    }

    [Fact]
    public void Ctor_Throws_When_Endpoint_Invalid()
    {
        var opts = ValidOptions();
        opts.AzureOpenAI!.Endpoint = "not-a-uri";

        Should.Throw<ArgumentException>(() => Create(opts))
            .Message.ShouldContain("Endpoint");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Ctor_Throws_When_Chat_Deployment_Null_Or_Whitespace(string? deployment)
    {
        var opts = ValidOptions();
        opts.AzureOpenAI!.Chat.Deployment = deployment!;

        Should.Throw<ArgumentException>(() => Create(opts))
            .Message.ShouldContain("Chat.Deployment");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Ctor_Throws_When_Embeddings_Deployment_Null_Or_Whitespace(string? deployment)
    {
        var opts = ValidOptions();
        opts.AzureOpenAI!.Embeddings.Deployment = deployment!;

        Should.Throw<ArgumentException>(() => Create(opts))
            .Message.ShouldContain("Embeddings.Deployment");
    }

    [Fact]
    public void Ctor_With_Valid_Options_Constructs_Successfully()
    {
        Should.NotThrow(() => Create(ValidOptions()));
    }

    [Fact]
    public void Name_Is_AzureOpenAI()
    {
        Create(ValidOptions()).Name.ShouldBe("AzureOpenAI");
    }
}
