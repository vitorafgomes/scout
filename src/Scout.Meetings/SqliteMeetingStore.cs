using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Scout.Conversation.Abstractions;

namespace Scout.Meetings;

/// <summary>
/// Keeps meetings in one SQLite file under Scout's data folder: the schema is created on first open, foreign keys
/// cascade a meeting's lines and turns, and every delete is followed by <c>VACUUM</c> and a WAL checkpoint under
/// <c>secure_delete</c>, so deleted text does not linger in the file. One connection per call.
/// </summary>
public sealed class SqliteMeetingStore : IMeetingStore
{
    internal const int MaxLines = 5000;
    internal const int MaxTurns = 500;
    internal const int MaxTitleChars = 200;
    internal const int MaxLineChars = 2000;

    private const string Schema = """
        PRAGMA foreign_keys = ON;
        PRAGMA secure_delete = ON;
        PRAGMA journal_mode = WAL;
        CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);
        INSERT INTO schema_version (version) SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM schema_version);
        CREATE TABLE IF NOT EXISTS meetings (
            id TEXT PRIMARY KEY,
            started_at TEXT NOT NULL,
            ended_at TEXT NOT NULL,
            title TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS lines (
            meeting_id TEXT NOT NULL REFERENCES meetings (id) ON DELETE CASCADE,
            seq INTEGER NOT NULL,
            at TEXT NOT NULL,
            lane TEXT NOT NULL,
            text TEXT NOT NULL,
            translation TEXT,
            language TEXT,
            PRIMARY KEY (meeting_id, seq));
        CREATE TABLE IF NOT EXISTS turns (
            meeting_id TEXT NOT NULL REFERENCES meetings (id) ON DELETE CASCADE,
            seq INTEGER NOT NULL,
            at TEXT NOT NULL,
            question TEXT NOT NULL,
            answer TEXT NOT NULL,
            screenshot INTEGER NOT NULL,
            transcript INTEGER NOT NULL,
            screen_text INTEGER NOT NULL,
            PRIMARY KEY (meeting_id, seq));
        """;

    private readonly string _connectionString;

