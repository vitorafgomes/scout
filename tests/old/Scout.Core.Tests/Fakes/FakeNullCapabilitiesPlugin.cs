using Microsoft.SemanticKernel;
using Scout.Core.Plugins;

namespace Scout.Core.Tests.Fakes;

/// <summary>
/// A plugin that returns null capabilities. Used to exercise the registry's
/// defense against a malicious or buggy plugin that bypasses the normal
/// PluginCapabilities constructor. The registry must reject it.
/// </summary>
public sealed class FakeNullCapabilitiesPlugin : IScoutPlugin
{
    public string Name => "fake-null-caps";

    public PluginCapabilities Capabilities => null!;

    public KernelPlugin Build(IServiceProvider services) =>
        KernelPluginFactory.CreateFromFunctions(Name, description: null, functions: []);
}
