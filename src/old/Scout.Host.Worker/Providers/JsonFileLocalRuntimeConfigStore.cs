using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Scout.Host.Worker.Providers;

/// <summary>
/// JSON-file backed <see cref="ILocalRuntimeConfigStore"/>. Writes go through
/// a temp file + atomic move so a crash mid-write cannot corrupt the store.
/// Concurrent writers are serialised through a per-instance semaphore.
/// </summary>
public sealed class JsonFileLocalRuntimeConfigStore : ILocalRuntimeConfigStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;
    private readonly ILogger<JsonFileLocalRuntimeConfigStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonFileLocalRuntimeConfigStore(
        IOptions<LocalRuntimeConfigStoreOptions> options,
        ILogger<JsonFileLocalRuntimeConfigStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _filePath = options.Value.FilePath ?? GetDefaultFilePath();
    }

    /// <summary>Absolute path of the underlying JSON file.</summary>
    public string FilePath => _filePath;

    public async Task<bool> GetAutostartAsync(string runtimeId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeId);

        var state = await ReadAsync(ct).ConfigureAwait(false);
        if (state.TryGetValue(runtimeId, out var entry) && entry.Autostart is { } flag)
        {
            return flag;
        }

        return true;
    }

    public async Task SetAutostartAsync(string runtimeId, bool enabled, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeId);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await ReadAsync(ct).ConfigureAwait(false);
            state[runtimeId] = new RuntimeEntry(enabled);
            await WriteAsync(state, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<Dictionary<string, RuntimeEntry>> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
        {
            return new Dictionary<string, RuntimeEntry>(StringComparer.Ordinal);
        }

        try
        {
            await using var stream = new FileStream(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            var parsed = await JsonSerializer
                .DeserializeAsync<Dictionary<string, RuntimeEntry>>(stream, JsonOptions, ct)
                .ConfigureAwait(false);
            return parsed is null
                ? new Dictionary<string, RuntimeEntry>(StringComparer.Ordinal)
                : new Dictionary<string, RuntimeEntry>(parsed, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Local runtime config at {Path} is malformed or unreadable; falling back to defaults",
                _filePath);
            return new Dictionary<string, RuntimeEntry>(StringComparer.Ordinal);
        }
    }

    private async Task WriteAsync(Dictionary<string, RuntimeEntry> state, CancellationToken ct)
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

        // File.Move with overwrite is atomic on the same volume on every
        // supported platform - the destination either points at the old or
        // the new inode, never at a half-written buffer.
        File.Move(tempPath, _filePath, overwrite: true);
    }

    private static string GetDefaultFilePath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "Scout", "local-runtimes.json");
    }

    private sealed record RuntimeEntry(
        [property: JsonPropertyName("autostart")] bool? Autostart);
}
