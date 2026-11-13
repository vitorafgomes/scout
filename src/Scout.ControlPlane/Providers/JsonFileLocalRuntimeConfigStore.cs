using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Scout.ControlPlane.Providers;

/// <summary>
/// JSON-file backed <see cref="ILocalRuntimeConfigStore"/>. Writes go through
/// a temp file + atomic move so a crash mid-write cannot corrupt the store.
/// Every read and write runs under a per-instance semaphore. A file that does
/// not parse is moved aside to <c>local-runtimes.json.corrupt-&lt;UTC yyyyMMddHHmmss&gt;</c>
/// and the store starts from defaults; a file that cannot be opened or read
/// fails the call, and a set whose read failed writes nothing.
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
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonFileLocalRuntimeConfigStore(
        IOptions<LocalRuntimeConfigStoreOptions> options,
        ILogger<JsonFileLocalRuntimeConfigStore> logger,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? TimeProvider.System;
        _filePath = options.Value.FilePath ?? GetDefaultFilePath();
    }

    /// <summary>Absolute path of the underlying JSON file.</summary>
    public string FilePath => _filePath;

    public async Task<bool> GetAutostartAsync(string runtimeId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeId);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await ReadAsync(ct).ConfigureAwait(false);
            return !state.TryGetValue(runtimeId, out var entry) || entry.Autostart is not false;
        }
        finally
        {
            _gate.Release();
        }
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
                .ConfigureAwait(false) ?? throw new JsonException("The local runtime config file holds null.");
            return new Dictionary<string, RuntimeEntry>(parsed, StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            var asidePath = CorruptFile.MoveAside(_filePath, _time);
            _logger.LogWarning(
                ex,
                "Local runtime config at {Path} is malformed; moved it to {AsidePath} and started from defaults",
                _filePath,
                asidePath);
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
