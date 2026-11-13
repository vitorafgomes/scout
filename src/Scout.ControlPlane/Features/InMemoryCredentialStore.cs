using System.Collections.Concurrent;

namespace Scout.ControlPlane.Features;

/// <summary>
/// Keeps secrets in memory: a stored secret lasts until Scout closes. <see cref="IsAvailable"/> is false to say so, and
/// <see cref="StoreAsync"/> still stores and returns true, so a feature can be set up and turned on in the same run.
/// </summary>
/// <remarks>
/// The constructor that takes <c>isAvailable</c> is for tests of a store that reports itself available.
/// </remarks>
public sealed class InMemoryCredentialStore(bool isAvailable) : ICredentialStore
{
    private readonly ConcurrentDictionary<string, string> _entries = new(StringComparer.Ordinal);
    private readonly bool _isAvailable = isAvailable;

    /// <summary>Default ctor; reports the store as unavailable.</summary>
    public InMemoryCredentialStore()
        : this(isAvailable: false)
    {
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
