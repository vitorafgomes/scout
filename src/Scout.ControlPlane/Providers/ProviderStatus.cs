namespace Scout.ControlPlane.Providers;

/// <summary>Structured error payload returned by provider operations.</summary>
public sealed record ProviderError(string Code, string Message);

/// <summary>A single model exposed by a local runtime provider.</summary>
public sealed record ProviderModel(string Name, long SizeBytes, string ModifiedAt);

/// <summary>
/// Snapshot describing a local runtime provider (Ollama, LM Studio, ...).
/// Models is empty when the provider daemon is not reachable.
/// </summary>
public sealed record ProviderStatus(
    string Id,
    bool Installed,
    bool Running,
    string? Endpoint,
    string? Version,
    bool ManagedByWorker,
    bool Autostart,
    bool SupportsLifecycle,
    ProviderError? LastError,
    IReadOnlyList<ProviderModel> Models);
