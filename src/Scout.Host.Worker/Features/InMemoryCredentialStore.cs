using System.Collections.Concurrent;

namespace Scout.Host.Worker.Features;

/// <summary>
/// Process-local fallback for hosts where no OS keyring is reachable
/// (CI runners, containers, sandboxes). <see cref="IsAvailable"/> is
/// false so endpoints can warn the user instead of silently persisting
/// secrets only for the lifetime of the process.
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
    /// true so the endpoint contract can be exercised end-to-end.
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
