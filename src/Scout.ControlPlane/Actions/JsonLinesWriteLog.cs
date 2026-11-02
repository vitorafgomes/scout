using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scout.ControlPlane.Features;

namespace Scout.ControlPlane.Actions;

/// <summary>
/// <see cref="IWriteLog"/> kept in <c>writes.log</c> beside the feature state
/// file, one JSON object per line, appended and never rewritten. A line that
/// does not parse is skipped and logged.
/// </summary>
public sealed class JsonLinesWriteLog : IWriteLog, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _filePath;
    private readonly ILogger<JsonLinesWriteLog> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Build a log next to the feature state file.</summary>
    public JsonLinesWriteLog(
        IOptions<FeatureStateStoreOptions> options,
        ILogger<JsonLinesWriteLog> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _filePath = Path.Combine(
            Path.GetDirectoryName(options.Value.ResolveFilePath()) ?? string.Empty,
            "writes.log");
    }

    /// <summary>Absolute path of the underlying file.</summary>
    public string FilePath => _filePath;

    /// <inheritdoc/>
    public async Task AppendAsync(WriteLogEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var line = JsonSerializer.Serialize(entry, JsonOptions) + "\n";

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.AppendAllTextAsync(_filePath, line, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<WriteLogEntry>> ListAsync(int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        if (!File.Exists(_filePath))
        {
            return [];
        }

        var tail = new Queue<(int Number, string Text)>(limit);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var number = 0;
            await foreach (var line in File.ReadLinesAsync(_filePath, ct).ConfigureAwait(false))
            {
                number++;
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (tail.Count == limit)
                {
                    tail.Dequeue();
                }

                tail.Enqueue((number, line));
            }
        }
        finally
        {
            _gate.Release();
        }

        var newestFirst = new List<WriteLogEntry>(tail.Count);
        foreach (var (number, text) in tail.Reverse())
        {
            try
            {
                if (JsonSerializer.Deserialize<WriteLogEntry>(text, JsonOptions) is { } entry)
                {
                    newestFirst.Add(entry);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Skipped malformed line {Line} of the write log at {Path}", number, _filePath);
            }
        }

        return newestFirst;
    }

    /// <inheritdoc/>
    public void Dispose() => _gate.Dispose();
}
