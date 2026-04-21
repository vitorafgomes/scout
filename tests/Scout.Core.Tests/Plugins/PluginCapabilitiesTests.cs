using Scout.Core.Plugins;

namespace Scout.Core.Tests.Plugins;

public class PluginCapabilitiesTests
{
    [Fact]
    public void Default_Constructor_Produces_ReadOnly_True()
    {
        var caps = new PluginCapabilities();

        caps.IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public void Static_ReadOnly_Instance_Is_ReadOnly()
    {
        PluginCapabilities.ReadOnly.IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public void Cannot_Construct_With_Write_Declaration()
    {
        Action create = () => new PluginCapabilities(isReadOnly: false);

        create.Should().Throw<InvalidOperationException>()
            .WithMessage("*read-only*");
    }
}
