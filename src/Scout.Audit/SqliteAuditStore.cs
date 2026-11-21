using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Scout.Audit.Storage;
using Scout.Review;
using static Scout.Audit.AuditStatus;

namespace Scout.Audit;

/// <summary>
/// Keeps the audit jobs in one SQLite file under Scout's data folder through Entity Framework: <c>audit_job</c>,
/// <c>audit_batch</c> and <c>audit_finding</c>. The schema is brought up to date by the migrations on the store's first
/// call; a file an older build wrote, with tables and no migration history, is deleted and created again. A finding
/// whose id another finding of the job holds gets a free id, so the desktop, which merges cards by id, never folds two
/// findings into one card. One context per call.
/// </summary>
internal sealed class SqliteAuditStore : IAuditJobStore
{
    private const int MaxIdLength = 40;

    private static readonly string[] PreviousSchemaTables = ["audit_job", "audit_batch", "audit_finding"];

    private readonly string _dbPath;
    private readonly DbContextOptions<AuditDbContext> _options;
    private readonly TimeProvider _time;
    private readonly Lock _schemaGate = new();
    private Task? _schema;

    /// <summary>Creates the store over the database file at <paramref name="dbPath"/>; nothing is opened until the first call.</summary>
    public SqliteAuditStore(string dbPath, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        _dbPath = Path.GetFullPath(dbPath);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
        }.ToString();
        _options = new DbContextOptionsBuilder<AuditDbContext>().UseSqlite(connectionString).Options;
        _time = time;
    }

    /// <inheritdoc />
    public Task<string> CreateJobAsync(
        string owner, string repo, int? pullRequest, string commit, string? origin, IReadOnlyList<AuditBatchPlan> batches, ReviewPreflight? preflight, CancellationToken ct,
        ReviewInputs? inputs = null) =>
        RunAsync("save", async db =>
        {
            var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
            db.Jobs.Add(new AuditJobRow
            {
                Id = id,
                Owner = owner,
                Repo = repo,
                Kind = pullRequest is null ? AuditKind.Audit : AuditKind.Review,
                PullRequest = pullRequest,
                CommitSha = commit,
                Origin = origin,
                Started = Now(),
                Status = Running,
                Preflight = preflight is null ? null : JsonSerializer.Serialize(preflight, JsonSerializerOptions.Web),
                Inputs = inputs is null ? null : JsonSerializer.Serialize(inputs, JsonSerializerOptions.Web),
            });
            db.Batches.AddRange(batches.Select((batch, i) => new AuditBatchRow
            {
                JobId = id,
                Seq = i + 1,
                Area = batch.Area,
                Files = [.. batch.Files],
                Status = batch.Failure is null ? Pending : NotCovered,
                Failure = batch.Failure,
                Partial = [.. batch.Partial ?? []],
            }));
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return id;
        });

    /// <inheritdoc />
    public Task<IReadOnlyList<AuditJob>> RunningJobsAsync(CancellationToken ct) =>
        RunAsync<IReadOnlyList<AuditJob>>("read", async db =>
        {
            var rows = await RunningJobs(db).ToListAsync(ct).ConfigureAwait(false);
            return [.. Jobs(rows.OrderBy(j => j.Resumed ?? j.Started).AsQueryable())];
        });

    /// <inheritdoc />
    public Task<AuditJob?> JobAsync(string id, CancellationToken ct) =>
        RunAsync("read", db => Jobs(db.Jobs.Where(j => j.Id == id)).SingleOrDefaultAsync(ct));

    /// <inheritdoc />
    public Task<AuditJob?> LatestJobAsync(string owner, string repo, int? pullRequest, CancellationToken ct) =>
        RunAsync("read", db => Jobs(LatestJobs(db, owner, repo, pullRequest)).FirstOrDefaultAsync(ct));

    /// <inheritdoc />
    public Task<IReadOnlyList<AuditBatch>> BatchesAsync(string jobId, CancellationToken ct) =>
        RunAsync<IReadOnlyList<AuditBatch>>("read", async db =>
            await Batches(db.Batches.Where(b => b.JobId == jobId).OrderBy(b => b.Seq)).ToListAsync(ct).ConfigureAwait(false));

    /// <inheritdoc />
    public Task<(AuditBatch? Next, int Count)> NextBatchAsync(string jobId, CancellationToken ct) =>
        RunAsync("read", async db =>
        {
            var total = await db.Batches.CountAsync(b => b.JobId == jobId, ct).ConfigureAwait(false);
            var next = await Batches(db.Batches.Where(b => b.JobId == jobId && b.Status == Pending).OrderBy(b => b.Seq)).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            return (next, total);
        });

    /// <inheritdoc />
    public Task<IReadOnlyList<AuditBatch>> PendingBatchesAsync(string jobId, int count, CancellationToken ct) =>
        RunAsync<IReadOnlyList<AuditBatch>>("read", async db =>
            await Batches(db.Batches.Where(b => b.JobId == jobId && b.Status == Pending).OrderBy(b => b.Seq).Take(count)).ToListAsync(ct).ConfigureAwait(false));

    /// <inheritdoc />
    public Task<IReadOnlyList<Finding>> FindingsAsync(string jobId, CancellationToken ct) =>
        RunAsync<IReadOnlyList<Finding>>("read", async db =>
            await Findings(db, jobId)
                .Select(f => new Finding(f.Id, f.Severity, f.Title, f.Path, f.Line, f.Context, f.Why, f.Fix, f.Rule, f.Evidence))
                .ToListAsync(ct).ConfigureAwait(false));

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, (string State, string? Reason)>> FindingStatesAsync(string jobId, CancellationToken ct) =>
        RunAsync<IReadOnlyDictionary<string, (string State, string? Reason)>>("read", async db =>
            (await db.Findings.Where(f => f.JobId == jobId && f.State != ContestStatus.Open)
                .Select(f => new { f.Id, f.State, f.DismissedReason })
                .ToListAsync(ct).ConfigureAwait(false))
            .ToDictionary(f => f.Id, f => (f.State, f.DismissedReason), StringComparer.Ordinal));

    /// <inheritdoc />
    public Task AddTurnAsync(string jobId, AuditTurn turn, CancellationToken ct) =>
        RunAsync("save", async db =>
        {
            db.Turns.Add(new AuditTurnRow
            {
                JobId = jobId,
                Batch = turn.Batch,
                Reading = turn.Reading,
                ContestGroup = turn.Group,
                Attempt = turn.Attempt,
                Extra = turn.Extra,
                Started = Now() - TimeSpan.FromMilliseconds(turn.ElapsedMs),
                ElapsedMs = turn.ElapsedMs,
                RequestLength = turn.RequestLength,
                Calls = JsonSerializer.Serialize(turn.Calls),
                Presented = turn.Presented,
                Placed = turn.Placed,
                LeftOut = turn.LeftOut,
                Failure = turn.Failure,
                Held = turn.Held,
                Refuted = turn.Refuted,
                Uncertain = turn.Uncertain,
                LastCallMs = turn.LastCallMs,
            });
            return await db.SaveChangesAsync(ct).ConfigureAwait(false);
        });

    /// <inheritdoc />
    public Task<IReadOnlyList<ReadingTotals>> TurnTotalsAsync(string jobId, CancellationToken ct) =>
        RunAsync<IReadOnlyList<ReadingTotals>>("read", async db =>
        {
            var turns = await db.Turns.Where(t => t.JobId == jobId).OrderBy(t => t.Number).ToListAsync(ct).ConfigureAwait(false);
            return [.. turns.GroupBy(t => t.Reading, StringComparer.Ordinal).Select(g => new ReadingTotals(
                g.Key,
                g.Count(),
                g.Sum(t => t.ElapsedMs),
                g.Sum(t => JsonSerializer.Deserialize<Dictionary<string, int>>(t.Calls)?.Values.Sum() ?? 0),
                g.Sum(t => t.Presented),
                g.Sum(t => t.Placed),
                g.Sum(t => t.LeftOut),
                g.Count(t => t.Failure is not null),
                g.GroupBy(t => (t.Batch, t.ContestGroup)).Count(place => place.All(t => t.Failure is not null))))];
        });

    /// <inheritdoc />
    public Task StampRunAsync(string jobId, string? version, string? chatModel, CancellationToken ct) =>
        RunAsync("save", db => db.Jobs.Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(set => set.SetProperty(j => j.ScoutVersion, version).SetProperty(j => j.ChatModel, chatModel), ct));

    /// <inheritdoc />
    public Task RecordOutcomesAsync(string jobId, IReadOnlyDictionary<string, ContestOutcome> outcomes, CancellationToken ct) =>
        RunAsync("save", async db =>
        {
            var recorded = 0;
            foreach (var (id, outcome) in outcomes)
            {
                var state = ContestStatus.StateOf(outcome.Status);
                var reason = outcome.Status == ContestStatus.Holds ? null : outcome.Reason;
                if (outcome.Status == ContestStatus.Holds && await db.Findings.FirstOrDefaultAsync(f => f.JobId == jobId && f.Id == id && f.State == ContestStatus.Open, ct).ConfigureAwait(false) is { } row)
                {
                    (row.State, row.DismissedReason, row.Severity, row.Why) = (state, null, outcome.Severity ?? row.Severity, Contestation.WithScenario(row.Why, outcome.Scenario));
                    recorded += await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    continue;
                }

                recorded += await db.Findings
                    .Where(f => f.JobId == jobId && f.Id == id && f.State == ContestStatus.Open)
                    .ExecuteUpdateAsync(set => set.SetProperty(f => f.State, state).SetProperty(f => f.DismissedReason, reason), ct).ConfigureAwait(false);
            }

            return recorded;
        });

    /// <inheritdoc />
    public Task BeginBatchAsync(string jobId, int order, CancellationToken ct) =>
        RunAsync("save", db => db.Batches
            .Where(b => b.JobId == jobId && b.Seq == order)
            .ExecuteUpdateAsync(set => set.SetProperty(b => b.Started, Now()), ct));

    /// <inheritdoc />
    public Task<IReadOnlyList<Finding>> CompleteBatchAsync(
        string jobId, int order, IReadOnlyList<string> filesRead, IReadOnlyList<Finding> findings, string? leftOut, CancellationToken ct) =>
        RunAsync<IReadOnlyList<Finding>>("save", async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            var existing = await db.Findings.Where(f => f.JobId == jobId).Select(f => new { f.Id, f.MergeKey }).ToListAsync(ct).ConfigureAwait(false);
            var ids = new HashSet<string>(existing.Select(f => f.Id), StringComparer.OrdinalIgnoreCase);
            var keys = new HashSet<string>(existing.Select(f => f.MergeKey), StringComparer.Ordinal);
            long seq = keys.Count;

            var added = new List<Finding>();
            foreach (var finding in findings)
            {
                if (!keys.Add(MergeKey(finding)))
                {
                    continue;
                }

                var kept = finding with { Id = FreeId(finding.Id, ids) };
                db.Findings.Add(new AuditFindingRow
                {
                    JobId = jobId,
                    Id = kept.Id,
                    MergeKey = MergeKey(kept),
                    Seq = ++seq,
                    Severity = kept.Severity,
                    Title = kept.Title,
                    Path = kept.Path,
                    Line = kept.Line,
                    Context = kept.Context,
                    Evidence = kept.Evidence,
                    Why = kept.Why,
                    Fix = kept.Fix,
                    Rule = kept.Rule,
                });
                added.Add(kept);
            }

            await FinishBatchAsync(db, jobId, order, Done, filesRead, null, leftOut, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return added;
        });

    /// <inheritdoc />
    public Task NotCoveredAsync(string jobId, int order, IReadOnlyList<string> filesRead, string reason, CancellationToken ct) =>
        RunAsync("save", db => FinishBatchAsync(db, jobId, order, NotCovered, filesRead, reason, null, ct));

    /// <inheritdoc />
    public Task<bool> StopAsync(string owner, string repo, int? pullRequest, CancellationToken ct) =>
        RunAsync("save", async db => await Stoppable(db, owner, repo, pullRequest)
            .ExecuteUpdateAsync(
                set => set.SetProperty(j => j.Status, Stopped).SetProperty(j => j.Finished, Now()).SetProperty(j => j.PausedReason, (string?)null),
                ct).ConfigureAwait(false) > 0);

    /// <inheritdoc />
    public Task<bool> PauseAsync(string jobId, string reason, CancellationToken ct) =>
        RunAsync("save", async db => await db.Jobs
            .Where(j => j.Id == jobId && j.Status == Running)
            .ExecuteUpdateAsync(set => set.SetProperty(j => j.Status, Paused).SetProperty(j => j.PausedReason, reason), ct).ConfigureAwait(false) > 0);

    /// <inheritdoc />
    public Task ResumeAsync(string jobId, string? origin, CancellationToken ct) =>
        RunAsync("save", db => db.Jobs
            .Where(j => j.Id == jobId && j.Status == Paused)
            .ExecuteUpdateAsync(
                set => set.SetProperty(j => j.Status, Running).SetProperty(j => j.PausedReason, (string?)null).SetProperty(j => j.Origin, j => origin ?? j.Origin).SetProperty(j => j.Resumed, Now()),
                ct));

    /// <inheritdoc />
    public Task<bool> FinishAsync(string jobId, CancellationToken ct) =>
        RunAsync("save", async db => await db.Jobs
            .Where(j => j.Id == jobId && j.Status == Running)
            .ExecuteUpdateAsync(set => set.SetProperty(j => j.Status, Done).SetProperty(j => j.Finished, Now()), ct).ConfigureAwait(false) > 0);

    internal static IQueryable<AuditJobRow> RunningJobs(AuditDbContext db) =>
        db.Jobs.Where(j => j.Status == Running).OrderBy(j => j.Started).ThenBy(j => j.Number);

    internal static IQueryable<AuditJobRow> LatestJobs(AuditDbContext db, string owner, string repo, int? pullRequest) =>
        OfRepository(db, owner, repo, pullRequest).OrderByDescending(j => j.Started).ThenByDescending(j => j.Number);

    internal static IQueryable<AuditJobRow> Stoppable(AuditDbContext db, string owner, string repo, int? pullRequest) =>
        OfRepository(db, owner, repo, pullRequest).Where(j => j.Status == Running || j.Status == Paused);

    private static IQueryable<AuditJobRow> OfRepository(AuditDbContext db, string owner, string repo, int? pullRequest) =>
        pullRequest is { } number
            ? db.Jobs.Where(j => j.Owner == owner && j.Repo == repo && j.Kind == AuditKind.Review && j.PullRequest == number)
            : db.Jobs.Where(j => j.Owner == owner && j.Repo == repo && j.Kind == AuditKind.Audit && j.PullRequest == null);

    internal static IQueryable<AuditFindingRow> Findings(AuditDbContext db, string jobId) =>
        db.Findings.Where(f => f.JobId == jobId).OrderBy(f => f.Seq);

    internal static string MergeKey(Finding finding) =>
        finding.Path.Trim().ToUpperInvariant() + "\n" + new string([.. finding.Title.Where(char.IsLetterOrDigit)]).ToUpperInvariant();

    private static string FreeId(string id, HashSet<string> taken)
    {
        var candidate = id;
        for (var n = 2; !taken.Add(candidate); n++)
        {
            var suffix = string.Create(CultureInfo.InvariantCulture, $"-{n}");
            candidate = id[..Math.Min(id.Length, MaxIdLength - suffix.Length)] + suffix;
        }

        return candidate;
    }

    private async Task<int> FinishBatchAsync(
        AuditDbContext db, string jobId, int order, string status, IReadOnlyList<string> filesRead, string? failure, string? leftOut, CancellationToken ct)
    {
        var batch = await db.Batches.SingleOrDefaultAsync(b => b.JobId == jobId && b.Seq == order, ct).ConfigureAwait(false);
        if (batch is not null)
        {
            batch.Status = status;
            batch.Finished = Now();
            batch.FilesRead = [.. filesRead];
            batch.Failure = failure;
            batch.LeftOut = leftOut;
        }

        return await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static IQueryable<AuditJob> Jobs(IQueryable<AuditJobRow> rows) =>
        rows.Select(j => new AuditJob(
            j.Id, j.Owner, j.Repo, j.CommitSha, j.Origin, j.Status, j.PausedReason, j.Kind, j.PullRequest, DateTime.SpecifyKind(j.Started, DateTimeKind.Utc),
            j.Finished == null ? null : DateTime.SpecifyKind(j.Finished.Value, DateTimeKind.Utc)));

    private static IQueryable<AuditBatch> Batches(IQueryable<AuditBatchRow> rows) =>
        rows.Select(b => new AuditBatch(b.Seq, b.Area, b.Files, b.Status, b.FilesRead, b.Failure, b.Partial, b.LeftOut));

    private async Task<T> RunAsync<T>(string operation, Func<AuditDbContext, Task<T>> work)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
            await SchemaAsync().ConfigureAwait(false);
            await using var db = new AuditDbContext(_options);
            return await work(db).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            throw Failed(operation, ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException inner)
        {
            throw Failed(operation, inner);
        }
        catch (InvalidDataException ex)
        {
            throw new AuditStoreException($"Scout could not {operation} the audit database: {ex.Message}.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AuditStoreException($"Scout could not {operation} the audit database ({ex.GetType().Name}).", ex);
        }
    }

    /// <summary>The schema of this store's first successful call; a failed attempt is tried again by the next call.</summary>
    private Task SchemaAsync()
    {
        lock (_schemaGate)
        {
            if (_schema is null || _schema.IsFaulted || _schema.IsCanceled)
            {
                _schema = MigrateAsync();
            }

            return _schema;
        }
    }

    /// <summary>
    /// Opens the file for writing, so a path that cannot be a database fails as it opens, applies the migrations this build
    /// has and the file lacks, and keeps the file in WAL mode; a file with tables and no migration history was written by a
    /// build before Entity Framework and is deleted first, with its write-ahead log, but only when every table it holds is
    /// one this store knows; a file with any other table is left as it is and the call fails.
    /// </summary>
    private async Task MigrateAsync()
    {
        await using var db = new AuditDbContext(_options);
        await db.Database.OpenConnectionAsync().ConfigureAwait(false);
        var tables = await db.Database
            .SqlQueryRaw<string>(@"SELECT name AS Value FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite\_%' ESCAPE '\'")
            .ToListAsync().ConfigureAwait(false);
        var olderBuild = !(await db.Database.GetAppliedMigrationsAsync().ConfigureAwait(false)).Any() && tables.Count > 0;
        await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        if (olderBuild)
        {
            if (tables.Except(KnownTables(db), StringComparer.OrdinalIgnoreCase).Any())
            {
                throw new InvalidDataException("it holds tables Scout did not create, so Scout left it as it is");
            }

            await db.Database.EnsureDeletedAsync().ConfigureAwait(false);
            File.Delete(_dbPath + "-wal");
            File.Delete(_dbPath + "-shm");
        }

        if ((await db.Database.GetPendingMigrationsAsync().ConfigureAwait(false)).Any())
        {
            await db.Database.MigrateAsync().ConfigureAwait(false);
        }

        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;").ConfigureAwait(false);
    }

    /// <summary>The tables a file of this store may hold: the model's, Entity Framework's own, and those the schema before Entity Framework created.</summary>
    private static IEnumerable<string> KnownTables(AuditDbContext db) =>
        db.Model.GetEntityTypes().Select(e => e.GetTableName()!).Concat(["__EFMigrationsHistory", "__EFMigrationsLock", .. PreviousSchemaTables]);

    private static AuditStoreException Failed(string operation, SqliteException ex) =>
        new(string.Create(CultureInfo.InvariantCulture, $"Scout could not {operation} the audit database (SQLite error {ex.SqliteErrorCode})."), ex);

    private DateTime Now() => _time.GetUtcNow().UtcDateTime;

    /// <inheritdoc />
    public Task<TimeSpan> RunningTimeAsync(string jobId, CancellationToken ct) =>
        RunAsync("read", async db =>
        {
            var resumed = await db.Jobs.Where(j => j.Id == jobId).Select(j => j.Resumed).SingleOrDefaultAsync(ct).ConfigureAwait(false);
            var intervals = await db.Batches.Where(b => b.JobId == jobId && b.Started != null && b.Finished != null && (resumed == null || b.Started >= resumed))
                .Select(b => new { Start = b.Started!.Value, End = b.Finished!.Value }).ToListAsync(ct).ConfigureAwait(false);
            var total = TimeSpan.Zero;
            DateTime? from = null;
            DateTime? to = null;
            foreach (var interval in intervals.OrderBy(i => i.Start))
            {
                if (to is { } end && interval.Start <= end)
                {
                    to = interval.End > end ? interval.End : end;
                    continue;
                }

                total += to - from ?? TimeSpan.Zero;
                (from, to) = (interval.Start, interval.End);
            }

            return total + (to - from ?? TimeSpan.Zero);
        });

    /// <inheritdoc />
    public Task<ReviewPreflight?> PreflightAsync(string jobId, CancellationToken ct) =>
        RunAsync("read", async db =>
            await db.Jobs.Where(j => j.Id == jobId).Select(j => j.Preflight).SingleOrDefaultAsync(ct).ConfigureAwait(false) is { } kept
                ? JsonSerializer.Deserialize<ReviewPreflight>(kept, JsonSerializerOptions.Web)
                : null);

    /// <inheritdoc />
    public Task<ReviewInputs?> InputsAsync(string jobId, CancellationToken ct) =>
        RunAsync("read", async db =>
        {
            var kept = await db.Jobs.Where(j => j.Id == jobId).Select(j => j.Inputs).SingleOrDefaultAsync(ct).ConfigureAwait(false);
            try
            {
                return kept is null ? null : JsonSerializer.Deserialize<ReviewInputs>(kept, JsonSerializerOptions.Web);
            }
            catch (JsonException)
            {
                return null;
            }
        });
}
