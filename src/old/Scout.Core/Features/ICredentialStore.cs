namespace Scout.Core.Features;

/// <summary>
/// Opaque key/value secret store. Backed by the OS keyring on supported
/// platforms (libsecret on Linux, Keychain on macOS, Windows Credential
/// Manager on Windows). Falls back to an in-memory store when no native
/// backend is reachable.
/// </summary>
public interface ICredentialStore
{
    /// <summary>
    /// True when the underlying backend is reachable. A false value lets
    /// the worker degrade gracefully (Features tab still renders) instead
    /// of refusing to boot.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Stores <paramref name="value"/> under <paramref name="key"/>.
    /// Returns false when the backend is unavailable.
    /// </summary>
    Task<bool> StoreAsync(string key, string value, CancellationToken ct);

    /// <summary>Reads a previously stored secret, or null when absent.</summary>
    Task<string?> GetAsync(string key, CancellationToken ct);

    /// <summary>Removes a secret. No-op when the key does not exist.</summary>
    Task DeleteAsync(string key, CancellationToken ct);

    /// <summary>True when a secret for <paramref name="key"/> exists.</summary>
    Task<bool> ContainsAsync(string key, CancellationToken ct);
}
