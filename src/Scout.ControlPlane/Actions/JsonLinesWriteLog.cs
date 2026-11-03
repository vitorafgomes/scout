using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scout.ControlPlane.Features;

namespace Scout.ControlPlane.Actions;

/// <summary>
/// <see cref="IWriteLog"/> kept in <c>writes.log</c> beside the feature state
/// file, one JSON object per line, appended and never rewritten. A list reads
/// the file backwards from its end in blocks until it has the lines it needs, or,
/// listing since a time, until the first entry older than it. A line that does not
/// parse is skipped and logged.
/// </summary>
public sealed class JsonLinesWriteLog : IWriteLog, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal const int BlockSize = 4096;

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
    public Task<IReadOnlyList<WriteLogEntry>> ListAsync(int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return ListCoreAsync(limit, stop: null, ct);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<WriteLogEntry>> ListSinceAsync(DateTimeOffset since, CancellationToken ct) =>
        ListCoreAsync(int.MaxValue, text => Parse(text) is { } entry && entry.At < since, ct);

    /// <inheritdoc/>
    public void Dispose() => _gate.Dispose();

    private static WriteLogEntry? Parse(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<WriteLogEntry>(text, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<WriteLogEntry>> ListCoreAsync(int limit, Func<string, bool>? stop, CancellationToken ct)
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        List<(int FromEnd, string Text)> tail;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            tail = await ReadTailAsync(limit, stop, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        var newestFirst = new List<WriteLogEntry>(tail.Count);
        foreach (var (fromEnd, text) in tail)
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
                _logger.LogWarning(
                    ex,
                    "Skipped malformed line {Line} from the end of the write log at {Path}",
                    fromEnd,
                    _filePath);
            }
        }

        return newestFirst;
    }

    private async Task<List<(int FromEnd, string Text)>> ReadTailAsync(int limit, Func<string, bool>? stop, CancellationToken ct)
    {
        var lines = new List<(int FromEnd, string Text)>();
        var fromEnd = 0;
        var atEnd = true;
        var stopped = false;

        void Take(ReadOnlySpan<byte> bytes)
        {
            if (atEnd)
            {
                atEnd = false;
                if (bytes.IsEmpty)
                {
                    return;
                }
            }

            fromEnd++;
            var text = Encoding.UTF8.GetString(bytes);
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            if (stop?.Invoke(text) == true)
            {
                stopped = true;
                return;
            }

            lines.Add((fromEnd, text));
        }

        await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1);
        var position = stream.Length;
        var partial = Array.Empty<byte>();
        while (lines.Count < limit && !stopped)
        {
            if (position == 0)
            {
                Take(partial);
                break;
            }

            var size = (int)Math.Min(BlockSize, position);
            position -= size;
            var block = new byte[size + partial.Length];
            stream.Position = position;
            await stream.ReadExactlyAsync(block.AsMemory(0, size), ct).ConfigureAwait(false);
            partial.CopyTo(block, size);

            var lineEnd = block.Length;
            for (var i = size - 1; i >= 0 && lines.Count < limit && !stopped; i--)
            {
                if (block[i] == (byte)'\n')
                {
                    Take(block.AsSpan(i + 1, lineEnd - i - 1));
                    lineEnd = i;
                }
            }

            partial = block[..lineEnd];
        }

        return lines;
    }
}
