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
/// aside to <c>actions.json.corrupt-&lt;UTC yyyyMMddHHmmss&gt;</c>, with <c>-1</c>, <c>-2</c>... appended while
/// that name is taken, never overwritten;
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
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Build a queue next to the feature state file.</summary>
    public JsonFileActionQueue(
        IOptions<FeatureStateStoreOptions> options,
        ILogger<JsonFileActionQueue> logger,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? TimeProvider.System;
        _filePath = Path.Combine(
            Path.GetDirectoryName(options.Value.ResolveFilePath()) ?? string.Empty,
            "actions.json");
    }

    /// <summary>Absolute path of the underlying JSON file.</summary>
    public string FilePath => _filePath;

    /// <inheritdoc/>
    public Task<PendingAction> EnqueueAsync(
        string featureId,
        string action,
        string target,
        string summary,
        CancellationToken ct) => EnqueueAsync(featureId, action, target, summary, null, ct);

    /// <inheritdoc/>
    public Task<PendingAction> EnqueueAsync(
        string featureId,
        string action,
        string target,
        string summary,
        string? payload,
        CancellationToken ct) => EnqueueAsync(featureId, action, target, summary, payload, null, null, ct);

    /// <inheritdoc/>
    public async Task<PendingAction> EnqueueAsync(
        string featureId,
        string action,
        string target,
        string summary,
        string? payload,
        string? groupId,
        string? repository,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(summary);
        CheckPayload(payload);

        var queued = new PendingAction(
            Guid.NewGuid().ToString("N"),
            featureId,
            action,
            target,
            summary,
            _time.GetUtcNow(),
            PendingActionStatus.Pending,
            DecidedAt: null)
        {
            Payload = payload,
            GroupId = groupId,
            Repository = repository,
        };

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
        foreach (var (action, target, summary) in actions)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(action);
            ArgumentNullException.ThrowIfNull(target);
            ArgumentNullException.ThrowIfNull(summary);
        }

        var now = _time.GetUtcNow();
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
    public async Task<(PendingAction Action, bool Added)> EnqueueApprovedOnceAsync(
        string featureId,
        string action,
        string target,
        string summary,
        string payload,
        Func<PendingAction, bool> sameAs,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(sameAs);
        CheckPayload(payload);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = await ReadAsync(ct).ConfigureAwait(false);
            var existing = all.Find(a => string.Equals(a.FeatureId, featureId, StringComparison.Ordinal)
                && a.Status != PendingActionStatus.Rejected
                && !(a.Status == PendingActionStatus.Executed && (a.Outcome?.StartsWith("failed", StringComparison.Ordinal) ?? false))
                && sameAs(a));
            if (existing is not null)
            {
                return (existing, false);
            }

            var now = _time.GetUtcNow();
            var approved = new PendingAction(Guid.NewGuid().ToString("N"), featureId, action, target, summary, now, PendingActionStatus.Approved, now)
            {
                Payload = payload,
            };
            all.Add(approved);
            await WriteAsync(all, ct).ConfigureAwait(false);
            return (approved, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<(PendingAction Action, bool Added)>> EnqueueApprovedOnceEachAsync(
        string featureId,
        string repository,
        IReadOnlyList<(string CardKey, string Action, string Target, string Summary, string Payload)> actions,
        Func<PendingAction, string, bool> answers,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(answers);
        foreach (var (cardKey, action, target, summary, payload) in actions)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(cardKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(action);
            ArgumentNullException.ThrowIfNull(target);
            ArgumentNullException.ThrowIfNull(summary);
            ArgumentNullException.ThrowIfNull(payload);
            CheckPayload(payload);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = await ReadAsync(ct).ConfigureAwait(false);
            var now = _time.GetUtcNow();
            var groupId = Guid.NewGuid().ToString("N");
            var answered = new List<(PendingAction Action, bool Added)>();
            foreach (var item in actions)
            {
                if (all.Find(a => string.Equals(a.FeatureId, featureId, StringComparison.Ordinal) && answers(a, item.CardKey)) is { } existing)
                {
                    answered.Add((existing, false));
                    continue;
                }

                var approved = new PendingAction(Guid.NewGuid().ToString("N"), featureId, item.Action, item.Target, item.Summary, now, PendingActionStatus.Approved, now)
                {
                    Payload = item.Payload,
                    GroupId = groupId,
                    Repository = repository,
                    GroupClosedAt = now,
                    CardKey = item.CardKey,
                };
                all.Add(approved);
                answered.Add((approved, true));
            }

            if (answered.Exists(a => a.Added))
            {
                await WriteAsync(all, ct).ConfigureAwait(false);
            }

            return answered;
        }
        finally
        {
            _gate.Release();
        }
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
            var now = _time.GetUtcNow();
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
            var now = _time.GetUtcNow();
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
    public async Task<IReadOnlyList<PendingAction>> RejectUnstartedAsync(string featureId, string reason, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = await ReadAsync(ct).ConfigureAwait(false);
            var now = _time.GetUtcNow();
            var rejected = new List<PendingAction>();
            for (var i = 0; i < all.Count; i++)
            {
                if (string.Equals(all[i].FeatureId, featureId, StringComparison.Ordinal)
                    && all[i].Status is PendingActionStatus.Pending or PendingActionStatus.Approved)
                {
                    all[i] = all[i] with { Status = PendingActionStatus.Rejected, DecidedAt = now, Reason = reason };
                    rejected.Add(all[i]);
                }
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
    public async Task ReleaseAsync(string id, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = await ReadAsync(ct).ConfigureAwait(false);
            var index = all.FindIndex(a => string.Equals(a.Id, id, StringComparison.Ordinal) && a.Status == PendingActionStatus.Executing);
            if (index < 0)
            {
                return;
            }

            all[index] = all[index] with { Status = PendingActionStatus.Approved, ClaimedAt = null };
            await WriteAsync(all, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PendingAction>> RejectExecutingAsync(
        string featureId,
        Func<PendingAction, bool> match,
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
            var now = _time.GetUtcNow();
            var rejected = new List<PendingAction>();
            for (var i = 0; i < all.Count; i++)
            {
                if (string.Equals(all[i].FeatureId, featureId, StringComparison.Ordinal)
                    && all[i].Status == PendingActionStatus.Executing
                    && match(all[i]))
                {
                    all[i] = all[i] with { Status = PendingActionStatus.Rejected, DecidedAt = now, Reason = reason };
                    rejected.Add(all[i]);
                }
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
                .Where(a => a.Status is PendingActionStatus.Pending or PendingActionStatus.Approved or PendingActionStatus.Executing)
                .OrderBy(a => a.RequestedAt)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PendingAction>> ListOpenAsync(string featureId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return (await ReadAsync(ct).ConfigureAwait(false))
                .Where(a => string.Equals(a.FeatureId, featureId, StringComparison.Ordinal)
                    && a.Status is PendingActionStatus.Approved or PendingActionStatus.Executing)
                .OrderBy(a => a.RequestedAt)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PendingAction>> ListAllAsync(string featureId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return (await ReadAsync(ct).ConfigureAwait(false))
                .Where(a => string.Equals(a.FeatureId, featureId, StringComparison.Ordinal))
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
        ExecuteAsync(id, null, null, _ => Task.CompletedTask, ct);

    /// <inheritdoc/>
    public Task<PendingAction> ExecuteAsync(string id, Func<CancellationToken, Task> record, CancellationToken ct) =>
        ExecuteAsync(id, null, null, record, ct);

    /// <inheritdoc/>
    public async Task<PendingAction> ExecuteAsync(
        string id,
        string? outcome,
        string? result,
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
            var executed = all[index] with { Status = PendingActionStatus.Executed, Outcome = outcome, Result = result, Payload = Shrink(all[index].Payload) };
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

    /// <summary>The payload of an executed action cut to <see cref="PendingAction.KeptAfterExecution"/>; null when there is none or it is not a JSON object.</summary>
    private static string? Shrink(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var kept = document.RootElement.EnumerateObject()
                .Where(p => PendingAction.KeptAfterExecution.Contains(p.Name, StringComparer.Ordinal))
                .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
            return JsonSerializer.Serialize(kept);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void CheckPayload(string? payload)
    {
        if (payload?.Length > PendingAction.MaxPayloadLength)
        {
            throw new ArgumentException(
                $"The action's payload is over {PendingAction.MaxPayloadLength.ToString(CultureInfo.InvariantCulture)} characters.", nameof(payload));
        }
    }

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
                DecidedAt = _time.GetUtcNow(),
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
                .ConfigureAwait(false) ?? throw new JsonException("The action queue file holds null.");
        }
        catch (JsonException ex)
        {
            var asidePath = CorruptFile.MoveAside(_filePath, _time);
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
        var now = _time.GetUtcNow();
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

    /// <inheritdoc/>
    public async Task<int> CloseGroupAsync(string groupId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = await ReadAsync(ct).ConfigureAwait(false);
            var now = _time.GetUtcNow();
            var held = 0;
            var stamped = false;
            for (var i = 0; i < all.Count; i++)
            {
                if (!string.Equals(all[i].GroupId, groupId, StringComparison.Ordinal))
                {
                    continue;
                }

                held++;
                if (all[i].GroupClosedAt is null)
                {
                    all[i] = all[i] with { GroupClosedAt = now };
                    stamped = true;
                }
            }

            if (stamped)
            {
                await WriteAsync(all, ct).ConfigureAwait(false);
            }

            return held;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PendingAction>> ListWhereAsync(Func<PendingAction, bool> match, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(match);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return (await ReadAsync(ct).ConfigureAwait(false)).Where(match).OrderBy(a => a.RequestedAt).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PendingAction>> DecideSetAsync(
        string groupId,
        Func<IReadOnlyList<PendingAction>, Task<IReadOnlyDictionary<string, (bool Approve, string? Reason)>>> decide,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentNullException.ThrowIfNull(decide);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = await ReadAsync(ct).ConfigureAwait(false);
            bool InGroup(PendingAction a) => string.Equals(a.GroupId, groupId, StringComparison.Ordinal);
            var decisions = await decide(all.Where(InGroup).OrderBy(a => a.RequestedAt).ToList()).ConfigureAwait(false);
            if (decisions.Count == 0)
            {
                return all.Where(InGroup).OrderBy(a => a.RequestedAt).ToList();
            }

            var now = _time.GetUtcNow();
            foreach (var (id, (approve, reason)) in decisions)
            {
                var index = all.FindIndex(a => string.Equals(a.Id, id, StringComparison.Ordinal) && InGroup(a));
                if (index < 0)
                {
                    throw new PendingActionNotFoundException(id);
                }

                if (all[index].Status != PendingActionStatus.Pending)
                {
                    throw new PendingActionAlreadyDecidedException(id, all[index].Status);
                }

                all[index] = all[index] with
                {
                    Status = approve ? PendingActionStatus.Approved : PendingActionStatus.Rejected,
                    DecidedAt = now,
                    Reason = string.IsNullOrWhiteSpace(reason) ? null : reason,
                };
            }

            await WriteAsync(all, ct).ConfigureAwait(false);
            return all.Where(InGroup).OrderBy(a => a.RequestedAt).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }
}
