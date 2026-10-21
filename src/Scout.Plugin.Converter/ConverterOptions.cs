namespace Scout.Plugin.Converter;

/// <summary>
/// Configuration for the Converter plugin, bound from "Scout:Plugins:Converter".
/// The plugin self-gates on <see cref="Enabled"/>: when disabled the registration
/// is a clean no-op (no kernel plugin), so an unconfigured host still starts.
/// </summary>
public sealed class ConverterOptions
{
    /// <summary>
    /// Whether the plugin is active. Defaults to true. Set to false to disable it
    /// without removing configuration.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum length, in characters, of an arithmetic expression accepted by
    /// <c>calculate</c>. A defensive cap against pathological input; expressions
    /// longer than this yield a corrective sentence rather than being evaluated.
    /// Defaults to 512.
    /// </summary>
    public int MaxExpressionLength { get; set; } = 512;
}
