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

    // NOTE: there is no negative "cannot construct write-capable" test here.
    // The type is designed so that write capabilities are unrepresentable —
    // the compiler forbids it, not runtime. The registry-level defense
    // against null / non-read-only capabilities is tested in PluginRegistryTests.
}
