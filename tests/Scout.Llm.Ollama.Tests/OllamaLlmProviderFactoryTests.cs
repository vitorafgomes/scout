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
