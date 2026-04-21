namespace Scout.Core.Hosting;

/// <summary>
/// Top-level Scout configuration, bound from the "Scout" configuration section.
/// </summary>
public sealed class ScoutOptions
{
    /// <summary>Selected chat adapter name, e.g. "Discord".</summary>
    public string ChatAdapter { get; set; } = "Discord";

    /// <summary>Selected LLM provider factory name, e.g. "Ollama".</summary>
    public string LlmProvider { get; set; } = "Ollama";

    /// <summary>Names of plugins that should be loaded at startup.</summary>
    public IList<string> EnabledPlugins { get; set; } = new List<string>();
}
