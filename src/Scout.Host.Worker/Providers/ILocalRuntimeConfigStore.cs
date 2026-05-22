namespace Scout.Host.Worker.Providers;

/// <summary>
/// Persists per-runtime user preferences (currently just the autostart flag)
/// in the user's local app data directory. Missing entries default to
/// <c>autostart = true</c>.
/// </summary>
public interface ILocalRuntimeConfigStore
{
    /// <summary>Reads the autostart flag for <paramref name="runtimeId"/>.</summary>
    Task<bool> GetAutostartAsync(string runtimeId, CancellationToken ct);

    /// <summary>Persists the autostart flag for <paramref name="runtimeId"/>.</summary>
    Task SetAutostartAsync(string runtimeId, bool enabled, CancellationToken ct);
}

/// <summary>
/// Options for <see cref="JsonFileLocalRuntimeConfigStore"/>. Bound from
/// <c>Scout:LocalRuntime:Config</c>. Tests set <see cref="FilePath"/> to
/// point at a disposable temp file.
/// </summary>
public sealed class LocalRuntimeConfigStoreOptions
{
    public const string SectionName = "Scout:LocalRuntime:Config";

    /// <summary>
    /// Absolute path of the JSON file. When null the store falls back to
    /// <c>%LocalAppData%/Scout/local-runtimes.json</c>.
    /// </summary>
    public string? FilePath { get; set; }
}
