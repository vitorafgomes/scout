using Microsoft.Extensions.DependencyInjection;
using Scout.Core.Plugins;

namespace Scout.Plugin.CodeQA.Tests;

public class CodeQAPluginTests
{
    [Fact]
    public void Name_Is_CodeQA()
    {
        new CodeQAPlugin().Name.Should().Be("CodeQA");
    }

    [Fact]
    public void Capabilities_Are_ReadOnly()
    {
        new CodeQAPlugin().Capabilities.IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public void Build_Returns_An_Empty_KernelPlugin_In_Scaffold_Phase()
    {
        var services = new ServiceCollection().BuildServiceProvider();

        var kp = new CodeQAPlugin().Build(services);

        kp.Name.Should().Be("CodeQA");
        kp.FunctionCount.Should().Be(0);
    }

    [Fact]
    public void Build_Throws_When_Services_Null()
    {
        Action act = () => new CodeQAPlugin().Build(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Can_Register_In_PluginRegistry_Because_ReadOnly()
    {
        var registry = new PluginRegistry();

        Action act = () => registry.Register(new CodeQAPlugin());

        act.Should().NotThrow();
        registry.Plugins.Should().ContainSingle()
                .Which.Name.Should().Be("CodeQA");
    }
}
