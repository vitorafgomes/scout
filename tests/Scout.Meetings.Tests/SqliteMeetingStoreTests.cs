using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Scout.Conversation.Abstractions;
using Scout.Meetings.Storage;

namespace Scout.Meetings.Tests;

public sealed class SqliteMeetingStoreTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 4, 10, 0, 0, TimeSpan.FromHours(1));

    private readonly string _folder = Directory.CreateTempSubdirectory("scout-meetings-").FullName;

    private string DbPath => Path.Combine(_folder, "meetings.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    internal static MeetingRecord Meeting(
        string title = "Weekly sync",
        DateTimeOffset? startedAt = null,
        IReadOnlyList<MeetingLine>? lines = null,
        IReadOnlyList<MeetingTurn>? turns = null)
    {
        var started = startedAt ?? Start;
        return new MeetingRecord(
            null,
            started,
            started.AddMinutes(30),
            title,
            lines ??
            [
                new MeetingLine(started.AddSeconds(5), "me", "Can we ship on Friday?", null, null),
                new MeetingLine(started.AddSeconds(9), "others", "Sim, se os testes passarem.", "Yes, if the tests pass.", "en"),
            ],
            turns ??
            [
                new MeetingTurn(started.AddMinutes(2), "What did they promise?", "Friday, if the tests pass.", false, true, false),
                new MeetingTurn(started.AddMinutes(4), "What is on the slide?", "The release checklist.", true, false, true),
            ]);
    }

    private static IReadOnlyList<MeetingLine> Lines(int count, string text)
        => [.. Enumerable.Range(0, count).Select(i => new MeetingLine(Start.AddSeconds(i), i % 2 == 0 ? "me" : "others", text, null, null))];

    private static IReadOnlyList<MeetingTurn> Turns(int count)
        => [.. Enumerable.Range(0, count).Select(i => new MeetingTurn(Start.AddSeconds(i), $"question {i}", $"answer {i}", false, false, false))];

    private long Scalar(string sql, string? id = null)
    {
        using var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (id is not null)
        {
            command.Parameters.AddWithValue("$id", id);
        }
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private string FileText()
    {
        var text = new StringBuilder();
        foreach (var file in new[] { DbPath, DbPath + "-wal" }.Where(File.Exists))
        {
            text.Append(Encoding.UTF8.GetString(File.ReadAllBytes(file)));
        }
        return text.ToString();
    }

    [Fact]
    public async Task Keep_Then_Get_Returns_The_Lines_Turns_And_Translations()
    {
        var store = new SqliteMeetingStore(DbPath);
        var meeting = Meeting();

        var id = await store.KeepAsync(meeting, null, CancellationToken.None);
        var kept = await store.GetAsync(id, CancellationToken.None);

        id.Length.ShouldBe(22, "an id is 16 random bytes in base64url");
        kept.ShouldNotBeNull();
        kept.Id.ShouldBe(id);
        kept.Title.ShouldBe("Weekly sync");
        kept.StartedAt.ShouldBe(meeting.StartedAt);
        new[] { kept.StartedAt, kept.EndedAt }.Concat(kept.Lines.Select(l => l.At)).Concat(kept.Turns.Select(t => t.At)).Select(t => t.Offset).ShouldAllBe(o => o == TimeSpan.FromHours(1), "every time keeps the offset the desktop sent");
        kept.EndedAt.ShouldBe(meeting.EndedAt);
        kept.Lines.ShouldBe(meeting.Lines, "both lanes, in order, with the translation and its language when there was one");
        kept.Turns.ShouldBe(meeting.Turns, "each question with its answer and its screenshot, transcript and screen text flags");
    }

    [Fact]
    public async Task Each_Keep_Gets_Its_Own_Id_And_The_Schema_Is_Version_1_In_Wal_Mode()
    {
        var store = new SqliteMeetingStore(DbPath);

        var first = await store.KeepAsync(Meeting(), null, CancellationToken.None);
        var second = await store.KeepAsync(Meeting(), null, CancellationToken.None);

        first.ShouldNotBe(second);
        first.ShouldMatch("^[A-Za-z0-9_-]{22}$");
        Scalar("SELECT version FROM schema_version").ShouldBe(1);
        Scalar("SELECT COUNT(*) FROM schema_version").ShouldBe(1, "a second open does not add a second version row");
        Scalar("SELECT COUNT(*) FROM pragma_journal_mode WHERE journal_mode = 'wal'").ShouldBe(1);
    }

    [Fact]
    public async Task A_Retried_Keep_Under_The_Same_Key_After_The_Worker_Restarted_Stores_The_Meeting_Once_And_Answers_The_Same_Id()
    {
        var first = await new SqliteMeetingStore(DbPath).KeepAsync(Meeting(), "keep-1", CancellationToken.None);
        SqliteConnection.ClearAllPools();

        var retried = await new SqliteMeetingStore(DbPath).KeepAsync(Meeting(), "keep-1", CancellationToken.None);

        retried.ShouldBe(first);
        Scalar("SELECT COUNT(*) FROM meetings").ShouldBe(1, "the retry of a keep whose answer was lost keeps nothing more");
        Scalar("SELECT COUNT(*) FROM lines").ShouldBe(2);
    }

    [Fact]
    public async Task Keeps_Under_Different_Keys_Each_Store_Their_Own_Meeting()
    {
        var store = new SqliteMeetingStore(DbPath);

        var first = await store.KeepAsync(Meeting(), "keep-1", CancellationToken.None);
        var second = await store.KeepAsync(Meeting(), "keep-2", CancellationToken.None);

        first.ShouldNotBe(second);
        Scalar("SELECT COUNT(*) FROM meetings").ShouldBe(2);
    }

    [Fact]
    public async Task Deleting_A_Meeting_Kept_Under_A_Key_Removes_The_Key_So_A_New_Keep_Under_It_Gets_A_New_Id()
    {
        var store = new SqliteMeetingStore(DbPath);
        var first = await store.KeepAsync(Meeting(), "keep-1", CancellationToken.None);

        var deleted = await store.DeleteAsync(first, CancellationToken.None);
        var keysLeft = Scalar("SELECT COUNT(*) FROM keeps");
        var again = await store.KeepAsync(Meeting(), "keep-1", CancellationToken.None);

        deleted.ShouldBeTrue();
        keysLeft.ShouldBe(0);
        again.ShouldNotBe(first);
    }

    [Fact]
    public async Task Deleting_All_Meetings_Kept_Under_Keys_Removes_The_Keys_So_A_New_Keep_Under_One_Gets_A_New_Id()
    {
        var store = new SqliteMeetingStore(DbPath);
        var first = await store.KeepAsync(Meeting(), "keep-1", CancellationToken.None);
        await store.KeepAsync(Meeting("Second"), "keep-2", CancellationToken.None);

        await store.DeleteAllAsync(CancellationToken.None);
        var keysLeft = Scalar("SELECT COUNT(*) FROM keeps");
        var again = await store.KeepAsync(Meeting(), "keep-1", CancellationToken.None);

        keysLeft.ShouldBe(0);
        again.ShouldNotBe(first);
        Scalar("SELECT COUNT(*) FROM meetings").ShouldBe(1);
    }

    [Fact]
    public async Task List_Is_Newest_First_With_Counts()
    {
        var store = new SqliteMeetingStore(DbPath);
        var middle = await store.KeepAsync(
            Meeting("Middle", new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero)), null, CancellationToken.None);
        var newest = await store.KeepAsync(
            Meeting("Newest", new DateTimeOffset(2026, 11, 4, 9, 30, 0, TimeSpan.FromHours(-1)), Lines(3, "line"), Turns(1)),
            null, CancellationToken.None);
        var oldest = await store.KeepAsync(
            Meeting("Oldest", new DateTimeOffset(2026, 11, 4, 11, 0, 0, TimeSpan.FromHours(2)), [], []),
            null, CancellationToken.None);

        var all = await store.ListAsync(10, CancellationToken.None);
        var two = await store.ListAsync(2, CancellationToken.None);

        all.Select(m => m.Id).ShouldBe([newest, middle, oldest], "10:30 UTC, then 10:00 UTC, then 09:00 UTC, whatever the offset");
        all.Select(m => (m.Title, m.LineCount, m.TurnCount)).ShouldBe([("Newest", 3, 1), ("Middle", 2, 2), ("Oldest", 0, 0)]);
        all[0].StartedAt.ShouldBe(new DateTimeOffset(2026, 11, 4, 9, 30, 0, TimeSpan.FromHours(-1)));
        all[0].EndedAt.ShouldBe(all[0].StartedAt.AddMinutes(30));
        two.Select(m => m.Id).ShouldBe([newest, middle]);
    }

    [Fact]
    public async Task Delete_Removes_The_Meeting_Its_Lines_And_Turns_And_Vacuums()
    {
        var store = new SqliteMeetingStore(DbPath);
        var kept = await store.KeepAsync(Meeting("Kept"), null, CancellationToken.None);
        var doomed = await store.KeepAsync(
            Meeting("Doomed", lines: Lines(2000, "DOOMED-LINE " + new string('x', 1000)), turns: Turns(500)),
            null, CancellationToken.None);

        var deleted = await store.DeleteAsync(doomed, CancellationToken.None);

        deleted.ShouldBeTrue();
        (await store.GetAsync(doomed, CancellationToken.None)).ShouldBeNull();
        Scalar("SELECT COUNT(*) FROM lines WHERE meeting_id = $id", doomed).ShouldBe(0, "the lines cascade with the meeting");
        Scalar("SELECT COUNT(*) FROM turns WHERE meeting_id = $id", doomed).ShouldBe(0, "the turns cascade with the meeting");
        FileText().ShouldNotContain("DOOMED-LINE", Case.Sensitive, "the deleted text does not linger in the database or its WAL");
        Scalar("PRAGMA freelist_count").ShouldBe(0, "VACUUM ran after the delete, so no freed page keeps old text");
        (await store.GetAsync(kept, CancellationToken.None)).ShouldNotBeNull().Lines.Count.ShouldBe(2);
        (await store.DeleteAsync(doomed, CancellationToken.None)).ShouldBeFalse("a meeting that is gone cannot be deleted twice");
    }

    [Fact]
    public async Task Delete_All_Empties_The_Store()
    {
        var store = new SqliteMeetingStore(DbPath);
        var first = await store.KeepAsync(Meeting("First", lines: Lines(1000, "ALL-GONE " + new string('y', 500))), null, CancellationToken.None);
        await store.KeepAsync(Meeting("Second"), null, CancellationToken.None);

        await store.DeleteAllAsync(CancellationToken.None);

        (await store.ListAsync(10, CancellationToken.None)).ShouldBeEmpty();
        (await store.GetAsync(first, CancellationToken.None)).ShouldBeNull();
        Scalar("SELECT COUNT(*) FROM lines").ShouldBe(0);
        Scalar("SELECT COUNT(*) FROM turns").ShouldBe(0);
        FileText().ShouldNotContain("ALL-GONE", Case.Sensitive);
        Scalar("PRAGMA freelist_count").ShouldBe(0);
    }

    [Theory]
    [InlineData("one")]
    [InlineData("all")]
    public async Task A_Delete_Scrubs_Even_When_The_Caller_Cancels_After_The_Row_Is_Gone(string scope)
    {
        var store = new SqliteMeetingStore(DbPath);
        var doomed = await store.KeepAsync(Meeting("Doomed", lines: Lines(2000, "CANCELLED-LINE " + new string('x', 1000))), null, CancellationToken.None);
        using (var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            connection.Open();
            using var trigger = connection.CreateCommand();
            trigger.CommandText = """
                CREATE TRIGGER slow_delete AFTER DELETE ON meetings BEGIN
                    SELECT count(*) FROM (WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < 6000000) SELECT x FROM c);
                END;
                """;
            trigger.ExecuteNonQuery();
        }
        using var cancel = new CancellationTokenSource();

        var running = Task.Run(() => scope == "one" ? store.DeleteAsync(doomed, cancel.Token) : store.DeleteAllAsync(cancel.Token));
        await Task.Delay(250);
        await cancel.CancelAsync();
        await running;

        Scalar("SELECT COUNT(*) FROM meetings").ShouldBe(0, "the delete had already started when the caller cancelled");
        FileText().ShouldNotContain("CANCELLED-LINE", Case.Sensitive, "the scrub does not take the caller's token, so a cancelled stop cannot leave the deleted text on disk");
        Scalar("PRAGMA freelist_count").ShouldBe(0);
    }

    [Fact]
    public async Task A_Retried_Delete_Of_A_Gone_Meeting_Still_Scrubs()
    {
        var store = new SqliteMeetingStore(DbPath);
        var doomed = await store.KeepAsync(Meeting("Doomed", lines: Lines(2000, "RETRIED-LINE " + new string('x', 1000))), null, CancellationToken.None);
        using (var reader = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            reader.Open();
            using var snapshot = reader.CreateCommand();
            snapshot.CommandText = "BEGIN; SELECT COUNT(*) FROM meetings;";
            snapshot.ExecuteScalar();

            (await store.DeleteAsync(doomed, CancellationToken.None)).ShouldBeTrue();

            FileText().ShouldContain("RETRIED-LINE", Case.Sensitive, "a reader holding the log back leaves the checkpoint with nothing to truncate");
        }

        (await store.DeleteAsync(doomed, CancellationToken.None)).ShouldBeFalse("the meeting is already gone");

        FileText().ShouldNotContain("RETRIED-LINE", Case.Sensitive, "a retry of a delete whose scrub did not finish repairs it");
        Scalar("PRAGMA freelist_count").ShouldBe(0);
    }

    public static TheoryData<string> PastTheLimits => ["5001 lines", "501 turns", "an empty title", "a 201-character title", "a 2001-character line"];

    private static MeetingRecord PastTheLimit(string limit) => limit switch
    {
        "5001 lines" => Meeting(lines: Lines(5001, "line")),
        "501 turns" => Meeting(turns: Turns(501)),
        "an empty title" => Meeting(title: " "),
        "a 201-character title" => Meeting(title: new string('t', 201)),
        "a 2001-character line" => Meeting(lines: Lines(1, new string('l', 2001))),
        _ => throw new ArgumentOutOfRangeException(nameof(limit)),
    };

    [Theory]
    [MemberData(nameof(PastTheLimits))]
    public async Task A_Meeting_Past_The_Limits_Is_Refused_And_Nothing_Is_Stored(string limit)
    {
        var store = new SqliteMeetingStore(DbPath);

        await Should.ThrowAsync<ArgumentException>(() => store.KeepAsync(PastTheLimit(limit), null, CancellationToken.None));

        (await store.ListAsync(10, CancellationToken.None)).ShouldBeEmpty($"a meeting with {limit} stores nothing");
    }

    [Fact]
    public async Task A_Meeting_At_The_Limits_Is_Kept()
    {
        var store = new SqliteMeetingStore(DbPath);

        var id = await store.KeepAsync(
            Meeting(new string('t', 200), lines: Lines(5000, new string('l', 2000)), turns: Turns(500)), null, CancellationToken.None);

        var summary = (await store.ListAsync(1, CancellationToken.None)).ShouldHaveSingleItem();
        summary.Id.ShouldBe(id);
        (summary.LineCount, summary.TurnCount).ShouldBe((5000, 500));
    }

    [Fact]
    public async Task An_Unreadable_Database_Throws_Without_The_Meeting_Text()
    {
        await File.WriteAllBytesAsync(DbPath, Encoding.UTF8.GetBytes(new string('?', 4096)));
        var store = new SqliteMeetingStore(DbPath);
        var meeting = Meeting(
            "Salary review with Dana",
            lines: [new MeetingLine(Start, "others", "the offer is ninety thousand", "a oferta", "pt")],
            turns: [new MeetingTurn(Start, "what was the offer?", "ninety thousand", false, true, false)]);

        var keep = await Should.ThrowAsync<MeetingStoreException>(() => store.KeepAsync(meeting, null, CancellationToken.None));
        var list = await Should.ThrowAsync<MeetingStoreException>(() => store.ListAsync(10, CancellationToken.None));

        keep.Message.ShouldBe("Scout could not save the meetings database (SQLite error 26).");
        list.Message.ShouldBe("Scout could not list the meetings database (SQLite error 26).");
        foreach (var text in new[] { "Salary review", "ninety thousand", "the offer", "a oferta", "what was the offer" })
        {
            keep.ToString().ShouldNotContain(text, Case.Sensitive);
        }
    }

    [Fact]
    public async Task A_Keyed_Keep_On_An_Unreadable_Database_Throws_Without_The_Meeting_Text_Or_The_Key()
    {
        await File.WriteAllBytesAsync(DbPath, Encoding.UTF8.GetBytes(new string('?', 4096)));
        var store = new SqliteMeetingStore(DbPath);

        var keep = await Should.ThrowAsync<MeetingStoreException>(
            () => store.KeepAsync(Meeting("Salary review with Dana"), "keep-secret-1", CancellationToken.None));

        keep.Message.ShouldBe("Scout could not save the meetings database (SQLite error 26).");
        keep.ToString().ShouldNotContain("Salary review", Case.Sensitive);
        keep.ToString().ShouldNotContain("keep-secret-1", Case.Sensitive);
    }

    [Fact]
    public async Task A_Keep_The_Database_Refuses_To_Write_Throws_Without_The_Meeting_Text_And_Stores_Nothing()
    {
        var earlier = await new SqliteMeetingStore(DbPath).KeepAsync(Meeting("Earlier"), null, CancellationToken.None);
        SqliteConnection.ClearAllPools();
        File.SetAttributes(DbPath, FileAttributes.ReadOnly);
        var store = new SqliteMeetingStore(DbPath);

        var keep = await Should.ThrowAsync<MeetingStoreException>(
            () => store.KeepAsync(Meeting("Salary review with Dana"), "keep-secret-1", CancellationToken.None));

        keep.Message.ShouldBe("Scout could not save the meetings database (SQLite error 8).");
        keep.ToString().ShouldNotContain("Salary review", Case.Sensitive);
        keep.ToString().ShouldNotContain("keep-secret-1", Case.Sensitive);
        (await store.ListAsync(10, CancellationToken.None)).Select(m => m.Id).ShouldBe([earlier], "a refused keep stores no part of the meeting");
    }

    [Fact]
    public async Task A_Database_An_Older_Build_Wrote_Without_The_Migration_History_Is_Created_Again()
    {
        using (var old = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            old.Open();
            using var schema = old.CreateCommand();
            schema.CommandText = """
                PRAGMA journal_mode = WAL;
                CREATE TABLE schema_version (version INTEGER NOT NULL);
                INSERT INTO schema_version (version) VALUES (1);
                CREATE TABLE meetings (id TEXT PRIMARY KEY, started_at TEXT NOT NULL, ended_at TEXT NOT NULL, title TEXT NOT NULL);
                INSERT INTO meetings VALUES ('old-meeting', '2026-11-04T10:00:00.0000000+01:00', '2026-11-04T10:30:00.0000000+01:00', 'Old sync');
                """;
            schema.ExecuteNonQuery();
        }
        var store = new SqliteMeetingStore(DbPath);

        var before = await store.ListAsync(10, CancellationToken.None);
        var id = await store.KeepAsync(Meeting(), null, CancellationToken.None);

        before.ShouldBeEmpty("the older build's file is deleted and created again, so its meetings are gone");
        (await store.GetAsync("old-meeting", CancellationToken.None)).ShouldBeNull();
        (await store.GetAsync(id, CancellationToken.None)).ShouldNotBeNull().Lines.Count.ShouldBe(2);
        Scalar("SELECT COUNT(*) FROM schema_version").ShouldBe(1);
    }

    private void OlderBuildFile(string extraTable = "")
    {
        using var old = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        old.Open();
        using var schema = old.CreateCommand();
        schema.CommandText = $"""
            CREATE TABLE schema_version (version INTEGER NOT NULL);
            INSERT INTO schema_version (version) VALUES (1);
            CREATE TABLE meetings (id TEXT PRIMARY KEY, started_at TEXT NOT NULL, ended_at TEXT NOT NULL, title TEXT NOT NULL);
            INSERT INTO meetings VALUES ('old-meeting', '2026-11-04T10:00:00.0000000+01:00', '2026-11-04T10:30:00.0000000+01:00', 'Old sync');
            {extraTable}
            """;
        schema.ExecuteNonQuery();
    }

    private static void FolderMode(string folder, bool writable)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The read-only folder is made with Unix permissions.");
        }

        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute | (writable ? UnixFileMode.UserWrite : UnixFileMode.None));
    }

    [Fact]
    public async Task An_Older_Build_File_In_A_Folder_Scout_Cannot_Write_To_Is_A_Store_Failure_And_Stays_Where_It_Is()
    {
        OlderBuildFile();
        FolderMode(_folder, writable: false);
        try
        {
            var list = await Should.ThrowAsync<MeetingStoreException>(() => new SqliteMeetingStore(DbPath).ListAsync(10, CancellationToken.None));

            list.Message.ShouldBe("Scout could not list the meetings database (UnauthorizedAccessException).");
            File.Exists(DbPath).ShouldBeTrue("a file Scout could not delete is left as it was");
        }
        finally
        {
            FolderMode(_folder, writable: true);
        }
    }

    [Fact]
    public async Task An_Older_Build_File_Holding_A_Table_Scout_Did_Not_Create_Is_Never_Deleted_And_The_Call_Says_Why()
    {
        OlderBuildFile("CREATE TABLE notes (body TEXT); INSERT INTO notes VALUES ('keep me');");

        var list = await Should.ThrowAsync<MeetingStoreException>(() => new SqliteMeetingStore(DbPath).ListAsync(10, CancellationToken.None));

        list.Message.ShouldBe("Scout could not list the meetings database: it holds tables Scout did not create, so Scout left it as it is.");
        SqliteConnection.ClearAllPools();
        Scalar("SELECT COUNT(*) FROM notes").ShouldBe(1, "the foreign table and its row are still in the file");
        Scalar("SELECT COUNT(*) FROM meetings").ShouldBe(1, "the older build's meeting is untouched too");
    }

    [Fact]
    public async Task The_Newest_First_List_Runs_On_Its_Index_Without_A_Table_Scan_Or_A_Sort()
    {
        await new SqliteMeetingStore(DbPath).ListAsync(1, CancellationToken.None);
        await using var db = new MeetingsDbContext(new DbContextOptionsBuilder<MeetingsDbContext>().UseSqlite($"Data Source={DbPath}").Options);

        var plan = await QueryPlan.OfAsync(db, SqliteMeetingStore.Newest(db, 100));

        plan.ShouldContain(step => step.Contains("IX_meetings_started_utc_ticks_number", StringComparison.Ordinal), $"the plan was: {string.Join(" | ", plan)}");
        plan.ShouldNotContain(step => step.Contains("TEMP B-TREE", StringComparison.Ordinal), "the newest-first order comes from the index");
        plan.ShouldNotContain(step => QueryPlan.IsTableScan(step), "no table is read from end to end, the counts included");
    }

    [Fact]
    public void Scout_Meetings_References_No_Network_Assembly()
    {
        var references = typeof(SqliteMeetingStore).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        references.ShouldContain("Microsoft.Data.Sqlite");
        references.ShouldNotContain(
            name => name.StartsWith("System.Net", StringComparison.Ordinal) || name.Contains("Http", StringComparison.Ordinal),
            "a kept meeting is never sent anywhere");
    }

    [Fact]
    public async Task Delete_All_Answers_How_Many_Meetings_It_Deleted_Not_Their_Lines_Or_Turns()
    {
        var store = new SqliteMeetingStore(DbPath);
        await store.KeepAsync(Meeting("First", lines: Lines(7, "line"), turns: Turns(3)), null, CancellationToken.None);
        await store.KeepAsync(Meeting("Second"), null, CancellationToken.None);

        var deleted = await store.DeleteAllAsync(CancellationToken.None);
        var again = await store.DeleteAllAsync(CancellationToken.None);

        deleted.ShouldBe(2, "two meetings were kept; their cascaded lines and turns are not counted");
        again.ShouldBe(0, "nothing is left to delete");
    }
}
