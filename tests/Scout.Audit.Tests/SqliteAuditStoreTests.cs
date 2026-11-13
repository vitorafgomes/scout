using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Scout.Audit.Storage;

namespace Scout.Audit.Tests;

public sealed class SqliteAuditStoreTests : IDisposable
{
    private readonly TempFolder _folder = new();

    private static readonly AuditBatchPlan[] ThreeBatches =
    [
        new("src/Orders", ["src/Orders/Checkout.cs"], 10),
        new("src/Billing", ["src/Billing/Invoice.cs"], 10),
        new("tests", ["tests/CheckoutTests.cs"], 10),
    ];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _folder.Dispose();
    }

    private SqliteAuditStore Store() => new(Path.Combine(_folder.Path, "data", "audit.db"), TimeProvider.System);

    private static AuditFinding Finding(string id, string title, string path) => new(id, "block", title, path, 12, null, "Why.", "Fix.", "Defect");

    [Fact]
    public async Task A_repeated_finding_is_not_reported_twice()
    {
        var store = Store();
        var job = await store.CreateJobAsync("acme", "shop", null, "abc1234def", "local:session", ThreeBatches, null, CancellationToken.None);

        var first = await store.CompleteBatchAsync(job, 1, ["src/Orders/Checkout.cs"], [Finding("swallow", "Gateway errors read as no orders", "src/Orders/Gateway.cs")], CancellationToken.None);
        var second = await store.CompleteBatchAsync(job, 2, [], [Finding("gateway", "  GATEWAY ERRORS READ AS NO ORDERS ", "SRC/orders/gateway.cs")], CancellationToken.None);

        first.Count.ShouldBe(1);
        second.ShouldBeEmpty();
        (await store.FindingsAsync(job, CancellationToken.None)).ShouldHaveSingleItem().Id.ShouldBe("swallow");
    }

    [Fact]
    public async Task Two_different_findings_the_model_gave_the_same_id_stay_two_cards()
    {
        var store = Store();
        var job = await store.CreateJobAsync("acme", "shop", null, "abc1234def", null, ThreeBatches, null, CancellationToken.None);

        await store.CompleteBatchAsync(job, 1, [], [Finding("swallow", "Gateway errors read as no orders", "src/Orders/Gateway.cs")], CancellationToken.None);
        var added = await store.CompleteBatchAsync(job, 2, [], [Finding("swallow", "Invoice errors read as paid", "src/Billing/Invoice.cs")], CancellationToken.None);

        added.ShouldHaveSingleItem().Id.ShouldBe("swallow-2");
        (await store.FindingsAsync(job, CancellationToken.None)).Select(f => f.Id).ShouldBe(["swallow", "swallow-2"]);
    }

    [Fact]
    public async Task The_audit_survives_a_restart_and_resumes_from_the_first_batch_not_done()
    {
        var job = await Store().CreateJobAsync("acme", "shop", null, "abc1234def", "local:session", ThreeBatches, null, CancellationToken.None);
        await Store().CompleteBatchAsync(job, 1, ["src/Orders/Checkout.cs"], [], CancellationToken.None);
        SqliteConnection.ClearAllPools();

        var reopened = Store();
        var running = (await reopened.RunningJobsAsync(CancellationToken.None)).ShouldHaveSingleItem();
        var batches = await reopened.BatchesAsync(job, CancellationToken.None);

        (running.Id, running.Origin, running.Commit).ShouldBe((job, "local:session", "abc1234def"));
        batches.Select(b => b.Status).ShouldBe([AuditStatus.Done, AuditStatus.Pending, AuditStatus.Pending]);
        batches.First(b => b.Status == AuditStatus.Pending).Area.ShouldBe("src/Billing");
        batches[0].FilesRead.ShouldBe(["src/Orders/Checkout.cs"]);
    }

    [Fact]
    public async Task A_batch_not_covered_keeps_its_reason()
    {
        var store = Store();
        var job = await store.CreateJobAsync("acme", "shop", null, "abc1234def", null, ThreeBatches, null, CancellationToken.None);

        await store.NotCoveredAsync(job, 2, ["src/Billing/Invoice.cs"], "the AI provider's usage limit was reached", CancellationToken.None);

        var batch = (await store.BatchesAsync(job, CancellationToken.None))[1];
        (batch.Status, batch.Failure).ShouldBe((AuditStatus.NotCovered, "the AI provider's usage limit was reached"));
    }

    [Fact]
    public async Task Stopping_the_audit_writes_the_status_of_the_running_job_only()
    {
        var store = Store();
        var job = await store.CreateJobAsync("acme", "shop", null, "abc1234def", null, ThreeBatches, null, CancellationToken.None);

        (await store.StopAsync("ACME", "Shop", null, CancellationToken.None)).ShouldBeTrue();
        (await store.StopAsync("acme", "shop", null, CancellationToken.None)).ShouldBeFalse();
        (await store.JobAsync(job, CancellationToken.None))!.Status.ShouldBe(AuditStatus.Stopped);
        (await store.RunningJobsAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_paused_audit_is_left_by_the_worker_resumes_with_the_new_chat_and_can_be_stopped()
    {
        var store = Store();
        var job = await store.CreateJobAsync("acme", "shop", null, "abc1234def", "discord:1:2", ThreeBatches, null, CancellationToken.None);

        await store.PauseAsync(job, "the AI provider's usage limit was reached", CancellationToken.None);
        (await store.RunningJobsAsync(CancellationToken.None)).ShouldBeEmpty();

        await store.ResumeAsync(job, "local:session", CancellationToken.None);
        var resumed = (await store.RunningJobsAsync(CancellationToken.None)).ShouldHaveSingleItem();
        (resumed.Id, resumed.Origin, resumed.PausedReason).ShouldBe((job, "local:session", null));

        await store.PauseAsync(job, "the AI provider's usage limit was reached", CancellationToken.None);
        (await store.StopAsync("acme", "shop", null, CancellationToken.None)).ShouldBeTrue();
        var stopped = (await store.JobAsync(job, CancellationToken.None))!;
        (stopped.Status, stopped.PausedReason).ShouldBe((AuditStatus.Stopped, null));
    }

    [Fact]
    public async Task A_database_an_older_build_wrote_without_the_migration_history_is_created_again_with_the_current_schema()
    {
        var path = Path.Combine(_folder.Path, "data", "audit.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (var old = new SqliteConnection($"Data Source={path}"))
        {
            await old.OpenAsync();
            await using var schema = old.CreateCommand();
            schema.CommandText = """
                PRAGMA journal_mode = WAL;
                CREATE TABLE audit_job (id TEXT PRIMARY KEY, owner TEXT NOT NULL, repo TEXT NOT NULL, commit_sha TEXT NOT NULL, origin TEXT,
                    started TEXT NOT NULL, finished TEXT, status TEXT NOT NULL);
                CREATE TABLE audit_batch (job_id TEXT NOT NULL REFERENCES audit_job (id) ON DELETE CASCADE, seq INTEGER NOT NULL, area TEXT NOT NULL,
                    files TEXT NOT NULL, status TEXT NOT NULL, started TEXT, finished TEXT, files_read TEXT NOT NULL DEFAULT '', failure TEXT,
                    PRIMARY KEY (job_id, seq));
                INSERT INTO audit_job VALUES ('old1', 'acme', 'shop', 'abc1234def', 'local:session', '2026-11-07T10:00:00.0000000+00:00', NULL, 'running');
                INSERT INTO audit_batch (job_id, seq, area, files, status) VALUES ('old1', 1, 'src', 'src/A.cs', 'pending');
                """;
            await schema.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();
        var store = Store();

        (await store.RunningJobsAsync(CancellationToken.None)).ShouldBeEmpty("the older build's file is deleted and created again, so its jobs are gone");
        (await store.JobAsync("old1", CancellationToken.None)).ShouldBeNull();
        var job = await store.CreateJobAsync("acme", "shop", null, "abc1234def", "local:session", ThreeBatches, null, CancellationToken.None);
        await store.PauseAsync(job, "the AI provider's usage limit was reached", CancellationToken.None);
        (await store.JobAsync(job, CancellationToken.None))!.PausedReason.ShouldBe("the AI provider's usage limit was reached");
        (await store.BatchesAsync(job, CancellationToken.None)).Select(b => b.Area).ShouldBe(["src/Orders", "src/Billing", "tests"]);
    }

    private static async Task<string> OlderBuildFileAsync(string path, string extraTable = "")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (var old = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await old.OpenAsync();
            await using var schema = old.CreateCommand();
            schema.CommandText = $"""
                CREATE TABLE audit_job (id TEXT PRIMARY KEY, owner TEXT NOT NULL, repo TEXT NOT NULL, commit_sha TEXT NOT NULL, origin TEXT,
                    started TEXT NOT NULL, finished TEXT, status TEXT NOT NULL);
                INSERT INTO audit_job VALUES ('old1', 'acme', 'shop', 'abc1234def', NULL, '2026-11-07T10:00:00.0000000+00:00', NULL, 'running');
                {extraTable}
                """;
            await schema.ExecuteNonQueryAsync();
        }

        return path;
    }

    private static void ReadOnlyFolder(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The read-only folder is made with Unix permissions.");
        }

        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
    }

    private static void WritableFolder(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The read-only folder is made with Unix permissions.");
        }

        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public async Task An_older_build_file_in_a_folder_Scout_cannot_write_to_is_a_sentence_and_stays_where_it_is()
    {
        var path = await OlderBuildFileAsync(Path.Combine(_folder.Path, "data", "audit.db"));
        var folder = Path.GetDirectoryName(path)!;
        ReadOnlyFolder(folder);
        try
        {
            var failure = await Should.ThrowAsync<AuditStoreException>(() => Store().RunningJobsAsync(CancellationToken.None));

            failure.Message.ShouldBe("Scout could not read the audit database (UnauthorizedAccessException).");
            File.Exists(path).ShouldBeTrue("a file Scout could not delete is left as it was");
        }
        finally
        {
            WritableFolder(folder);
        }
    }

    [Fact]
    public async Task An_older_build_file_holding_a_table_Scout_did_not_create_is_never_deleted_and_the_call_says_why()
    {
        var path = await OlderBuildFileAsync(Path.Combine(_folder.Path, "data", "audit.db"), "CREATE TABLE notes (body TEXT); INSERT INTO notes VALUES ('keep me');");

        var failure = await Should.ThrowAsync<AuditStoreException>(() => Store().RunningJobsAsync(CancellationToken.None));

        failure.Message.ShouldBe("Scout could not read the audit database: it holds tables Scout did not create, so Scout left it as it is.");
        SqliteConnection.ClearAllPools();
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var notes = connection.CreateCommand();
        notes.CommandText = "SELECT body FROM notes;";
        (await notes.ExecuteScalarAsync()).ShouldBe("keep me", "the foreign table and its rows are still in the file");
        await using var jobs = connection.CreateCommand();
        jobs.CommandText = "SELECT count(*) FROM audit_job;";
        Convert.ToInt64(await jobs.ExecuteScalarAsync(), CultureInfo.InvariantCulture).ShouldBe(1, "the older build's rows are untouched too");
    }

    [Fact]
    public async Task The_database_is_kept_in_wal_mode_with_the_three_tables()
    {
        var path = Path.Combine(_folder.Path, "data", "audit.db");
        await new SqliteAuditStore(path, TimeProvider.System).RunningJobsAsync(CancellationToken.None);

        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var mode = connection.CreateCommand();
        mode.CommandText = "PRAGMA journal_mode;";
        (await mode.ExecuteScalarAsync())!.ToString().ShouldBe("wal");
        await using var tables = connection.CreateCommand();
        tables.CommandText = """
            SELECT group_concat(name, ',') FROM (SELECT name FROM sqlite_master
            WHERE type = 'table' AND name NOT LIKE '\_\_EF%' ESCAPE '\' AND name NOT LIKE 'sqlite\_%' ESCAPE '\' ORDER BY name);
            """;
        (await tables.ExecuteScalarAsync())!.ToString().ShouldBe("audit_batch,audit_finding,audit_job");
        await using var history = connection.CreateCommand();
        history.CommandText = "SELECT count(*) FROM __EFMigrationsHistory;";
        Convert.ToInt64(await history.ExecuteScalarAsync(), CultureInfo.InvariantCulture).ShouldBeGreaterThan(0, "the schema comes from the migrations, so a later build can migrate it");
    }

    [Fact]
    public async Task A_database_that_cannot_be_opened_is_a_sentence_naming_the_sqlite_error_never_a_raw_exception()
    {
        var directory = Path.Combine(_folder.Path, "audit.db");
        Directory.CreateDirectory(directory);
        var store = new SqliteAuditStore(directory, TimeProvider.System);

        var failure = await Should.ThrowAsync<AuditStoreException>(() => store.RunningJobsAsync(CancellationToken.None));

        failure.Message.ShouldBe("Scout could not read the audit database (SQLite error 14).");
    }

    [Fact]
    public async Task A_database_that_refuses_a_write_is_a_sentence_naming_the_sqlite_error_and_keeps_what_it_held()
    {
        var path = Path.Combine(_folder.Path, "data", "audit.db");
        var job = await Store().CreateJobAsync("acme", "shop", null, "abc1234def", null, ThreeBatches, null, CancellationToken.None);
        SqliteConnection.ClearAllPools();
        File.SetAttributes(path, FileAttributes.ReadOnly);
        var store = Store();

        var failure = await Should.ThrowAsync<AuditStoreException>(
            () => store.CompleteBatchAsync(job, 1, ["src/Orders/Checkout.cs"], [Finding("swallow", "Gateway errors read as no orders", "src/Orders/Gateway.cs")], CancellationToken.None));

        failure.Message.ShouldBe("Scout could not save the audit database (SQLite error 8).");
        (await store.FindingsAsync(job, CancellationToken.None)).ShouldBeEmpty("the batch and its findings are one transaction, so a refused write keeps neither");
        (await store.BatchesAsync(job, CancellationToken.None)).Select(b => b.Status).ShouldBe([AuditStatus.Pending, AuditStatus.Pending, AuditStatus.Pending]);
    }

    public static TheoryData<string, string> IndexedQueries => new()
    {
        { "running jobs", "IX_audit_job_status_started" },
        { "latest job of a repository", "IX_audit_job_owner_repo_kind_pull_request_started" },
        { "latest review of a pull request", "IX_audit_job_owner_repo_kind_pull_request_started" },
        { "stop a repository's job", "IX_audit_job_owner_repo_kind_pull_request_started" },
        { "findings of a job", "IX_audit_finding_job_id_seq" },
    };

    [Theory]
    [MemberData(nameof(IndexedQueries))]
    public async Task Each_lookup_and_sort_runs_on_its_index_without_a_table_scan_or_a_sort(string query, string index)
    {
        var path = Path.Combine(_folder.Path, "data", "audit.db");
        await new SqliteAuditStore(path, TimeProvider.System).RunningJobsAsync(CancellationToken.None);
        await using var db = new AuditDbContext(new DbContextOptionsBuilder<AuditDbContext>().UseSqlite($"Data Source={path}").Options);
        IQueryable shape = query switch
        {
            "running jobs" => SqliteAuditStore.RunningJobs(db),
            "latest job of a repository" => SqliteAuditStore.LatestJobs(db, "acme", "shop", null),
            "latest review of a pull request" => SqliteAuditStore.LatestJobs(db, "acme", "shop", 7),
            "stop a repository's job" => SqliteAuditStore.Stoppable(db, "acme", "shop", null),
            "findings of a job" => SqliteAuditStore.Findings(db, "job1"),
            _ => throw new ArgumentOutOfRangeException(nameof(query)),
        };

        var plan = await QueryPlan.OfAsync(db, shape);

        plan.ShouldContain(step => step.Contains(index, StringComparison.Ordinal), $"the {query} uses {index}; the plan was: {string.Join(" | ", plan)}");
        plan.ShouldNotContain(step => step.Contains("TEMP B-TREE", StringComparison.Ordinal), $"the {query} needs no sort of its own");
        plan.ShouldNotContain(step => QueryPlan.IsTableScan(step), $"the {query} reads no table from end to end");
    }

    [Fact]
    public async Task The_latest_audit_of_a_repository_is_never_its_review_and_a_review_is_found_by_its_pull_request_only()
    {
        var store = Store();
        var audit = await store.CreateJobAsync("acme", "shop", null, "abc1234def", "local:session", ThreeBatches, null, CancellationToken.None);
        var review7 = await store.CreateJobAsync("acme", "shop", 7, "def5678abc", "local:session", ThreeBatches, null, CancellationToken.None);
        await store.CreateJobAsync("acme", "shop", 8, "0123456fed", "local:session", ThreeBatches, null, CancellationToken.None);

        var latestAudit = (await store.LatestJobAsync("ACME", "Shop", null, CancellationToken.None)).ShouldNotBeNull();
        var latestReview = (await store.LatestJobAsync("acme", "shop", 7, CancellationToken.None)).ShouldNotBeNull();

        (latestAudit.Id, latestAudit.Kind, latestAudit.PullRequest).ShouldBe((audit, AuditKind.Audit, (int?)null), "the newer reviews are not the repository's audit");
        (latestReview.Id, latestReview.Kind, latestReview.PullRequest, latestReview.Commit).ShouldBe(
            (review7, AuditKind.Review, (int?)7, "def5678abc"), "the newer review of pull request 8 is not pull request 7's");
        (await store.LatestJobAsync("acme", "shop", 9, CancellationToken.None)).ShouldBeNull("no review of pull request 9 was started");
    }

    [Fact]
    public async Task Stopping_the_audit_leaves_a_review_of_the_same_repository_running()
    {
        var store = Store();
        var audit = await store.CreateJobAsync("acme", "shop", null, "abc1234def", "local:session", ThreeBatches, null, CancellationToken.None);
        var review = await store.CreateJobAsync("acme", "shop", 7, "def5678abc", "local:session", ThreeBatches, null, CancellationToken.None);

        (await store.StopAsync("acme", "shop", null, CancellationToken.None)).ShouldBeTrue();

        (await store.JobAsync(audit, CancellationToken.None))!.Status.ShouldBe(AuditStatus.Stopped);
        (await store.RunningJobsAsync(CancellationToken.None)).ShouldHaveSingleItem().Id.ShouldBe(review, "the review of pull request 7 is still running");
        (await store.StopAsync("acme", "shop", null, CancellationToken.None)).ShouldBeFalse("the running review is not the repository's audit");
    }

    private static async Task<string> FileAtTheFirstMigrationAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (var db = new AuditDbContext(new DbContextOptionsBuilder<AuditDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options))
        {
            await db.Database.MigrateAsync("20261108150323_Initial");
        }

        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var rows = connection.CreateCommand();
        rows.CommandText = """
            INSERT INTO audit_job (id, owner, repo, commit_sha, origin, started, status)
                VALUES ('old1', 'acme', 'shop', 'abc1234def', 'local:session', '2026-11-07 10:00:00', 'running');
            INSERT INTO audit_batch (job_id, seq, area, files, status, files_read, partial) VALUES ('old1', 1, 'src', '["src/A.cs"]', 'pending', '[]', '[]');
            """;
        await rows.ExecuteNonQueryAsync();
        return path;
    }

    [Fact]
    public async Task A_file_migrated_before_the_kind_existed_reads_its_jobs_as_audits()
    {
        await FileAtTheFirstMigrationAsync(Path.Combine(_folder.Path, "data", "audit.db"));
        var store = Store();

        var latest = (await store.LatestJobAsync("acme", "shop", null, CancellationToken.None)).ShouldNotBeNull("the job kept before the kind existed is the repository's audit");

        (latest.Id, latest.Kind, latest.PullRequest, latest.Status).ShouldBe(("old1", AuditKind.Audit, (int?)null, AuditStatus.Running));
        (await store.BatchesAsync("old1", CancellationToken.None)).ShouldHaveSingleItem().Files.ShouldBe(["src/A.cs"]);
        (await store.LatestJobAsync("acme", "shop", 7, CancellationToken.None)).ShouldBeNull("a job kept before the kind existed is never a review");
    }

    [Fact]
    public async Task A_migration_the_file_refuses_is_a_sentence_and_the_rows_stay()
    {
        var path = await FileAtTheFirstMigrationAsync(Path.Combine(_folder.Path, "data", "audit.db"));
        File.SetAttributes(path, FileAttributes.ReadOnly);

        var failure = await Should.ThrowAsync<AuditStoreException>(() => Store().RunningJobsAsync(CancellationToken.None));

        failure.Message.ShouldBe("Scout could not read the audit database (SQLite error 8).");
        SqliteConnection.ClearAllPools();
        await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var jobs = connection.CreateCommand();
        jobs.CommandText = "SELECT group_concat(id) FROM audit_job;";
        (await jobs.ExecuteScalarAsync()).ShouldBe("old1", "the job kept before the refused migration is still in the file");
        await using var kind = connection.CreateCommand();
        kind.CommandText = "SELECT count(*) FROM pragma_table_info('audit_job') WHERE name = 'kind';";
        Convert.ToInt64(await kind.ExecuteScalarAsync(), CultureInfo.InvariantCulture).ShouldBe(0, "the refused migration added no column");
        await using var history = connection.CreateCommand();
        history.CommandText = "SELECT group_concat(MigrationId) FROM __EFMigrationsHistory;";
        (await history.ExecuteScalarAsync()).ShouldBe("20261108150323_Initial", "the file still holds only the migration it had");
    }

    [Fact]
    public async Task A_planned_batch_with_a_failure_is_kept_not_covered_with_its_reason_and_the_others_pending()
    {
        var store = Store();

        var job = await store.CreateJobAsync(
            "acme", "shop", 7, "def5678abc", "local:session", [ThreeBatches[0], new("no diff", ["assets/logo.png"], 0, Failure: "GitHub returned no diff")], null, CancellationToken.None);

        (await store.BatchesAsync(job, CancellationToken.None)).Select(b => (b.Area, b.Status, b.Failure)).ShouldBe(
        [
            ("src/Orders", AuditStatus.Pending, (string?)null),
            ("no diff", AuditStatus.NotCovered, "GitHub returned no diff"),
        ]);
        var (next, count) = await store.NextBatchAsync(job, CancellationToken.None);
        (next?.Area, count).ShouldBe(("src/Orders", 2), "the batch kept not covered is never the next one to run");
    }
}
