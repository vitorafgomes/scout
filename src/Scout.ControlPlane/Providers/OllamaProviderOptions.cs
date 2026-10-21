namespace Scout.ControlPlane.Providers;

/// <summary>
/// Options for the local Ollama provider integration. Bound from
/// <c>Scout:Providers:Ollama</c>.
/// </summary>
public sealed class OllamaProviderOptions
{
    public const string SectionName = "Scout:Providers:Ollama";

    /// <summary>Base URL of the local Ollama HTTP API.</summary>
    public string Endpoint { get; set; } = "http://localhost:11434";

    /// <summary>
    /// Hard ceiling for the <c>/api/tags</c> probe. Mirrored on the
    /// HttpClient timeout so callers cannot exceed it even by mistake.
    /// </summary>
    public TimeSpan PingTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Maximum time the start path will wait for the spawned daemon to
    /// become reachable via <c>/api/tags</c>.
    /// </summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Hard cap on how long the autostart hosted service may block host
    /// startup while waiting for Ollama to come up.
    /// </summary>
    public TimeSpan WarmupBudget { get; set; } = TimeSpan.FromSeconds(5);
}
