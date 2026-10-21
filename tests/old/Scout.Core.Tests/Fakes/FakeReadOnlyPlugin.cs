using Microsoft.SemanticKernel;
using Scout.Core.Plugins;

namespace Scout.Core.Tests.Fakes;

/// <summary>
/// A minimal valid plugin used by registry tests. Declares read-only
/// capabilities and produces an empty Semantic Kernel plugin.
/// </summary>
public sealed class FakeReadOnlyPlugin : IScoutPlugin
{
    private readonly string _name;

    public FakeReadOnlyPlugin(string name = "fake-read-only")
    {
        _name = name;
    }

    public string Name => _name;

    public PluginCapabilities Capabilities => PluginCapabilities.ReadOnly;

    public KernelPlugin Build(IServiceProvider services) =>
        KernelPluginFactory.CreateFromFunctions(_name, description: null, functions: []);
}
