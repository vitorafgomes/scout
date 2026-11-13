namespace Scout.ControlPlane.Features;

/// <summary>
/// Opaque key/value secret store. The store Scout ships keeps values in memory; one backed by the
/// operating system's keyring is not built yet.
/// </summary>
public interface ICredentialStore
{
    /// <summary>
    /// True when a stored secret is kept after Scout closes. False means a stored secret lasts only while Scout
    /// runs; the store still accepts it.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Stores <paramref name="value"/> under <paramref name="key"/>. Returns false only when the value was not stored at all.
    /// A store whose <see cref="IsAvailable"/> is false stores it for as long as Scout runs and returns true.
    /// </summary>
    Task<bool> StoreAsync(string key, string value, CancellationToken ct);

    /// <summary>Reads a previously stored secret, or null when absent.</summary>
    Task<string?> GetAsync(string key, CancellationToken ct);

    /// <summary>Removes a secret. No-op when the key does not exist.</summary>
    Task DeleteAsync(string key, CancellationToken ct);

    /// <summary>True when a secret for <paramref name="key"/> exists.</summary>
    Task<bool> ContainsAsync(string key, CancellationToken ct);
}
