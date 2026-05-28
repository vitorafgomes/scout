using Microsoft.Extensions.DependencyInjection;
using Scout.Core.Plugins;
using Scout.Core.Tests.Fakes;

namespace Scout.Core.Tests.Plugins;

public class PluginRegistryTests
{
    private static IServiceProvider EmptyServices() =>
        new ServiceCollection().BuildServiceProvider();

    [Fact]
    public void Register_Read_Only_Plugin_Succeeds()
    {
        var registry = new PluginRegistry();

        registry.Register(new FakeReadOnlyPlugin());

        registry.Plugins.ShouldHaveSingleItem()
            .Name.ShouldBe("fake-read-only");
    }

    [Fact]
    public void Register_Null_Plugin_Throws_ArgumentNullException()
    {
        var registry = new PluginRegistry();

        Action register = () => registry.Register(null!);

        register.ShouldThrow<ArgumentNullException>();
    }

    [Fact]
    public void Register_Plugin_With_Null_Capabilities_Throws_ReadOnlyViolation()
    {
        var registry = new PluginRegistry();
        var plugin = new FakeNullCapabilitiesPlugin();

        Action register = () => registry.Register(plugin);

        register.ShouldThrow<ReadOnlyViolationException>()
            .PluginName.ShouldBe("fake-null-caps");
    }

    [Fact]
    public void Register_Duplicate_Name_Throws()
    {
        var registry = new PluginRegistry();
        registry.Register(new FakeReadOnlyPlugin());

        Action register = () => registry.Register(new FakeReadOnlyPlugin());

        register.ShouldThrow<InvalidOperationException>()
            .Message!.ShouldContain("already registered");
    }

    [Fact]
    public void Register_Is_Case_Sensitive_On_Name()
    {
        var registry = new PluginRegistry();
        registry.Register(new FakeReadOnlyPlugin(name: "cloud-status"));

        // Same name with different casing is treated as a distinct plugin
        // so we don't accidentally collapse case-sensitive config keys.
        Action register = () =>
            registry.Register(new FakeReadOnlyPlugin(name: "Cloud-Status"));

        register.ShouldNotThrow();
        registry.Plugins.Count().ShouldBe(2);
    }

    [Fact]
    public void BuildAll_Returns_Kernel_Plugins_In_Registration_Order()
    {
        var registry = new PluginRegistry();
        registry.Register(new FakeReadOnlyPlugin(name: "first"));
        registry.Register(new FakeReadOnlyPlugin(name: "second"));

        var built = registry.BuildAll(EmptyServices());

        built.Select(p => p.Name).ShouldBe(new[] { "first", "second" });
    }

    [Fact]
    public void BuildAll_With_Null_Services_Throws()
    {
        var registry = new PluginRegistry();
        registry.Register(new FakeReadOnlyPlugin());

        Action build = () => registry.BuildAll(null!);

        build.ShouldThrow<ArgumentNullException>();
    }
}
