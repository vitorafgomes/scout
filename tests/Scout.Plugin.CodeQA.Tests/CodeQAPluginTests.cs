using Microsoft.Extensions.DependencyInjection;
using Scout.Core.Plugins;
using Scout.Plugin.CodeQA.Composing;
using Scout.Plugin.CodeQA.Functions;
using Scout.Plugin.CodeQA.Retrieval;

namespace Scout.Plugin.CodeQA.Tests;

public class CodeQAPluginTests
{
    [Fact]
    public void Name_Is_CodeQA()
    {
        new CodeQAPlugin().Name.ShouldBe("CodeQA");
    }

    [Fact]
    public void Capabilities_Are_ReadOnly()
    {
        new CodeQAPlugin().Capabilities.IsReadOnly.ShouldBeTrue();
    }

    [Fact]
    public void Build_Exposes_CodeQAFunctions()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IRetriever>());
        services.AddSingleton(Substitute.For<IComposer>());
        services.AddSingleton<CodeQAFunctions>();
        using var provider = services.BuildServiceProvider();

        var kp = new CodeQAPlugin().Build(provider);

        kp.Name.ShouldBe("CodeQA");
        kp.FunctionCount.ShouldBe(2);
        kp.Select(f => f.Name).ShouldBe(new[] { "ask_code", "search_code" }, ignoreOrder: true);
    }

    [Fact]
    public void Build_Throws_When_Services_Null()
    {
        Action act = () => new CodeQAPlugin().Build(null!);

        act.ShouldThrow<ArgumentNullException>();
    }

    [Fact]
    public void Build_Throws_When_CodeQAFunctions_Missing_From_DI()
    {
        var provider = new ServiceCollection().BuildServiceProvider();

        Action act = () => new CodeQAPlugin().Build(provider);

        act.ShouldThrow<InvalidOperationException>();
    }

    [Fact]
    public void Can_Register_In_PluginRegistry_Because_ReadOnly()
    {
        var registry = new PluginRegistry();

        Action act = () => registry.Register(new CodeQAPlugin());

        act.ShouldNotThrow();
        registry.Plugins.ShouldHaveSingleItem()
                .Name.ShouldBe("CodeQA");
    }
}
