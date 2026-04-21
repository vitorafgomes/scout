using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Scout.Core.Llm;

namespace Scout.Llm.Ollama.Tests;

public class OllamaServiceCollectionExtensionsTests
{
    [Fact]
    public void AddOllamaLlmProvider_Registers_Factory()
    {
        var services = new ServiceCollection();
        services.Configure<LlmOptions>(_ => { });

        services.AddOllamaLlmProvider();

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ILlmProviderFactory>();

        factory.Should().BeOfType<OllamaLlmProviderFactory>();
        factory.Name.Should().Be("Ollama");
    }

    [Fact]
    public void AddOllamaLlmProvider_Null_Services_Throws()
    {
        Action act = () =>
            OllamaServiceCollectionExtensions.AddOllamaLlmProvider(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
