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
/// Every write drops rejected actions decided more than 30 days ago and executed
/// ones approved more than 90 days ago; pending, approved and executing actions are kept.
/// </summary>
public sealed class JsonFileActionQueue : IActionQueue, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly TimeSpan RejectedKeptFor = TimeSpan.FromDays(30);
    private static readonly TimeSpan ExecutedKeptFor = TimeSpan.FromDays(90);

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
    public async Task<IReadOnlyList<PendingAction>> EnqueueApprovedAsync(
        string featureId,
        IReadOnlyList<(string Action, string Target, string Summary)> actions,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        ArgumentNullException.ThrowIfNull(actions);

        var now = DateTimeOffset.UtcNow;
        var approved = actions
            .Select(a => new PendingAction(
                Guid.NewGuid().ToString("N"), featureId, a.Action, a.Target, a.Summary, now, PendingActionStatus.Approved, now))
            .ToList();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = await ReadAsync(ct).ConfigureAwait(false);
            all.AddRange(approved);
            await WriteAsync(all, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        return approved;
    }

    /// <inheritdoc/>
    public async Task<PendingAction?> ClaimNextAsync(
        string featureId,
        TimeSpan lease,
        Func<PendingAction, IReadOnlyList<PendingAction>, bool> eligible,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        ArgumentNullException.ThrowIfNull(eligible);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = await ReadAsync(ct).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var open = all
                .Where(a => string.Equals(a.FeatureId, featureId, StringComparison.Ordinal)
                    && a.Status is PendingActionStatus.Approved or PendingActionStatus.Executing)
                .OrderBy(a => a.RequestedAt)
                .ToList();
            var next = open.FirstOrDefault(a =>
                (a.Status == PendingActionStatus.Approved || a.ClaimedAt <= now - lease) && eligible(a, open));
            if (next is null)
            {
                return null;
            }

            var claimed = next with { Status = PendingActionStatus.Executing, ClaimedAt = now };
            all[all.IndexOf(next)] = claimed;
            await WriteAsync(all, ct).ConfigureAwait(false);
            return claimed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PendingAction>> RejectApprovedAsync(
        string featureId,
        Func<PendingAction, IReadOnlyList<PendingAction>, bool> match,
        string reason,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        ArgumentNullException.ThrowIfNull(match);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = await ReadAsync(ct).ConfigureAwait(false);
            var approved = all
                .Where(a => string.Equals(a.FeatureId, featureId, StringComparison.Ordinal) && a.Status == PendingActionStatus.Approved)
                .OrderBy(a => a.RequestedAt)
                .ToList();
            var now = DateTimeOffset.UtcNow;
            var rejected = new List<PendingAction>();
            foreach (var action in approved.Where(a => match(a, approved)))
            {
                var decided = action with { Status = PendingActionStatus.Rejected, DecidedAt = now, Reason = reason };
                all[all.IndexOf(action)] = decided;
                rejected.Add(decided);
            }

            if (rejected.Count > 0)
            {
                await WriteAsync(all, ct).ConfigureAwait(false);
            }

            return rejected;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PendingAction>> ListPendingAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return (await ReadAsync(ct).ConfigureAwait(false))
                .Where(a => a.Status is PendingActionStatus.Pending or PendingActionStatus.Executing)
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
    public Task<PendingAction> MarkExecutedAsync(string id, CancellationToken ct) =>
        ExecuteAsync(id, _ => Task.CompletedTask, ct);

    /// <inheritdoc/>
    public async Task<PendingAction> ExecuteAsync(
        string id,
        Func<CancellationToken, Task> record,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(record);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = await ReadAsync(ct).ConfigureAwait(false);
            var index = all.FindIndex(a => string.Equals(a.Id, id, StringComparison.Ordinal));
            if (index < 0)
            {
                throw new PendingActionNotFoundException(id);
            }

            if (all[index].Status is not (PendingActionStatus.Approved or PendingActionStatus.Executing))
            {
                throw new PendingActionNotApprovedException(id, all[index].Status);
            }

            await record(ct).ConfigureAwait(false);
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
        var now = DateTimeOffset.UtcNow;
        all.RemoveAll(a => a.Status switch
        {
            PendingActionStatus.Rejected => a.DecidedAt < now - RejectedKeptFor,
            PendingActionStatus.Executed => a.DecidedAt < now - ExecutedKeptFor,
            _ => false,
        });

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
