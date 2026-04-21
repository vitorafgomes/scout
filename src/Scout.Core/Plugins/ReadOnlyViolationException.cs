namespace Scout.Core.Plugins;

/// <summary>
/// Thrown when a plugin is registered without declaring <see cref="PluginCapabilities"/>
/// that are read-only. Failing at startup — not at runtime — is an explicit
/// design choice: a misbehaving plugin cannot reach production undetected.
/// </summary>
public sealed class ReadOnlyViolationException : InvalidOperationException
{
    public ReadOnlyViolationException(string pluginName)
        : base(
            $"Plugin '{pluginName}' violates the read-only invariant: " +
            $"its Capabilities are null or not read-only.")
    {
        PluginName = pluginName;
    }

    public string PluginName { get; }
}
