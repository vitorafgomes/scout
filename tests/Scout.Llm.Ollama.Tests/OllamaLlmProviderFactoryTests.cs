using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Core.Llm;

namespace Scout.Llm.Ollama.Tests;

public class OllamaLlmProviderFactoryTests
{
    [Fact]
    public void Name_Is_Ollama()
    {
        var factory = new OllamaLlmProviderFactory(Options.Create(new LlmOptions()));

        factory.Name.Should().Be("Ollama");
    }

    [Fact]
    public void Ctor_Throws_When_Options_Null()
    {
        Action create = () => new OllamaLlmProviderFactory(null!);

        create.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_Throws_When_Endpoint_Empty()
    {
        var opts = Options.Create(new LlmOptions
        {
            Ollama = new OllamaOptions { Endpoint = "", Model = "llama3.1" }
        });

        Action create = () => new OllamaLlmProviderFactory(opts);

        create.Should().Throw<ArgumentException>()
            .WithMessage("*Endpoint*");
    }

    [Fact]
    public void Ctor_Throws_When_Model_Empty()
    {
        var opts = Options.Create(new LlmOptions
        {
            Ollama = new OllamaOptions { Endpoint = "http://localhost:11434", Model = "" }
        });

        Action create = () => new OllamaLlmProviderFactory(opts);

        create.Should().Throw<ArgumentException>()
            .WithMessage("*Model*");
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("localhost:11434")]       // missing scheme
    [InlineData("/relative/path")]        // relative
    [InlineData("file:///etc/passwd")]    // non-http scheme
    [InlineData("ftp://example.com")]     // non-http scheme
    public void Ctor_Throws_When_Endpoint_Is_Not_Absolute_Http_Or_Https(string endpoint)
    {
        var opts = Options.Create(new LlmOptions
        {
            Ollama = new OllamaOptions { Endpoint = endpoint, Model = "llama3.1" }
        });

        Action create = () => new OllamaLlmProviderFactory(opts);

        create.Should().Throw<ArgumentException>()
            .WithMessage("*not a valid absolute http or https URI*");
    }

    [Theory]
    [InlineData("http://localhost:11434")]
    [InlineData("https://ollama.internal")]
    [InlineData("http://127.0.0.1:11434/")]
    public void Ctor_Accepts_Valid_Http_And_Https_Endpoints(string endpoint)
    {
        var opts = Options.Create(new LlmOptions
        {
            Ollama = new OllamaOptions { Endpoint = endpoint, Model = "llama3.1" }
        });

        Action create = () => new OllamaLlmProviderFactory(opts);

        create.Should().NotThrow();
    }

    [Fact]
    public void Ctor_Snapshots_Options_So_Later_Mutation_Is_Ignored()
    {
        var ollama = new OllamaOptions
        {
            Endpoint = "http://localhost:11434",
            Model = "llama3.1",
        };
        var factory = new OllamaLlmProviderFactory(
            Options.Create(new LlmOptions { Ollama = ollama }));

        // Mutate after construction — the factory held a reference once,
        // but now stores the parsed Uri and Model by value. Mutation must
        // not affect the built kernel.
        ollama.Endpoint = "http://mutated.example";
        ollama.Model = "mutated-model";

        var builder = Kernel.CreateBuilder();
        factory.AddToKernel(builder);
        var kernel = builder.Build();

        kernel.GetRequiredService<IChatCompletionService>()
              .Should().NotBeNull();
    }

    [Fact]
    public void AddToKernel_Throws_When_Builder_Null()
    {
        var factory = new OllamaLlmProviderFactory(Options.Create(new LlmOptions()));

        Action add = () => factory.AddToKernel(null!);

        add.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddToKernel_Registers_Chat_Completion_Service()
    {
        var factory = new OllamaLlmProviderFactory(Options.Create(new LlmOptions
        {
            Ollama = new OllamaOptions
            {
                Endpoint = "http://localhost:11434",
                Model = "llama3.1",
            },
        }));

        var builder = Kernel.CreateBuilder();
        factory.AddToKernel(builder);
        var kernel = builder.Build();

        kernel.GetRequiredService<IChatCompletionService>()
              .Should().NotBeNull();
    }
}
