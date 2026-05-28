using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Scout.Core.Plugins;

namespace Scout.Plugin.CloudStatus;

/// <summary>
/// Scout feature plugin that exposes read-only cloud health queries for
/// Azure, AWS, and Oracle Cloud through <see cref="CloudStatusFunctions"/>.
/// </summary>
public sealed class CloudStatusPlugin : IScoutPlugin
{
    public const string PluginName = "CloudStatus";

    public string Name => PluginName;

    public PluginCapabilities Capabilities => PluginCapabilities.ReadOnly;

    public KernelPlugin Build(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var functions = services.GetRequiredService<CloudStatusFunctions>();
        return KernelPluginFactory.CreateFromObject(functions, PluginName);
    }
}
