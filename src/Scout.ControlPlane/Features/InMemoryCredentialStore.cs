using System.Collections.Concurrent;

namespace Scout.ControlPlane.Features;

/// <summary>
/// Process-local fallback for hosts where no OS keyring is reachable
/// (CI runners, containers, sandboxes). The default instance reports
/// <see cref="IsAvailable"/> as false and its <see cref="StoreAsync"/>
/// returns false, so callers fail closed and tell the user the secret was
/// NOT durably stored rather than silently keeping it for the process
/// lifetime. No durable at-rest backing exists yet; wiring a real OS
/// keyring / Electron safeStorage store is tracked as a follow-up.
/// </summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly ConcurrentDictionary<string, string> _entries = new(StringComparer.Ordinal);
    private readonly bool _isAvailable;

    /// <summary>Default ctor; reports the store as unavailable.</summary>
    public InMemoryCredentialStore()
        : this(isAvailable: false)
    {
    }

    /// <summary>
    /// Test-only ctor: lets the test harness flip <c>IsAvailable</c> to
    /// true so the operation contract can be exercised end-to-end.
    /// </summary>
    public InMemoryCredentialStore(bool isAvailable)
    {
        _isAvailable = isAvailable;
    }

    /// <inheritdoc/>
    public bool IsAvailable => _isAvailable;

    /// <inheritdoc/>
    public Task<bool> StoreAsync(string key, string value, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        ct.ThrowIfCancellationRequested();

        // Fail closed: when no durable backend is reachable, report the write
        // as failed instead of silently keeping the secret for the process
        // lifetime only. Nothing is retained so ContainsAsync stays false.
        if (!_isAvailable)
        {
            return Task.FromResult(false);
        }

        _entries[key] = value;
        return Task.FromResult(true);
    }

    /// <inheritdoc/>
    public Task<string?> GetAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(_entries.TryGetValue(key, out var value) ? value : null);
    }

    /// <inheritdoc/>
    public Task DeleteAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ct.ThrowIfCancellationRequested();

        _entries.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> ContainsAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(_entries.ContainsKey(key));
    }
}
