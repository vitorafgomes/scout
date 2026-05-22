namespace Scout.Host.Worker.Providers;

/// <summary>Structured error payload returned by provider endpoints.</summary>
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

/// <summary>Body of <c>PUT /providers/{id}/autostart</c>.</summary>
public sealed record AutostartRequest(bool Enabled);