    /// <summary>Creates the store over the database file at <paramref name="dbPath"/>; nothing is opened until the first call.</summary>
    public SqliteMeetingStore(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    /// <inheritdoc />
    public async Task<string> KeepAsync(MeetingRecord meeting, CancellationToken ct)
    {
        Validate(meeting);
        var id = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            await using (var insert = Command(
                connection, "INSERT INTO meetings (id, started_at, ended_at, title) VALUES ($id, $started, $ended, $title);"))
            {
                insert.Parameters.AddWithValue("$id", id);
                insert.Parameters.AddWithValue("$started", Format(meeting.StartedAt));
                insert.Parameters.AddWithValue("$ended", Format(meeting.EndedAt));
                insert.Parameters.AddWithValue("$title", meeting.Title);
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using (var line = Command(
                connection,
                "INSERT INTO lines (meeting_id, seq, at, lane, text, translation, language) VALUES ($id, $seq, $at, $lane, $text, $translation, $language);"))
            {
                var seq = line.Parameters.Add("$seq", SqliteType.Integer);
                var at = line.Parameters.Add("$at", SqliteType.Text);
                var lane = line.Parameters.Add("$lane", SqliteType.Text);
                var text = line.Parameters.Add("$text", SqliteType.Text);
                var translation = line.Parameters.Add("$translation", SqliteType.Text);
                var language = line.Parameters.Add("$language", SqliteType.Text);
                line.Parameters.AddWithValue("$id", id);
                for (var i = 0; i < meeting.Lines.Count; i++)
                {
                    var current = meeting.Lines[i];
                    seq.Value = i + 1;
                    at.Value = Format(current.At);
                    lane.Value = current.Lane;
                    text.Value = current.Text;
                    translation.Value = (object?)current.Translation ?? DBNull.Value;
                    language.Value = (object?)current.Language ?? DBNull.Value;
                    await line.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }

            await using (var turn = Command(
                connection,
                "INSERT INTO turns (meeting_id, seq, at, question, answer, screenshot, transcript, screen_text) VALUES ($id, $seq, $at, $question, $answer, $screenshot, $transcript, $screenText);"))
            {
                var seq = turn.Parameters.Add("$seq", SqliteType.Integer);
                var at = turn.Parameters.Add("$at", SqliteType.Text);
                var question = turn.Parameters.Add("$question", SqliteType.Text);
                var answer = turn.Parameters.Add("$answer", SqliteType.Text);
                var screenshot = turn.Parameters.Add("$screenshot", SqliteType.Integer);
                var transcript = turn.Parameters.Add("$transcript", SqliteType.Integer);
                var screenText = turn.Parameters.Add("$screenText", SqliteType.Integer);
                turn.Parameters.AddWithValue("$id", id);
                for (var i = 0; i < meeting.Turns.Count; i++)
                {
                    var current = meeting.Turns[i];
                    seq.Value = i + 1;
                    at.Value = Format(current.At);
                    question.Value = current.Question;
                    answer.Value = current.Answer;
                    screenshot.Value = current.Screenshot ? 1 : 0;
                    transcript.Value = current.Transcript ? 1 : 0;
                    screenText.Value = current.ScreenText ? 1 : 0;
                    await turn.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return id;
        }
        catch (SqliteException ex)
        {
            throw Failed("save", ex);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MeetingSummary>> ListAsync(int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var list = Command(
                connection,
                """
                SELECT m.id, m.started_at, m.ended_at, m.title,
                    (SELECT COUNT(*) FROM lines l WHERE l.meeting_id = m.id),
                    (SELECT COUNT(*) FROM turns t WHERE t.meeting_id = m.id)
                FROM meetings m
                ORDER BY julianday(m.started_at) DESC, m.rowid DESC
                LIMIT $limit;
                """);
            list.Parameters.AddWithValue("$limit", limit);
            await using var reader = await list.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var summaries = new List<MeetingSummary>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                summaries.Add(new MeetingSummary(
                    reader.GetString(0),
                    Parse(reader.GetString(1)),
                    Parse(reader.GetString(2)),
                    reader.GetString(3),
                    reader.GetInt32(4),
                    reader.GetInt32(5)));
            }
            return summaries;
        }
        catch (SqliteException ex)
        {
            throw Failed("list", ex);
        }
    }

    /// <inheritdoc />
    public async Task<MeetingRecord?> GetAsync(string id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            DateTimeOffset startedAt, endedAt;
            string title;
            await using (var meeting = Command(connection, "SELECT started_at, ended_at, title FROM meetings WHERE id = $id;"))
            {
                meeting.Parameters.AddWithValue("$id", id);
                await using var reader = await meeting.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    return null;
                }
                startedAt = Parse(reader.GetString(0));
                endedAt = Parse(reader.GetString(1));
                title = reader.GetString(2);
            }

            var lines = new List<MeetingLine>();
            await using (var select = Command(
                connection, "SELECT at, lane, text, translation, language FROM lines WHERE meeting_id = $id ORDER BY seq;"))
            {
                select.Parameters.AddWithValue("$id", id);
                await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    lines.Add(new MeetingLine(
                        Parse(reader.GetString(0)),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4)));
                }
            }

            var turns = new List<MeetingTurn>();
            await using (var select = Command(
                connection,
                "SELECT at, question, answer, screenshot, transcript, screen_text FROM turns WHERE meeting_id = $id ORDER BY seq;"))
            {
                select.Parameters.AddWithValue("$id", id);
                await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    turns.Add(new MeetingTurn(
                        Parse(reader.GetString(0)),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetInt64(3) != 0,
                        reader.GetInt64(4) != 0,
                        reader.GetInt64(5) != 0));
                }
            }

            return new MeetingRecord(id, startedAt, endedAt, title, lines, turns);
        }
        catch (SqliteException ex)
        {
            throw Failed("read", ex);
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            int deleted;
            await using (var delete = Command(connection, "DELETE FROM meetings WHERE id = $id;"))
            {
                delete.Parameters.AddWithValue("$id", id);
                deleted = await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await ScrubAsync(connection).ConfigureAwait(false);
            return deleted != 0;
        }
        catch (SqliteException ex)
        {
            throw Failed("delete", ex);
        }
    }

    /// <inheritdoc />
    public async Task DeleteAllAsync(CancellationToken ct)
    {
        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using (var delete = Command(connection, "DELETE FROM meetings;"))
            {
                await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await ScrubAsync(connection).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            throw Failed("delete", ex);
        }
    }

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

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var schema = Command(connection, Schema);
            await schema.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ScrubAsync(SqliteConnection connection)
    {
        await using var scrub = Command(connection, "VACUUM; PRAGMA wal_checkpoint(TRUNCATE);");
        await scrub.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static MeetingStoreException Failed(string operation, SqliteException ex)
        => new($"Scout could not {operation} the meetings database (SQLite error {ex.SqliteErrorCode}).", ex);
}
