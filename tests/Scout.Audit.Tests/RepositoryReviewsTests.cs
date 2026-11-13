using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Scout.Audit.Storage;

namespace Scout.Audit.Tests;

public sealed class RepositoryReviewsTests : IDisposable
{
    private const string Head = "abc1234def5678";
    private const string Earlier = "0123456789abcdef";
    private const string TakenAtHead = "Took a snapshot of acme/shop at abc1234def5678 (abc1234): 3 files, 1 KB.";

    private static readonly ReviewFile[] OneFile = [new("src/A.cs", "@@ -1 +1 @@\n-a\n+b")];

    private readonly TempFolder _folder = new();

    public RepositoryReviewsTests()
    {
        Options = new AuditOptions { DbPath = Path.Combine(_folder.Path, "audit.db") };
    }

    private AuditOptions Options { get; }

    private FakeSnapshots Snapshots { get; } = new();

    private FakeEnvironment Environment { get; } = new();

    private static readonly ReviewCheckCounts Green = new(5, 0, 0);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _folder.Dispose();
    }

    private void Snapshot(string sha) =>
        Snapshots.Taken.Add(new AuditSnapshot("acme", "shop", sha, Path.Combine(_folder.Path, "snapshots", sha), DateTimeOffset.Parse("2026-11-13T10:00:00+00:00", CultureInfo.InvariantCulture)));

    private Kernel TakingTheSnapshot(List<string> references, string sha = Head, string answer = TakenAtHead) => Kernels.Audit(arguments =>
    {
        references.Add((string)arguments["reference"]!);
        Snapshot(sha);
        return answer;
    });

    private RepositoryAudits Reviews(Kernel kernel, SqliteAuditStore store) =>
        new(store, new AuditSignal(), Options, Snapshots, Environment, () => kernel, () => "local:session", NullLogger<RepositoryAudits>.Instance);

    private SqliteAuditStore Store() => new(Options.DbPath, TimeProvider.System);

    [Fact]
    public async Task Starting_a_review_pins_the_head_commit_snapshots_at_it_and_answers_review_started_with_files_and_batches()
    {
        var references = new List<string>();
        var store = Store();
        ReviewFile[] files = [new("README.md", "@@ -1 +1 @@\n-Shop\n+Acme shop"), new("src/Orders/Checkout.cs", "@@ -3 +3 @@\n-return;\n+throw;")];

        var answer = (await Reviews(TakingTheSnapshot(references), store).StartAsync("acme", "shop", 12, Head, files, 1, Green, CancellationToken.None)).Sentence;

        answer.ShouldBe("review started: 2 files, 2 batches, acme/shop#12 at abc1234.");
        references.ShouldBe([Head], "the snapshot is taken at the pull request's head commit");
        Snapshots.Asked.ShouldBe(["acme/shop@abc1234"]);
        var job = (await store.LatestJobAsync("acme", "shop", 12, CancellationToken.None)).ShouldNotBeNull();
        (job.Kind, job.PullRequest, job.Commit, job.Origin, job.Status).ShouldBe((AuditKind.Review, (int?)12, Head, "local:session", AuditStatus.Running));
        (await store.BatchesAsync(job.Id, CancellationToken.None)).Select(b => b.Area).ShouldBe([AuditPlanner.ProjectFilesArea, "src"]);
        (await store.LatestJobAsync("acme", "shop", null, CancellationToken.None)).ShouldBeNull("a review is not an audit of the repository");
    }

    [Fact]
    public async Task A_review_is_planned_by_patch_length_and_a_file_with_no_diff_is_kept_as_not_read()
    {
        Options.MaxBatchCharacters = 100;
        var store = Store();
        ReviewFile[] files =
        [
            new("src/Orders/Checkout.cs", new string('+', 60)),
            new("assets/logo.png", null),
            new("src/Orders/Refund.cs", new string('-', 60)),
        ];

        var answer = (await Reviews(TakingTheSnapshot([]), store).StartAsync("acme", "shop", 12, Head, files, 2, Green, CancellationToken.None)).Sentence;

        answer.ShouldBe("review started: 3 files, 3 batches, acme/shop#12 at abc1234. Files GitHub returned no diff for, counted as not read: 1.");
        var job = (await store.LatestJobAsync("acme", "shop", 12, CancellationToken.None)).ShouldNotBeNull();
        (await store.BatchesAsync(job.Id, CancellationToken.None)).Select(b => (b.Area, string.Join(", ", b.Files), b.Status, b.Failure)).ShouldBe(
        [
            ("src/Orders (1/2)", "src/Orders/Checkout.cs", AuditStatus.Pending, (string?)null),
            ("src/Orders (2/2)", "src/Orders/Refund.cs", AuditStatus.Pending, null),
            (RepositoryAudits.NoDiffArea, "assets/logo.png", AuditStatus.NotCovered, "GitHub returned no diff"),
        ]);
    }

    [Fact]
    public async Task A_refused_snapshot_starts_no_review_and_its_answer_is_returned_as_it_is()
    {
        const string refusal = "The owner has not allowed github to write; allow it on the Features screen of Scout Desktop.";
        Snapshot(Head);
        var store = Store();

        var answer = (await Reviews(Kernels.Audit(_ => refusal), store).StartAsync("acme", "shop", 12, Head, OneFile, 1, Green, CancellationToken.None)).Sentence;

        answer.ShouldBe(refusal);
        Snapshots.Asked.ShouldBeEmpty("a refused snapshot never falls back to one taken earlier at the same commit");
        (await store.RunningJobsAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_snapshot_call_that_fails_starts_no_review_and_is_a_sentence()
    {
        var store = Store();

        var answer = (await Reviews(Kernels.Audit(_ => throw new HttpRequestException("reset")), store).StartAsync("acme", "shop", 12, Head, OneFile, 1, Green, CancellationToken.None)).Sentence;

        answer.ShouldBe("Scout could not take the snapshot of acme/shop (HttpRequestException).");
        (await store.RunningJobsAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_snapshot_at_another_commit_than_the_head_starts_no_review()
    {
        var store = Store();
        var kernel = TakingTheSnapshot([], "def5678abc1234", "Took a snapshot of acme/shop at abc1234def5678 (def5678): 3 files, 1 KB.");

        var answer = (await Reviews(kernel, store).StartAsync("acme", "shop", 12, Head, OneFile, 1, Green, CancellationToken.None)).Sentence;

        answer.ShouldBe("The review of acme/shop#12 did not start: the snapshot Scout took is at def5678, not at the pull request's head abc1234.");
        (await store.RunningJobsAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    private const string Started = "review started: 1 files, 1 batches, acme/shop#12 at abc1234.";
    private const string OneDone = "1 of 2 batches done, 0 not covered, 0 findings, 1 files read.";

    /// <summary>
    /// The latest review of the pull request in each state, at the head commit or an earlier one, and a second start: the
    /// answer, the status of the latest review after, whether that is still the review already kept, the snapshots taken, and
    /// the outcome and sentence of the check for another review of the same pull request and commit, none for a resume.
    /// </summary>
    public static TheoryData<string, string, string, string, bool, int, string?, string?> SecondStart => new()
    {
        { "running", Head, $"A review of acme/shop#12 at abc1234 is already running. {OneDone}", AuditStatus.Running, true, 0, PreflightOutcome.Block, $"A review of acme/shop#12 at abc1234 is already running. {OneDone}" },
        { "done", Head, AlreadyDone, AuditStatus.Done, true, 0, PreflightOutcome.Block, AlreadyDone },
        { "paused", Head, $"Resuming the review of acme/shop#12 at abc1234 from the first batch not done. {OneDone}", AuditStatus.Running, true, 0, null, null },
        { "stopped", Head, Started, AuditStatus.Running, false, 1, PreflightOutcome.Warning, "A review of acme/shop#12 at abc1234 was stopped; this start reviews it again." },
        { "done", Earlier, Started, AuditStatus.Running, false, 1, PreflightOutcome.Ok, "No other review of acme/shop#12 at abc1234." },
    };

    private const string AlreadyDone = "The review of acme/shop#12 at abc1234 is already done. 2 of 2 batches done, 0 not covered, 0 findings, 2 files read.";

    [Theory]
    [MemberData(nameof(SecondStart))]
    public async Task A_second_start_for_the_same_pull_request_and_commit_is_refused_with_its_progress(
        string state, string commit, string answer, string status, bool kept, int snapshots, string? outcome, string? sentence)
    {
        var references = new List<string>();
        var store = Store();
        var seeded = await store.CreateJobAsync("acme", "shop", 12, commit, "discord:1:2", [new("src", ["src/A.cs"], 10), new("tests", ["tests/T.cs"], 10)], null, CancellationToken.None);
        await store.CompleteBatchAsync(seeded, 1, ["src/A.cs"], [], CancellationToken.None);
        await (state switch
        {
            "paused" => store.PauseAsync(seeded, "the AI provider's usage limit was reached", CancellationToken.None),
            "stopped" => store.StopAsync("acme", "shop", 12, CancellationToken.None),
            "done" => FinishAsync(store, seeded),
            _ => Task.CompletedTask,
        });

        var start = await Reviews(TakingTheSnapshot(references), store).StartAsync("acme", "shop", 12, Head, OneFile, 1, Green, CancellationToken.None);

        start.Sentence.ShouldBe(answer);
        var latest = (await store.LatestJobAsync("acme", "shop", 12, CancellationToken.None)).ShouldNotBeNull();
        (latest.Status, latest.Id == seeded).ShouldBe((status, kept));
        references.Count.ShouldBe(snapshots);
        var duplicate = start.Preflight.Checks.Where(c => c.Name == PreflightName.Duplicate).Select(c => (c.Outcome, c.Sentence)).ToList();
        List<(string, string)> expected = outcome is null ? [] : [(outcome, sentence!)];
        duplicate.ShouldBe(expected, $"the check for another review when the latest is {state} at {commit[..7]}");
    }

    private static async Task FinishAsync(SqliteAuditStore store, string job)
    {
        await store.CompleteBatchAsync(job, 2, ["tests/T.cs"], [], CancellationToken.None);
        await store.FinishAsync(job, CancellationToken.None);
    }

    private const string Moved = $"{Started} Stopped the review at 0123456: the pull request moved from 0123456 to abc1234.";

    /// <summary>The review of the pull request kept at an earlier head in each state, and a start at the new head: the answer and the earlier review's status after.</summary>
    public static TheoryData<string, string, string> EarlierHead => new()
    {
        { "running", Moved, AuditStatus.Stopped },
        { "paused", Moved, AuditStatus.Stopped },
        { "done", Started, AuditStatus.Done },
        { "stopped", Started, AuditStatus.Stopped },
    };

    [Theory]
    [MemberData(nameof(EarlierHead))]
    public async Task A_start_at_a_new_head_stops_a_live_review_at_the_earlier_head_and_leaves_one_live_review_at_the_new_head(string state, string answer, string earlierStatus)
    {
        var store = Store();
        var earlier = await store.CreateJobAsync("acme", "shop", 12, Earlier, "discord:1:2", [new("src", ["src/A.cs"], 10), new("tests", ["tests/T.cs"], 10)], null, CancellationToken.None);
        await (state switch
        {
            "paused" => store.PauseAsync(earlier, "you paused it", CancellationToken.None),
            "stopped" => store.StopAsync("acme", "shop", 12, CancellationToken.None),
            "done" => FinishAsync(store, earlier),
            _ => Task.CompletedTask,
        });

        var told = (await Reviews(TakingTheSnapshot([]), store).StartAsync("acme", "shop", 12, Head, OneFile, 1, Green, CancellationToken.None)).Sentence;

        told.ShouldBe(answer);
        (await store.JobAsync(earlier, CancellationToken.None))!.Status.ShouldBe(earlierStatus);
        var live = (await store.RunningJobsAsync(CancellationToken.None)).ShouldHaveSingleItem("one live review of the pull request, the new one");
        (live.Commit, live.PullRequest, live.Id == earlier).ShouldBe((Head, (int?)12, false));
    }

    [Theory]
    [InlineData("every changed file has no diff", 2)]
    [InlineData("no changed file is listed", 0)]
    public async Task A_pull_request_with_nothing_to_read_starts_no_review_and_takes_no_snapshot(string input, int changed)
    {
        var references = new List<string>();
        var store = Store();
        var files = Enumerable.Range(1, changed).Select(i => new ReviewFile(string.Create(CultureInfo.InvariantCulture, $"assets/{i}.png"), null)).ToArray();

        var answer = (await Reviews(TakingTheSnapshot(references), store).StartAsync("acme", "shop", 12, Head, files, 3, Green, CancellationToken.None)).Sentence;

        answer.ShouldBe(string.Create(CultureInfo.InvariantCulture, $"No review of acme/shop#12 was started: GitHub returned no diff for any of the {changed} changed files, so there is nothing to read in batches."), input);
        references.ShouldBeEmpty(input);
        (await store.LatestJobAsync("acme", "shop", 12, CancellationToken.None)).ShouldBeNull(input);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_pull_request_number_of_zero_or_below_starts_and_reports_no_review(int number)
    {
        var references = new List<string>();
        var store = Store();
        var reviews = Reviews(TakingTheSnapshot(references), store);

        var started = (await reviews.StartAsync("acme", "shop", number, Head, OneFile, 1, Green, CancellationToken.None)).Sentence;
        var status = await reviews.StatusAsync("acme", "shop", number, CancellationToken.None);

        (started, status).ShouldBe((RepositoryAudits.InvalidPullRequest, RepositoryAudits.InvalidPullRequest));
        references.ShouldBeEmpty();
        File.Exists(Options.DbPath).ShouldBeFalse("nothing is read or written for a number that is no pull request");
    }

    [Theory]
    [InlineData("")]
    [InlineData("main")]
    [InlineData("../../etc/passwd")]
    [InlineData("abc1234def5678\n")]
    public async Task A_head_that_is_not_a_commit_sha_starts_no_review_and_takes_no_snapshot(string head)
    {
        var references = new List<string>();
        var store = Store();

        var answer = (await Reviews(TakingTheSnapshot(references), store).StartAsync("acme", "shop", 12, head, OneFile, 1, Green, CancellationToken.None)).Sentence;

        answer.ShouldBe(RepositoryAudits.InvalidHead);
        references.ShouldBeEmpty();
        (await store.LatestJobAsync("acme", "shop", 12, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task A_new_review_the_database_refuses_after_the_earlier_one_was_stopped_is_the_stores_sentence_and_a_second_start_succeeds()
    {
        var sqlite = Store();
        var earlier = await sqlite.CreateJobAsync("acme", "shop", 12, Earlier, "discord:1:2", [new("src", ["src/A.cs"], 10)], null, CancellationToken.None);
        var refused = false;
        var store = new RacingStore(sqlite)
        {
            BeforeCreate = () =>
            {
                if (!refused)
                {
                    refused = true;
                    throw new AuditStoreException("Scout could not save the audit database (SQLite error 8).", new InvalidOperationException());
                }

                return Task.CompletedTask;
            },
        };
        var reviews = new RepositoryAudits(store, new AuditSignal(), Options, Snapshots, Environment, () => TakingTheSnapshot([]), () => "local:session", NullLogger<RepositoryAudits>.Instance);

        var first = (await reviews.StartAsync("acme", "shop", 12, Head, OneFile, 1, Green, CancellationToken.None)).Sentence;
        var afterFirst = (await sqlite.JobAsync(earlier, CancellationToken.None))!.Status;
        var runningAfterFirst = await sqlite.RunningJobsAsync(CancellationToken.None);
        var second = (await reviews.StartAsync("acme", "shop", 12, Head, OneFile, 1, Green, CancellationToken.None)).Sentence;

        first.ShouldBe("Scout could not save the audit database (SQLite error 8).");
        afterFirst.ShouldBe(AuditStatus.Stopped, "the earlier review was stopped before the new one was refused");
        runningAfterFirst.ShouldBeEmpty();
        second.ShouldBe(Started);
        (await sqlite.RunningJobsAsync(CancellationToken.None)).ShouldHaveSingleItem().Commit.ShouldBe(Head);
    }

    [Fact]
    public async Task A_pull_request_with_no_diff_left_out_starts_no_review()
    {
        var references = new List<string>();
        var store = Store();

        var answer = (await Reviews(TakingTheSnapshot(references), store).StartAsync("acme", "shop", 12, Head, OneFile, 0, Green, CancellationToken.None)).Sentence;

        answer.ShouldBe("No review of acme/shop#12 was started: the pull request's answer left no diff out, so it is reviewed in this turn.");
        references.ShouldBeEmpty("no snapshot is taken for a review that fits one turn");
        (await store.RunningJobsAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task How_a_review_is_going_is_answered_from_its_rows()
    {
        var store = Store();
        await store.CreateJobAsync("acme", "shop", null, Earlier, "local:session", [new("src", ["src/A.cs"], 10)], null, CancellationToken.None);
        var job = await store.CreateJobAsync(
            "acme", "shop", 12, Head, "local:session", [new("src", ["src/A.cs"], 10), new(RepositoryAudits.NoDiffArea, ["assets/logo.png"], 0, Failure: RepositoryAudits.NoDiff)], null, CancellationToken.None);
        await store.CompleteBatchAsync(job, 1, ["src/A.cs"], [new("a", "fix", "Title", "src/A.cs", 1, null, "Why.", "Fix.", null)], CancellationToken.None);
        var reviews = Reviews(Kernels.Audit(), store);

        var running = await reviews.StatusAsync("acme", "shop", 12, CancellationToken.None);
        var none = await reviews.StatusAsync("acme", "shop", 13, CancellationToken.None);

        running.ShouldBe("The review of acme/shop#12 at abc1234 is running. 1 of 2 batches done, 1 not covered, 1 findings, 1 files read.");
        none.ShouldBe("No review of acme/shop#13 has been started.");
    }

    [Fact]
    public async Task A_database_that_cannot_be_opened_starts_no_review_and_takes_no_snapshot()
    {
        Directory.CreateDirectory(Options.DbPath);
        var references = new List<string>();

        var answer = (await Reviews(TakingTheSnapshot(references), Store()).StartAsync("acme", "shop", 12, Head, OneFile, 1, Green, CancellationToken.None)).Sentence;

        answer.ShouldBe("Scout could not read the audit database (SQLite error 14).");
        references.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("..", "shop")]
    [InlineData("acme", "../etc")]
    [InlineData("acme\n", "shop")]
    [InlineData("acme", "shop\n")]
    public async Task A_repository_name_that_could_leave_the_snapshots_folder_starts_and_reports_no_review(string owner, string repo)
    {
        var references = new List<string>();
        var reviews = Reviews(TakingTheSnapshot(references), Store());

        var started = (await reviews.StartAsync(owner, repo, 12, Head, OneFile, 1, Green, CancellationToken.None)).Sentence;
        var status = await reviews.StatusAsync(owner, repo, 12, CancellationToken.None);

        (started, status).ShouldBe((RepositoryAudits.InvalidRepository, RepositoryAudits.InvalidRepository));
        references.ShouldBeEmpty();
    }

    private const string Refused = "The owner has not allowed github to write; allow it on the Features screen of Scout Desktop.";

    private static readonly ReviewFile[] TwoFiles = [new("README.md", "@@ -1 +1 @@\n-Shop\n+Acme shop"), new("src/Orders/Checkout.cs", "@@ -3 +3 @@\n-return;\n+throw;")];

    /// <summary>The checks kept on the latest review of the pull request, as its row holds them; null when the row holds none.</summary>
    private IReadOnlyList<PreflightCheck>? Kept(int number)
    {
        using var db = new AuditDbContext(new DbContextOptionsBuilder<AuditDbContext>().UseSqlite($"Data Source={Options.DbPath}").Options);
        var json = db.Jobs.Where(j => j.PullRequest == number).OrderByDescending(j => j.Number).Select(j => j.Preflight).First();
        return json is null ? null : JsonSerializer.Deserialize<ReviewPreflight>(json, JsonSerializerOptions.Web)!.Checks;
    }

    private static (string Outcome, string Sentence) Check(ReviewStart start, string name) =>
        start.Preflight.Checks.Where(c => c.Name == name).Select(c => (c.Outcome, c.Sentence)).ShouldHaveSingleItem($"one {name} check");

    /// <summary>
    /// Changed files with a patch, files without one, and the files a batch holds at most: the size check's outcome and
    /// sentence, the most the batches can take at two tries of 10 minutes each and never past the review's 60 minutes,
    /// never an expected time: under the bound, at it, and over it with batches past the tenth.
    /// </summary>
    public static TheoryData<int, int, int, string, string> Sizes => new()
    {
        { 1, 1, 40, PreflightOutcome.Ok, "2 files in 2 batches; at most 20 minutes." },
        { 3, 0, 1, PreflightOutcome.Ok, "3 files in 3 batches; at most 60 minutes." },
        { 12, 1, 1, PreflightOutcome.Warning, "13 files in 13 batches; at most 60 minutes; 2 files will not be read." },
    };

    [Theory]
    [MemberData(nameof(Sizes))]
    public async Task Size_names_files_batches_and_the_upper_bound_never_an_expected_time(int patched, int noDiff, int perBatch, string outcome, string sentence)
    {
        Options.MaxBatchFiles = perBatch;
        var files = Enumerable.Range(1, patched).Select(i => new ReviewFile(string.Create(CultureInfo.InvariantCulture, $"f{i:00}/A.cs"), "+a"))
            .Concat(Enumerable.Range(1, noDiff).Select(i => new ReviewFile(string.Create(CultureInfo.InvariantCulture, $"assets/{i}.png"), null)))
            .ToArray();

        var start = await Reviews(TakingTheSnapshot([]), Store()).StartAsync("acme", "shop", 12, Head, files, 1, Green, CancellationToken.None);

        Check(start, PreflightName.Size).ShouldBe((outcome, sentence));
        start.Started.ShouldBeTrue("a size past the batches a review runs is a warning, and a warning starts the review");
        Kept(12).ShouldBe(start.Preflight.Checks);
    }

    [Theory]
    [InlineData(true, PreflightOutcome.Ok, "Docker runs are on.")]
    [InlineData(false, PreflightOutcome.Warning, "Docker runs are off; tests will not run.")]
    public async Task Docker_runs_off_warns(bool on, string outcome, string sentence)
    {
        Environment.RunsOn = on;

        var start = await Reviews(TakingTheSnapshot([]), Store()).StartAsync("acme", "shop", 12, Head, OneFile, 1, Green, CancellationToken.None);

        Check(start, PreflightName.DockerRuns).ShouldBe((outcome, sentence));
        start.Started.ShouldBeTrue();
        Kept(12).ShouldBe(start.Preflight.Checks);
    }

    /// <summary>The head's checks as the caller read them (passing, pending, failing), none when it could not: the head check's outcome and sentence.</summary>
    public static TheoryData<int[]?, string, string> Counts => new()
    {
        { null, PreflightOutcome.Warning, "Could not check the checks." },
        { [5, 0, 0], PreflightOutcome.Ok, "Head abc1234; 5 checks passing." },
        { [5, 2, 0], PreflightOutcome.Warning, "Head abc1234; 5 checks passing, 2 pending, 0 failing." },
        { [5, 0, 1], PreflightOutcome.Warning, "Head abc1234; 5 checks passing, 0 pending, 1 failing." },
    };

    [Theory]
    [MemberData(nameof(Counts))]
    public async Task Check_counts_are_read_from_the_argument_once(int[]? read, string outcome, string sentence)
    {
        var counts = read is null ? null : new ReviewCheckCounts(read[0], read[1], read[2]);
        var store = Store();
        var reviews = Reviews(TakingTheSnapshot([]), store);

        var start = await reviews.StartAsync("acme", "shop", 12, Head, OneFile, 1, counts, CancellationToken.None);
        var job = (await store.LatestJobAsync("acme", "shop", 12, CancellationToken.None)).ShouldNotBeNull();
        await store.PauseAsync(job.Id, "the AI provider's usage limit was reached", CancellationToken.None);
        var resumed = await reviews.StartAsync("acme", "shop", 12, Head, OneFile, 1, new ReviewCheckCounts(0, 0, 9), CancellationToken.None);

        Check(start, PreflightName.Head).ShouldBe((outcome, sentence));
        start.Started.ShouldBeTrue();
        resumed.Preflight.Checks.ShouldBeEmpty("a resume runs no check of its own");
        Kept(12).ShouldBe(start.Preflight.Checks, "the checks are kept once, at the start, and a resume with other counts leaves them as they were");
    }

    [Fact]
    public async Task A_refused_snapshot_is_the_grant_block()
    {
        var store = Store();

        var start = await Reviews(Kernels.Audit(_ => Refused), store).StartAsync("acme", "shop", 12, Head, OneFile, 1, Green, CancellationToken.None);

        (start.Started, start.Sentence).ShouldBe((false, Refused));
        Check(start, PreflightName.Grants).ShouldBe((PreflightOutcome.Block, Refused), "the gate's sentence, unchanged");
        (await store.LatestJobAsync("acme", "shop", 12, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task A_block_creates_no_row_and_returns_every_check()
    {
        var references = new List<string>();
        var store = Store();
        var running = await store.CreateJobAsync("acme", "shop", 12, Head, "discord:1:2", [new("src", ["src/A.cs"], 10)], null, CancellationToken.None);
        Environment.RunsOn = false;

        var start = await Reviews(TakingTheSnapshot(references), store).StartAsync("acme", "shop", 12, Head, TwoFiles, 1, new ReviewCheckCounts(3, 1, 0), CancellationToken.None);

        start.Started.ShouldBeFalse();
        start.Preflight.Checks.Select(c => (c.Name, c.Outcome, c.Sentence)).ShouldBe(
        [
            (PreflightName.Size, PreflightOutcome.Ok, "2 files in 2 batches; at most 40 minutes."),
            (PreflightName.DockerRuns, PreflightOutcome.Warning, "Docker runs are off; tests will not run."),
            (PreflightName.Head, PreflightOutcome.Warning, "Head abc1234; 3 checks passing, 1 pending, 0 failing."),
            (PreflightName.Duplicate, PreflightOutcome.Block, "A review of acme/shop#12 at abc1234 is already running. 0 of 1 batches done, 0 not covered, 0 findings, 0 files read."),
            (PreflightName.Grants, PreflightOutcome.Warning, "Could not check the grants: the snapshot was not asked for, because the review did not start."),
        ]);
        references.ShouldBeEmpty("a block before the snapshot asks the gate for no write");
        (await store.RunningJobsAsync(CancellationToken.None)).ShouldHaveSingleItem().Id.ShouldBe(running);
        Kept(12).ShouldBeNull("the review already running keeps the row it had");
    }

    [Fact]
    public async Task Warnings_start_the_review_and_are_kept_with_it()
    {
        var store = Store();
        Environment.RunsOn = false;

        var start = await Reviews(TakingTheSnapshot([]), store).StartAsync("acme", "shop", 12, Head, OneFile, 1, null, CancellationToken.None);

        (start.Started, start.Sentence).ShouldBe((true, Started));
        start.Preflight.Checks.Select(c => c.Outcome).ShouldBe([PreflightOutcome.Ok, PreflightOutcome.Warning, PreflightOutcome.Warning, PreflightOutcome.Ok, PreflightOutcome.Ok]);
        Kept(12).ShouldBe(start.Preflight.Checks);
    }

    [Fact]
    public async Task A_check_that_throws_is_could_not_check_and_the_review_still_starts()
    {
        var store = Store();
        Environment.Failure = new InvalidOperationException("the switch could not be read");

        var start = await Reviews(TakingTheSnapshot([]), store).StartAsync("acme", "shop", 12, Head, OneFile, 1, Green, CancellationToken.None);

        Check(start, PreflightName.DockerRuns).ShouldBe((PreflightOutcome.Warning, "Could not check whether Docker runs are on."));
        (start.Started, start.Sentence).ShouldBe((true, Started));
        Kept(12).ShouldBe(start.Preflight.Checks);
    }

    [Fact]
    public async Task A_start_the_database_refuses_starts_no_review_and_answers_the_stores_sentence_with_every_check()
    {
        var sqlite = Store();
        var store = new RacingStore(sqlite)
        {
            BeforeCreate = () => throw new AuditStoreException("Scout could not save the audit database (SQLite error 8).", new InvalidOperationException()),
        };
        var reviews = new RepositoryAudits(store, new AuditSignal(), Options, Snapshots, Environment, () => TakingTheSnapshot([]), () => "local:session", NullLogger<RepositoryAudits>.Instance);

        var start = await reviews.StartAsync("acme", "shop", 12, Head, OneFile, 1, Green, CancellationToken.None);

        (start.Started, start.Sentence).ShouldBe((false, "Scout could not save the audit database (SQLite error 8)."));
        start.Preflight.Checks.Select(c => c.Name).ShouldBe([PreflightName.Size, PreflightName.DockerRuns, PreflightName.Head, PreflightName.Duplicate, PreflightName.Grants]);
        (await sqlite.LatestJobAsync("acme", "shop", 12, CancellationToken.None)).ShouldBeNull("no review is kept when its save is refused");
    }
}
