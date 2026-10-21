using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Scout.Host.Worker.Features;

/// <summary>
/// JSON-file backed <see cref="IFeatureStateStore"/>. Writes go through a
/// temp file + atomic move so a crash mid-write cannot corrupt the store.
/// Concurrent writers are serialised through a per-instance semaphore.
/// Mirrors <c>JsonFileLocalRuntimeConfigStore</c>.
/// </summary>
public sealed class JsonFileFeatureStateStore : IFeatureStateStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;
    private readonly ILogger<JsonFileFeatureStateStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Build a store from bound options.</summary>
    public JsonFileFeatureStateStore(
        IOptions<FeatureStateStoreOptions> options,
        ILogger<JsonFileFeatureStateStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _filePath = options.Value.FilePath ?? GetDefaultFilePath();
    }

    /// <summary>Absolute path of the underlying JSON file.</summary>
    public string FilePath => _filePath;

    /// <inheritdoc/>
    public async Task<FeatureState> GetAsync(string featureId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);

        var state = await ReadAsync(ct).ConfigureAwait(false);
        return state.TryGetValue(featureId, out var entry)
            ? Materialise(entry)
            : EmptyState();
    }

    /// <inheritdoc/>
    public async Task SetAsync(string featureId, FeatureState state, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        ArgumentNullException.ThrowIfNull(state);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = await ReadAsync(ct).ConfigureAwait(false);
            current[featureId] = new FeatureEntry(
                state.Enabled,
                state.LastError,
                SerialiseSettings(state.Settings));
            await WriteAsync(current, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, FeatureState>> GetAllAsync(CancellationToken ct)
    {
        var state = await ReadAsync(ct).ConfigureAwait(false);
        var snapshot = new Dictionary<string, FeatureState>(state.Count, StringComparer.Ordinal);
        foreach (var (id, entry) in state)
        {
            snapshot[id] = Materialise(entry);
        }

        return snapshot;
    }

    private static FeatureState Materialise(FeatureEntry entry) =>
        new(
            entry.Enabled ?? false,
            entry.LastError,
            entry.Settings is null
                ? EmptySettings()
                : new Dictionary<string, string?>(entry.Settings, StringComparer.Ordinal));

    private static FeatureState EmptyState() =>
        new(false, null, EmptySettings());

    private static Dictionary<string, string?> EmptySettings() =>
        new(StringComparer.Ordinal);

    // The dictionary is serialised only when it carries values; an empty
    // collection is dropped so legacy state files stay byte-identical.
    private static Dictionary<string, string?>? SerialiseSettings(
        IReadOnlyDictionary<string, string?> settings) =>
        settings.Count == 0
            ? null
            : new Dictionary<string, string?>(settings, StringComparer.Ordinal);

    /// <inheritdoc/>
    public void Dispose() => _gate.Dispose();

    private async Task<Dictionary<string, FeatureEntry>> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
        {
            return new Dictionary<string, FeatureEntry>(StringComparer.Ordinal);
        }

        try
        {
            await using var stream = new FileStream(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            var parsed = await JsonSerializer
                .DeserializeAsync<Dictionary<string, FeatureEntry>>(stream, JsonOptions, ct)
                .ConfigureAwait(false);
            return parsed is null
                ? new Dictionary<string, FeatureEntry>(StringComparer.Ordinal)
                : new Dictionary<string, FeatureEntry>(parsed, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Feature state file at {Path} is malformed or unreadable; falling back to defaults",
                _filePath);
            return new Dictionary<string, FeatureEntry>(StringComparer.Ordinal);
        }
    }

    private async Task WriteAsync(Dictionary<string, FeatureEntry> state, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = _filePath + ".tmp";

        await using (var stream = new FileStream(
            tempPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, state, JsonOptions, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        // Atomic on the same volume on every supported platform.
        File.Move(tempPath, _filePath, overwrite: true);
    }

    private static string GetDefaultFilePath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "Scout", "features.json");
    }

    private sealed record FeatureEntry(
        [property: JsonPropertyName("enabled")] bool? Enabled,
        [property: JsonPropertyName("lastError")] string? LastError,
        [property: JsonPropertyName("settings")] Dictionary<string, string?>? Settings);
}
