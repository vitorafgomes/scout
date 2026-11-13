using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Scout.Conversation.Abstractions;
using Scout.Meetings.Storage;

namespace Scout.Meetings;

/// <summary>
/// Keeps meetings in one SQLite file under Scout's data folder through Entity Framework: the schema is brought up to date
/// by the migrations on the store's first call, and a file an older build wrote, with tables and no migration history, is
/// deleted and created again. Foreign keys cascade a meeting's lines, turns and keep key, and every delete is followed by
/// <c>VACUUM</c> and a WAL checkpoint under <c>secure_delete</c>, so deleted text does not linger in the file. One context
/// per call.
/// </summary>
public sealed class SqliteMeetingStore : IMeetingStore
{
    internal const int MaxLines = 5000;
    internal const int MaxTurns = 500;
    internal const int MaxTitleChars = 200;
    internal const int MaxLineChars = 2000;

    private static readonly string[] PreviousSchemaTables = ["schema_version", "meetings", "lines", "turns", "keeps"];

    private readonly string _dbPath;
    private readonly DbContextOptions<MeetingsDbContext> _options;
    private readonly Lock _schemaGate = new();
    private Task? _schema;

    /// <summary>Creates the store over the database file at <paramref name="dbPath"/>; nothing is opened until the first call.</summary>
    public SqliteMeetingStore(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        _dbPath = Path.GetFullPath(dbPath);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
        }.ToString();
        _options = new DbContextOptionsBuilder<MeetingsDbContext>().UseSqlite(connectionString).Options;
    }

    /// <inheritdoc />
    public async Task<string> KeepAsync(MeetingRecord meeting, string? keepKey, CancellationToken ct)
    {
        Validate(meeting);
        var id = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        try
        {
            await using var db = await OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

            if (keepKey is not null
                && await db.Keeps.Where(k => k.Key == keepKey).Select(k => k.MeetingId).FirstOrDefaultAsync(ct).ConfigureAwait(false) is { } kept)
            {
                return kept;
            }

            db.Meetings.Add(new MeetingRow
            {
                Id = id,
                StartedAt = meeting.StartedAt,
                StartedUtcTicks = meeting.StartedAt.UtcTicks,
                EndedAt = meeting.EndedAt,
                Title = meeting.Title,
                Lines = [.. meeting.Lines.Select((line, i) => new LineRow
                {
                    Seq = i + 1,
                    At = line.At,
                    Lane = line.Lane,
                    Text = line.Text,
                    Translation = line.Translation,
                    Language = line.Language,
                })],
                Turns = [.. meeting.Turns.Select((turn, i) => new TurnRow
                {
                    Seq = i + 1,
                    At = turn.At,
                    Question = turn.Question,
                    Answer = turn.Answer,
                    Screenshot = turn.Screenshot,
                    Transcript = turn.Transcript,
                    ScreenText = turn.ScreenText,
                })],
            });
            if (keepKey is not null)
            {
                db.Keeps.Add(new KeepRow { Key = keepKey, MeetingId = id });
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return id;
        }
        catch (Exception ex) when (Failure("save", ex) is { } failure)
        {
            throw failure;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MeetingSummary>> ListAsync(int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        try
        {
            await using var db = await OpenAsync(ct).ConfigureAwait(false);
            return await Newest(db, limit).ToListAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (Failure("list", ex) is { } failure)
        {
            throw failure;
        }
    }

    /// <inheritdoc />
    public async Task<MeetingRecord?> GetAsync(string id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        try
        {
            await using var db = await OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

            var meeting = await db.Meetings.AsNoTracking()
                .Where(m => m.Id == id)
                .Select(m => new { m.StartedAt, m.EndedAt, m.Title })
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (meeting is null)
            {
                return null;
            }

            var lines = await db.Lines
                .Where(l => l.MeetingId == id)
                .OrderBy(l => l.Seq)
                .Select(l => new MeetingLine(l.At, l.Lane, l.Text, l.Translation, l.Language))
                .ToListAsync(ct).ConfigureAwait(false);
            var turns = await db.Turns
                .Where(t => t.MeetingId == id)
                .OrderBy(t => t.Seq)
                .Select(t => new MeetingTurn(t.At, t.Question, t.Answer, t.Screenshot, t.Transcript, t.ScreenText))
                .ToListAsync(ct).ConfigureAwait(false);

            return new MeetingRecord(id, meeting.StartedAt, meeting.EndedAt, meeting.Title, lines, turns);
        }
        catch (Exception ex) when (Failure("read", ex) is { } failure)
        {
            throw failure;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        try
        {
            await using var db = await OpenAsync(ct).ConfigureAwait(false);
            var deleted = await db.Meetings.Where(m => m.Id == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await ScrubAsync(db).ConfigureAwait(false);
            return deleted != 0;
        }
        catch (Exception ex) when (Failure("delete", ex) is { } failure)
        {
            throw failure;
        }
    }

    /// <inheritdoc />
    public async Task<int> DeleteAllAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await OpenAsync(ct).ConfigureAwait(false);
            var deleted = await db.Meetings.ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await ScrubAsync(db).ConfigureAwait(false);
            return deleted;
        }
        catch (Exception ex) when (Failure("delete", ex) is { } failure)
        {
            throw failure;
        }
    }

    internal static IQueryable<MeetingSummary> Newest(MeetingsDbContext db, int limit) =>
        db.Meetings
            .OrderByDescending(m => m.StartedUtcTicks)
            .ThenByDescending(m => m.Number)
            .Take(limit)
            .Select(m => new MeetingSummary(m.Id, m.StartedAt, m.EndedAt, m.Title, m.Lines.Count, m.Turns.Count));

    internal static void Validate(MeetingRecord meeting)
    {
        ArgumentNullException.ThrowIfNull(meeting);
        if (string.IsNullOrWhiteSpace(meeting.Title)
            || meeting.Title.Length > MaxTitleChars
            || meeting.Lines is null
            || meeting.Turns is null
            || meeting.Lines.Count > MaxLines
            || meeting.Turns.Count > MaxTurns
            || meeting.Lines.Any(l => l.Text is null || l.Text.Length > MaxLineChars))
        {
            throw new ArgumentException(
                $"A meeting holds at most {MaxLines} lines of at most {MaxLineChars} characters and {MaxTurns} turns, with a title of 1 to {MaxTitleChars} characters.",
                nameof(meeting));
        }
    }

    /// <summary>A context on an open connection under <c>secure_delete</c>, over a schema the migrations brought up to date.</summary>
    private async Task<MeetingsDbContext> OpenAsync(CancellationToken ct)
    {
        await SchemaAsync().ConfigureAwait(false);
        var db = new MeetingsDbContext(_options);
        try
        {
            await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync("PRAGMA secure_delete = ON;", ct).ConfigureAwait(false);
            return db;
        }
        catch
        {
            await db.DisposeAsync().ConfigureAwait(false);
            throw;
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
    /// Opens the file for writing, applies the migrations this build has and the file lacks, and keeps the file in WAL
    /// mode; a file with tables and no migration history was written by a build before Entity Framework and is deleted
    /// first, with its write-ahead log, but only when every table it holds is one this store knows; a file with any other
    /// table is left as it is and the call fails.
    /// </summary>
    private async Task MigrateAsync()
    {
        await using var db = new MeetingsDbContext(_options);
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
    private static IEnumerable<string> KnownTables(MeetingsDbContext db) =>
        db.Model.GetEntityTypes().Select(e => e.GetTableName()!).Concat(["__EFMigrationsHistory", "__EFMigrationsLock", .. PreviousSchemaTables]);

    private static Task<int> ScrubAsync(MeetingsDbContext db) =>
        db.Database.ExecuteSqlRawAsync("VACUUM; PRAGMA wal_checkpoint(TRUNCATE);", CancellationToken.None);

    private static SqliteException? Sqlite(Exception ex) => ex as SqliteException ?? (ex as DbUpdateException)?.InnerException as SqliteException;

    /// <summary>The store's own exception for a failure of the database or of its file, or null for any other exception.</summary>
    private static MeetingStoreException? Failure(string operation, Exception ex) => ex switch
    {
        _ when Sqlite(ex) is { } sqlite => new($"Scout could not {operation} the meetings database (SQLite error {sqlite.SqliteErrorCode}).", sqlite),
        InvalidDataException => new($"Scout could not {operation} the meetings database: {ex.Message}.", ex),
        IOException or UnauthorizedAccessException => new($"Scout could not {operation} the meetings database ({ex.GetType().Name}).", ex),
        _ => null,
    };
}
