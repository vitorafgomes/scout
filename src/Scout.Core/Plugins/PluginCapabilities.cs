namespace Scout.Core.Plugins;

/// <summary>
/// Declared capabilities of a Scout plugin. The read-only invariant is
/// enforced at construction: declaring write capabilities is not possible.
/// </summary>
public sealed class PluginCapabilities
{
    public PluginCapabilities(bool isReadOnly = true)
    {
        if (!isReadOnly)
        {
            throw new InvalidOperationException(
                "Scout plugins must be read-only. " +
                "Declaring write capabilities is not allowed.");
        }

        IsReadOnly = true;
    }

    public bool IsReadOnly { get; }

    public static PluginCapabilities ReadOnly { get; } = new(isReadOnly: true);
}
