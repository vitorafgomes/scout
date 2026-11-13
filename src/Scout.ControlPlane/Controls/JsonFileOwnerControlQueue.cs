using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scout.ControlPlane.Features;

namespace Scout.ControlPlane.Controls;

/// <summary>
/// <see cref="IOwnerControlQueue"/> kept in <c>controls.json</c> beside the feature state file. Every read-check-write runs
/// under one gate, and writes go through a temp file and an atomic move. A file that does not parse is moved aside to
/// <c>controls.json.corrupt-&lt;UTC yyyyMMddHHmmss&gt;</c>, never overwritten; a file that cannot be opened or read fails
/// the call and stays where it is. Every write drops controls settled more than a day ago; one not settled is kept.
/// </summary>
public sealed class JsonFileOwnerControlQueue : IOwnerControlQueue, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly TimeSpan SettledKeptFor = TimeSpan.FromDays(1);

    private readonly string _filePath;
    private readonly ILogger<JsonFileOwnerControlQueue> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Build a queue next to the feature state file.</summary>
    public JsonFileOwnerControlQueue(IOptions<FeatureStateStoreOptions> options, ILogger<JsonFileOwnerControlQueue> logger, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? TimeProvider.System;
        _filePath = Path.Combine(Path.GetDirectoryName(options.Value.ResolveFilePath()) ?? string.Empty, "controls.json");
    }

    /// <summary>Absolute path of the underlying JSON file.</summary>
    public string FilePath => _filePath;

    /// <inheritdoc/>
    public async Task EnqueueAsync(OwnerControl control, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(control);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = await ReadAsync(ct).ConfigureAwait(false);
            all.Add(control with { TakenAt = null, SettledAt = null });
            await WriteAsync(all, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<(OwnerControl? Taken, IReadOnlyList<OwnerControl> Lapsed)> TakeNextAsync(DateTimeOffset now, DateTimeOffset lapsedBefore, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = await ReadAsync(ct).ConfigureAwait(false);
            IReadOnlyList<OwnerControl> lapsed = [.. all.Where(c => c.TakenAt < lapsedBefore && c.SettledAt is null)];
            var index = all.FindIndex(c => c.TakenAt is null);
            if (index < 0)
            {
                return (null, lapsed);
            }

            var taken = all[index] with { TakenAt = now };
            all[index] = taken;
            await WriteAsync(all, ct).ConfigureAwait(false);
            return (taken, lapsed);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<OwnerControl?> SettleAsync(string id, DateTimeOffset now, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = await ReadAsync(ct).ConfigureAwait(false);
            var index = all.FindIndex(c => string.Equals(c.Id, id, StringComparison.Ordinal) && c.TakenAt is not null && c.SettledAt is null);
            if (index < 0)
            {
                return null;
            }

            var settled = all[index] with { SettledAt = now };
            all[index] = settled;
            await WriteAsync(all, ct).ConfigureAwait(false);
            return settled;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<OwnerControl>> UnsettledAsync(DateTimeOffset before, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return [.. (await ReadAsync(ct).ConfigureAwait(false)).Where(c => c.TakenAt < before && c.SettledAt is null)];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _gate.Dispose();

    private async Task<List<OwnerControl>> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        try
        {
            await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await JsonSerializer.DeserializeAsync<List<OwnerControl>>(stream, JsonOptions, ct).ConfigureAwait(false)
                ?? throw new JsonException("The owner control file holds null.");
        }
        catch (JsonException ex)
        {
            var asidePath = CorruptFile.MoveAside(_filePath, _time);
            _logger.LogWarning(ex, "Owner control file at {Path} is malformed; moved it to {AsidePath} and started an empty queue", _filePath, asidePath);
            return [];
        }
    }

    private async Task WriteAsync(List<OwnerControl> all, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        all.RemoveAll(c => c.SettledAt < now - SettledKeptFor);
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = _filePath + ".tmp";
        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, all, JsonOptions, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        File.Move(tempPath, _filePath, overwrite: true);
    }
}
