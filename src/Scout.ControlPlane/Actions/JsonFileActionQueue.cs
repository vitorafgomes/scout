using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scout.ControlPlane.Features;

namespace Scout.ControlPlane.Actions;

/// <summary>
/// <see cref="IActionQueue"/> kept in <c>actions.json</c> beside the feature
/// state file. Every read-check-write runs under one gate, and writes go
/// through a temp file and an atomic move. A file that does not parse is moved
/// aside to <c>actions.json.corrupt-&lt;UTC yyyyMMddHHmmss&gt;</c>, never overwritten;
/// a file that cannot be opened or read fails the call and stays where it is.
/// </summary>
public sealed class JsonFileActionQueue : IActionQueue, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _filePath;
    private readonly ILogger<JsonFileActionQueue> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Build a queue next to the feature state file.</summary>
    public JsonFileActionQueue(
        IOptions<FeatureStateStoreOptions> options,
        ILogger<JsonFileActionQueue> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _filePath = Path.Combine(
            Path.GetDirectoryName(options.Value.ResolveFilePath()) ?? string.Empty,
            "actions.json");
    }

    /// <summary>Absolute path of the underlying JSON file.</summary>
    public string FilePath => _filePath;

    /// <inheritdoc/>
    public async Task<PendingAction> EnqueueAsync(
        string featureId,
        string action,
        string target,
        string summary,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(summary);

        var queued = new PendingAction(
            Guid.NewGuid().ToString("N"),
            featureId,
            action,
            target,
            summary,
            DateTimeOffset.UtcNow,
            PendingActionStatus.Pending,
            DecidedAt: null);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = await ReadAsync(ct).ConfigureAwait(false);
            all.Add(queued);
            await WriteAsync(all, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        return queued;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PendingAction>> ListPendingAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return (await ReadAsync(ct).ConfigureAwait(false))
                .Where(a => a.Status == PendingActionStatus.Pending)
                .OrderBy(a => a.RequestedAt)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public Task<PendingAction> ApproveAsync(string id, string? reason, CancellationToken ct) =>
        DecideAsync(id, PendingActionStatus.Approved, reason, ct);

    /// <inheritdoc/>
    public Task<PendingAction> RejectAsync(string id, string? reason, CancellationToken ct) =>
        DecideAsync(id, PendingActionStatus.Rejected, reason, ct);

    /// <inheritdoc/>
    public async Task<PendingAction> MarkExecutedAsync(string id, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = await ReadAsync(ct).ConfigureAwait(false);
            var index = all.FindIndex(a => string.Equals(a.Id, id, StringComparison.Ordinal));
            if (index < 0)
            {
                throw new PendingActionNotFoundException(id);
            }

            if (all[index].Status != PendingActionStatus.Approved)
            {
                throw new PendingActionNotApprovedException(id, all[index].Status);
            }

            var executed = all[index] with { Status = PendingActionStatus.Executed };
            all[index] = executed;
            await WriteAsync(all, ct).ConfigureAwait(false);
            return executed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<PendingAction?> FindAsync(string id, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return (await ReadAsync(ct).ConfigureAwait(false))
                .Find(a => string.Equals(a.Id, id, StringComparison.Ordinal));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _gate.Dispose();

    private async Task<PendingAction> DecideAsync(
        string id,
        PendingActionStatus decision,
        string? reason,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = await ReadAsync(ct).ConfigureAwait(false);
            var index = all.FindIndex(a => string.Equals(a.Id, id, StringComparison.Ordinal));
            if (index < 0)
            {
                throw new PendingActionNotFoundException(id);
            }

            if (all[index].Status != PendingActionStatus.Pending)
            {
                throw new PendingActionAlreadyDecidedException(id, all[index].Status);
            }

            var decided = all[index] with
            {
                Status = decision,
                DecidedAt = DateTimeOffset.UtcNow,
                Reason = string.IsNullOrWhiteSpace(reason) ? null : reason,
            };
            all[index] = decided;
            await WriteAsync(all, ct).ConfigureAwait(false);
            return decided;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<PendingAction>> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        try
        {
            await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await JsonSerializer.DeserializeAsync<List<PendingAction>>(stream, JsonOptions, ct)
                .ConfigureAwait(false) ?? [];
        }
        catch (JsonException ex)
        {
            var asidePath = _filePath + ".corrupt-"
                + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            File.Move(_filePath, asidePath, overwrite: false);
            _logger.LogWarning(
                ex,
                "Action queue file at {Path} is malformed; moved it to {AsidePath} and started an empty queue",
                _filePath,
                asidePath);
            return [];
        }
    }

    private async Task WriteAsync(List<PendingAction> all, CancellationToken ct)
    {
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
