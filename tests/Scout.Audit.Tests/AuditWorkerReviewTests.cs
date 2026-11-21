using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Llm.Abstractions;
using Scout.Review;
using Scout.Skills;

namespace Scout.Audit.Tests;

public sealed class AuditWorkerReviewTests : IDisposable
{
    private const string Head = "abc1234def";

    private readonly TempFolder _folder = new();

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-11-13T10:00:00+00:00", CultureInfo.InvariantCulture));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _folder.Dispose();
    }

    private SqliteAuditStore Store() => new(Path.Combine(_folder.Path, "audit.db"), _time);

    private static AuditBatchPlan[] Batches(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new AuditBatchPlan($"src/M{i}", [$"src/M{i}/F.cs"], 10))];

    private static ReviewPullRequest At(string head, int count, params int[] withoutPatch) =>
        new(head, [.. Enumerable.Range(1, count).Select(i => new ReviewFile($"src/M{i}/F.cs", withoutPatch.Contains(i) ? null : $"@@ -1 +1 @@\n+line {i}"))]);

    private static async Task<AuditJob> ReviewAsync(SqliteAuditStore store, int batches, ReviewPreflight? preflight = null, ReviewInputs? inputs = null) =>
        (await store.JobAsync(await store.CreateJobAsync("acme", "shop", 12, Head, "local:session", Batches(batches), preflight, CancellationToken.None, inputs), CancellationToken.None))!;

    private static AuditWorker Worker(
        IAuditJobStore store, ScriptedChat chat, RecordingNotifier notifier, IReviewPullRequests pullRequests, Kernel? kernel = null, ISkillCatalog? skills = null,
        ILogger<AuditWorker>? logger = null, int parallel = 1, IAuditSnapshots? snapshots = null, TimeSpan? contestTime = null, string? recordFolder = null,
        TimeSpan? contestGroupTime = null) =>
        new(store, new AuditBatchRunner(kernel ?? Kernels.Audit(), chat, skills, TimeSpan.FromMinutes(10), NullLogger<AuditBatchRunner>.Instance, recordFolder, store, "Test"),
            notifier, new AuditSignal(), pullRequests, logger ?? NullLogger<AuditWorker>.Instance,
            Microsoft.Extensions.Options.Options.Create(new AuditOptions { ReviewParallelBatches = parallel }), snapshots)
        {
            ContestTime = contestTime ?? Contestation.MaxTime,
            ContestGroupTime = contestGroupTime,
        };

    private static string Block(params (string Id, string Severity, string Path)[] findings) =>
        "```scout-findings\n" + JsonSerializer.Serialize(new
        {
            repository = "acme/shop",
            number = 12,
            commit = "abc1234",
            verdict = "needs rework",
            findings = findings.Select(f => new { id = f.Id, severity = f.Severity, title = $"Title {f.Id}", path = f.Path, line = 1, why = "Why.", fix = "Fix.", evidence = Kernels.EvidenceFor(f.Path) }),
        }) + "\n```";

    private static int BatchOf(ChatHistory history) => BatchOf(history[1].Content!);

    private static int BatchOf(string request) => int.Parse(request.Split("(batch ")[1].Split(' ')[0], CultureInfo.InvariantCulture);

    private static JsonElement Card(string post)
    {
        var start = post.IndexOf(FindingsBlock.Fence, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"a card, not: {post}");
        using var json = JsonDocument.Parse(post[(post.IndexOf('\n', start) + 1)..post.LastIndexOf("```", StringComparison.Ordinal)]);
        return json.RootElement.Clone();
    }

    private static string[] Notes(JsonElement card) => [.. AllNotes(card).Where(n => !n.StartsWith("Scout asked the model ", StringComparison.Ordinal))];

    private static string[] AllNotes(JsonElement card) =>
        card.TryGetProperty("notes", out var notes) ? [.. notes.EnumerateArray().Select(n => n.GetString()!)] : [];

    [Fact]
    public async Task A_moved_head_runs_no_further_batch_and_the_card_names_both_commits()
    {
        var store = Store();
        var job = await ReviewAsync(store, 3);
        var chat = new ScriptedChat(Kernels.PresentingAnEmptyReview);
        var notifier = new RecordingNotifier();
        var pullRequests = new FakePullRequests(() => At(Head, 3), () => At("def5678abc", 3));

        await Worker(store, chat, notifier, pullRequests).RunReviewAsync(job, CancellationToken.None);

        chat.Turns.Count.ShouldBe(1, "the head moved before batch 2, so no turn after the first");
        (await store.BatchesAsync(job.Id, CancellationToken.None)).Select(b => (b.Status, b.Failure)).ShouldBe(
        [
            (AuditStatus.Done, (string?)null),
            (AuditStatus.NotCovered, "the pull request moved from abc1234 to def5678"),
            (AuditStatus.NotCovered, "the pull request moved from abc1234 to def5678"),
        ]);
        var card = Card(notifier.Posts.Last().Text);
        Notes(card).ShouldBe(["2 files not read: the pull request moved from abc1234 to def5678."]);
        card.GetProperty("verdict").GetString().ShouldBe(AuditWorker.NoBlockingInFilesRead);
        (await store.JobAsync(job.Id, CancellationToken.None))!.Status.ShouldBe(AuditStatus.Done);
    }

    [Fact]
    public async Task A_first_read_at_another_head_runs_no_turn_and_reads_no_head_again()
    {
        var store = Store();
        var job = await ReviewAsync(store, 2);
        var chat = new ScriptedChat(Kernels.PresentingAnEmptyReview);
        var pullRequests = new FakePullRequests(() => At("def5678abc", 2));

        await Worker(store, chat, new RecordingNotifier(), pullRequests).RunReviewAsync(job, CancellationToken.None);

        chat.Turns.Count.ShouldBe(0, "the pull request moved before the first batch, so no batch is reviewed against another commit's patches");
        pullRequests.Heads.ShouldBe(0, "the first read already names the new head, so the head is not read again");
        (await store.BatchesAsync(job.Id, CancellationToken.None)).Select(b => (b.Status, b.Failure)).ShouldBe(
        [
            (AuditStatus.NotCovered, "the pull request moved from abc1234 to def5678"),
            (AuditStatus.NotCovered, "the pull request moved from abc1234 to def5678"),
        ]);
    }

    [Fact]
    public async Task The_changed_files_are_read_once_and_only_the_head_before_each_batch()
    {
        var store = Store();
        var job = await ReviewAsync(store, 3);
        var pullRequests = new FakePullRequests(() => At(Head, 3));

        await Worker(store, new ScriptedChat(Kernels.PresentingAnEmptyReview), new RecordingNotifier(), pullRequests).RunReviewAsync(job, CancellationToken.None);

        (pullRequests.Reads, pullRequests.Heads).ShouldBe((1, 2), "the changed files are read once with the head, and only the head before each of the 2 later batches");
        (await store.JobAsync(job.Id, CancellationToken.None))!.Status.ShouldBe(AuditStatus.Done);
    }

    [Fact]
    public async Task A_batch_is_handed_only_its_own_files_patches()
    {
        var store = Store();
        var job = await ReviewAsync(store, 3);
        var files = new WalkCountingFiles(At(Head, 3).Files);
        var walksAtTurns = new List<int>();
        var chat = new ScriptedChat((history, kernel, turn) =>
        {
            walksAtTurns.Add(files.Walks);
            return Kernels.PresentingAnEmptyReview(history, kernel, turn);
        });

        await Worker(store, chat, new RecordingNotifier(), new FakePullRequests(() => new ReviewPullRequest(Head, files))).RunReviewAsync(job, CancellationToken.None);

        walksAtTurns.ShouldBe([1, 2, 3], "the pull request's whole list of patches is walked once per batch, to pick that batch's own, and never by the batch's turn");
        (await store.BatchesAsync(job.Id, CancellationToken.None)).ShouldAllBe(b => b.Status == AuditStatus.Done);
    }

    [Fact]
    public async Task A_head_read_that_fails_before_a_later_batch_pauses_with_the_reason_and_runs_no_turn_for_it()
    {
        var store = Store();
        var job = await ReviewAsync(store, 2);
        var chat = new ScriptedChat(Kernels.PresentingAnEmptyReview);
        var pullRequests = new FakePullRequests(() => At(Head, 2), () => throw new HttpRequestException("reset"));

        await Worker(store, chat, new RecordingNotifier(), pullRequests).RunReviewAsync(job, CancellationToken.None);

        chat.Turns.Count.ShouldBe(1, "the head read that failed before batch 2 runs no turn for it");
        var paused = (await store.JobAsync(job.Id, CancellationToken.None))!;
        (paused.Status, paused.PausedReason).ShouldBe((AuditStatus.Paused, AuditWorker.GitHubUnreadable), "a head read that fails pauses the review with the reason");
    }

    [Fact]
    public async Task A_review_stopped_while_the_head_is_read_runs_no_turn()
    {
        var store = Store();
        var job = await ReviewAsync(store, 2);
        var chat = new ScriptedChat(Kernels.PresentingAnEmptyReview);
        var pullRequests = new FakePullRequests(() => At(Head, 2))
        {
            OnHead = () => store.StopAsync("acme", "shop", 12, CancellationToken.None),
        };

        await Worker(store, chat, new RecordingNotifier(), pullRequests).RunReviewAsync(job, CancellationToken.None);

        chat.Turns.Count.ShouldBe(1, "a review stopped while its head was read before batch 2 runs no turn for it");
        (await store.JobAsync(job.Id, CancellationToken.None))!.Status.ShouldBe(AuditStatus.Stopped);
    }

    /// <summary>A pull request's list of changed files that counts how many times it is walked from the start.</summary>
    private sealed class WalkCountingFiles(IReadOnlyList<ReviewFile> files) : IReadOnlyList<ReviewFile>
    {
        public int Walks { get; private set; }

        public int Count => files.Count;

        public ReviewFile this[int index] => files[index];

        public IEnumerator<ReviewFile> GetEnumerator()
        {
            Walks++;
            return files.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public async Task At_60_minutes_a_review_pauses_and_asks_with_nothing_marked_not_read()
    {
        var store = Store();
        var job = await ReviewAsync(store, 4);
        var chat = new ScriptedChat((history, kernel, turn) =>
        {
            _time.Advance(TimeSpan.FromMinutes(30));
            return Kernels.PresentingAnEmptyReview(history, kernel, turn);
        });
        var notifier = new RecordingNotifier();

        await Worker(store, chat, notifier, new FakePullRequests(() => At(Head, 4))).RunReviewAsync(job, CancellationToken.None);

        chat.Turns.Count.ShouldBe(2, "at 60 minutes of running time no further batch runs");
        var paused = (await store.JobAsync(job.Id, CancellationToken.None))!;
        (paused.Status, paused.PausedReason).ShouldBe((AuditStatus.Paused, AuditWorker.RanForTheBound), "at 60 minutes the review pauses and asks");
        (await store.BatchesAsync(job.Id, CancellationToken.None)).Select(b => b.Status).ShouldBe(
            [AuditStatus.Done, AuditStatus.Done, AuditStatus.Pending, AuditStatus.Pending], "nothing is marked not read at the bound");
        notifier.Posts.Last().Text.ShouldBe(
            "The review of acme/shop#12 paused: it ran for 60 minutes with 2 batches left. Ask Scout to continue the review of acme/shop#12 to give it 60 more minutes.");
    }

    [Fact]
    public async Task A_review_continued_after_the_60_minutes_runs_60_more()
    {
        var store = Store();
        var job = await ReviewAsync(store, 5);
        var chat = new ScriptedChat((history, kernel, turn) =>
        {
            _time.Advance(TimeSpan.FromMinutes(30));
            return Kernels.PresentingAnEmptyReview(history, kernel, turn);
        });
        var notifier = new RecordingNotifier();
        var worker = Worker(store, chat, notifier, new FakePullRequests(() => At(Head, 5)));
        await worker.RunReviewAsync(job, CancellationToken.None);
        var reviews = new RepositoryAudits(store, new AuditSignal(), new AuditOptions(), new FakeSnapshots(), new FakeEnvironment(), () => Kernels.Audit(), () => null, NullLogger<RepositoryAudits>.Instance);

        (await reviews.ControlAsync("acme", "shop", 12, RepositoryAudits.Continue, CancellationToken.None)).Applied.ShouldBeTrue();
        await worker.RunReviewAsync((await store.JobAsync(job.Id, CancellationToken.None))!, CancellationToken.None);

        chat.Turns.Count.ShouldBe(4, "a continue gives the review 60 more minutes: two more batches of 30 minutes run");
        (await store.JobAsync(job.Id, CancellationToken.None))!.PausedReason.ShouldBe(AuditWorker.RanForTheBound, "after its next 60 minutes it pauses and asks again");
    }

    [Fact]
    public async Task Time_paused_does_not_count_toward_the_60_minutes()
    {
        var store = Store();
        var job = await ReviewAsync(store, 3);
        var limited = true;
        var chat = new ScriptedChat((history, kernel, turn) =>
        {
            if (limited && BatchOf(history) == 2)
            {
                throw new LlmQuotaExceededException("usage_limit_reached");
            }

            _time.Advance(TimeSpan.FromMinutes(25));
            return Kernels.PresentingAnEmptyReview(history, kernel, turn);
        });
        using var worker = Worker(store, chat, new RecordingNotifier(), new FakePullRequests(() => At(Head, 3)));
        await worker.RunReviewAsync(job, CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(2));
        limited = false;
        await store.ResumeAsync(job.Id, null, CancellationToken.None);

        await worker.RunReviewAsync(job, CancellationToken.None);

        (await store.BatchesAsync(job.Id, CancellationToken.None)).Select(b => b.Status).ShouldBe(
            [AuditStatus.Done, AuditStatus.Done, AuditStatus.Done], "a review paused for two hours runs its remaining batches: only the time it runs counts");
        (await store.JobAsync(job.Id, CancellationToken.None))!.Status.ShouldBe(AuditStatus.Done);
    }

    [Fact]
    public async Task Each_batch_posts_one_progress_line_and_no_findings_block()
    {
        var store = Store();
        var job = await ReviewAsync(store, 3);
        var chat = new ScriptedChat((history, kernel, _) =>
            Kernels.PresentReviewAsync(kernel, Block(($"f{BatchOf(history)}", "fix", $"src/M{BatchOf(history)}/F.cs"))));
        var notifier = new RecordingNotifier();

        await Worker(store, chat, notifier, new FakePullRequests(() => At(Head, 3))).RunReviewAsync(job, CancellationToken.None);

        var posts = notifier.Posts.Select(p => p.Text).ToList();
        posts.Count.ShouldBe(5, "one progress line per batch, one while the second reading runs and one card at the end");
        posts[3].ShouldBe($"Review of acme/shop#12: 3 of 3 batches done, 3 of 3 files read, 3 findings so far.\n{AuditWorker.SecondReadingLine(3)}", "the second reading's line comes on its own line");
        posts.Take(3).ShouldBe(
        [
            "Review of acme/shop#12: 1 of 3 batches done, 1 of 3 files read, 1 finding so far, reading src/M2.",
            "Review of acme/shop#12: 2 of 3 batches done, 2 of 3 files read, 2 findings so far, reading src/M3.",
            "Review of acme/shop#12: 3 of 3 batches done, 3 of 3 files read, 3 findings so far.",
        ], "each batch posts one progress line to the desktop with the batches done, the files read, the items so far and the area being read");
        posts.Count(p => p.Contains(FindingsBlock.Fence, StringComparison.Ordinal)).ShouldBe(1, "the findings come in the one card at the end");
        Card(posts[^1]).GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("id").GetString()).ShouldBe(["f1", "f2", "f3"]);
    }

    [Theory]
    [InlineData("block", AuditWorker.ReadyAfterBlocking, ReviewOffers.RequestChanges)]
    [InlineData("fix", AuditWorker.ReadyAfterBlocking, ReviewOffers.RequestChanges)]
    [InlineData("nit", AuditWorker.NoBlockingInFilesRead, ReviewOffers.Comment)]
    [InlineData("", AuditWorker.NoBlockingInFilesRead, ReviewOffers.Comment)]
    public async Task The_review_ends_with_one_block_with_the_verdict_from_severities_and_read_N_of_M(string severity, string verdict, string offered)
    {
        var store = Store();
        var job = await ReviewAsync(store, 2);
        var chat = new ScriptedChat((history, kernel, _) => BatchOf(history) == 1 && severity.Length > 0
            ? Kernels.PresentReviewAsync(kernel, Block(("rate", severity, "src/M1/F.cs")))
            : Kernels.PresentReviewAsync(kernel, Block()));
        var notifier = new RecordingNotifier();

        await Worker(store, chat, notifier, new FakePullRequests(() => At(Head, 2))).RunReviewAsync(job, CancellationToken.None);

        var post = notifier.Posts.Last().Text;
        post.ShouldStartWith("Review of acme/shop#12 at abc1234:\n\n```scout-findings\n");
        var card = Card(post);
        card.TryGetProperty("kind", out _).ShouldBeFalse("a review's card is never an audit's");
        (card.GetProperty("repository").GetString(), card.GetProperty("number").GetInt32(), card.GetProperty("commit").GetString()).ShouldBe(("acme/shop", 12, Head));
        card.GetProperty("verdict").GetString().ShouldBe(verdict, "the verdict comes from the kept severities, never from the model's needs rework");
        card.GetProperty("offer").GetProperty("verdict").GetString().ShouldBe(offered, "V1 V2 V5 the card carries the offer the one decision gives, and its verdict sentence agrees with it");
        card.GetProperty("offer").GetProperty("commentInstead").GetBoolean().ShouldBe(offered == ReviewOffers.RequestChanges, "sending as a comment instead only beside request changes");
        card.GetProperty("read").GetString().ShouldBe("2 of 2 files read", "Scout's code computes the read N of M line");
        (card.GetProperty("coverage").GetProperty("read").GetInt32(), card.GetProperty("coverage").GetProperty("of").GetInt32()).ShouldBe((2, 2));
        card.GetProperty("findings").GetArrayLength().ShouldBe(severity.Length > 0 ? 1 : 0);
    }

    [Theory]
    [InlineData(false, true, false, AuditWorker.NoBlockingInFilesRead)]
    [InlineData(false, true, true, AuditWorker.NoBlockingInFilesRead)]
    [InlineData(false, false, false, AuditWorker.NoBlockingInFilesRead)]
    [InlineData(false, false, true, AuditWorker.NoBlockingInFilesRead)]
    [InlineData(true, true, false, AuditWorker.ReadyAfterBlocking)]
    [InlineData(true, false, false, AuditWorker.ReadyAfterBlocking)]
    [InlineData(true, false, true, AuditWorker.ReadyAfterBlocking)]
    public async Task Tests_not_run_and_an_incomplete_reading_never_change_the_verdict(bool blocking, bool readAll, bool testsKept, string verdict)
    {
        var store = Store();
        var job = await ReviewAsync(store, 2);
        var chat = new ScriptedChat((history, kernel, _) => Kernels.PresentReviewAsync(kernel, blocking && BatchOf(history) == 1 ? Block(("rate", "block", "src/M1/F.cs")) : Block()));
        var kernel = Kernels.Audit(presentReview: arguments =>
        {
            var answer = Kernels.ReviewCard(arguments);
            if (testsKept)
            {
                var block = TurnAttachments.Take()[0];
                TurnAttachments.Attach(block.Replace("\"findings\":", "\"tests\":{\"ran\":true,\"exitCode\":1},\"findings\":", StringComparison.Ordinal));
            }

            return answer;
        });
        var notifier = new RecordingNotifier();

        await Worker(store, chat, notifier, new FakePullRequests(() => readAll ? At(Head, 2) : At(Head, 2, withoutPatch: 2)), kernel).RunReviewAsync(job, CancellationToken.None);

        var card = Card(notifier.Posts.Last().Text);
        card.GetProperty("verdict").GetString().ShouldBe(verdict, "tests not run never change the verdict, and an incomplete reading is never ready to merge");
        card.GetProperty("coverage").GetProperty("read").GetInt32().ShouldBe(readAll ? 2 : 1);
        var tests = card.GetProperty("tests");
        if (testsKept)
        {
            (tests.GetProperty("ran").GetBoolean(), tests.GetProperty("exitCode").GetInt32()).ShouldBe((true, 1), "the run the plugin kept stays on the card");
        }
        else
        {
            (tests.GetProperty("ran").GetBoolean(), tests.GetProperty("reason").GetString()).ShouldBe((false, AuditWorker.TestsNotKept));
        }

        Notes(card).ShouldBe(readAll ? [] : ["1 files not read: no diff was given for it."]);
    }

    [Fact]
    public async Task A_review_of_a_pull_request_GitHub_listed_in_part_is_never_ready_to_merge_and_its_note_says_why()
    {
        var store = Store();
        var job = (await store.JobAsync(await store.CreateJobAsync(
            "acme", "shop", 12, Head, "local:session",
            [.. Batches(2), new AuditBatchPlan(RepositoryAudits.NotListedArea, ["(file 1 GitHub did not list)", "(file 2 GitHub did not list)"], 0, Failure: RepositoryAudits.NotListed)],
            null, CancellationToken.None), CancellationToken.None))!;
        var notifier = new RecordingNotifier();

        await Worker(store, new ScriptedChat(Kernels.PresentingAnEmptyReview), notifier, new FakePullRequests(() => At(Head, 2))).RunReviewAsync(job, CancellationToken.None);

        var card = Card(notifier.Posts.Last().Text);
        (card.GetProperty("verdict").GetString(), card.GetProperty("read").GetString()).ShouldBe(
            (AuditWorker.NoBlockingInFilesRead, "2 of 4 files read"), "the files GitHub did not list count as not read, so the review is never ready to merge");
        Notes(card).ShouldBe(["2 files not read: GitHub did not list these files."], "the note says why those files were not read");
        notifier.Posts.First().Text.ShouldStartWith(
            "Review of acme/shop#12: 1 of 2 batches done, 1 of 4 files read",
            customMessage: "the batches that can run leave out the files GitHub did not list, and the files read count them");
    }

    [Fact]
    public async Task C4_the_card_says_how_many_times_the_model_was_asked_for_the_review()
    {
        var store = Store();
        var job = await ReviewAsync(store, 2);
        var notifier = new RecordingNotifier();
        var chat = new ScriptedChat(Kernels.PresentingAnEmptyReview);

        await Worker(store, chat, notifier, new FakePullRequests(() => At(Head, 2))).RunReviewAsync(job, CancellationToken.None);

        chat.Turns.Count.ShouldBeGreaterThan(1, "the review asked the model more than once");
        AllNotes(Card(notifier.Posts.Last().Text)).ShouldContain(
            string.Create(CultureInfo.InvariantCulture, $"Scout asked the model {chat.Turns.Count + chat.Contests.Count} times for this review."), "the card counts every turn the review asked of the model");
    }

    public static TheoryData<string, Func<Exception>> ChecksFailures => new()
    {
        { "GitHub fails", () => new HttpRequestException("reset") },
        { "the read times out", () => new TaskCanceledException("timed out") },
    };

    [Theory]
    [MemberData(nameof(ChecksFailures))]
    public async Task The_card_is_built_when_the_checks_cannot_be_read_and_its_offer_says_so(string how, Func<Exception> failure)
    {
        var store = Store();
        var job = await ReviewAsync(store, 2);
        var notifier = new RecordingNotifier();
        var logger = new RecordingLogger<AuditWorker>();

        await Worker(store, new ScriptedChat(Kernels.PresentingAnEmptyReview), notifier, new FakePullRequests(() => At(Head, 2)) { ChecksFail = failure }, logger: logger)
            .RunReviewAsync(job, CancellationToken.None);

        var offer = Card(notifier.Posts.Last().Text).GetProperty("offer");
        offer.GetProperty("verdict").GetString().ShouldBe("comment", $"when {how}, the card is still built and offers a comment");
        offer.GetProperty("reason").GetString().ShouldNotBeNull().ShouldContain("the pull request's checks could not be read", Case.Sensitive, $"when {how}, the offer says the checks could not be read");
        logger.Entries.ShouldContain(e => e.Message.Contains("could not read its checks", StringComparison.Ordinal), $"when {how}, the failure is logged");
    }

    [Fact]
    public async Task The_stored_checks_warnings_are_on_the_card()
    {
        var store = Store();
        var preflight = new ReviewPreflight(
        [
            new(PreflightName.Size, PreflightOutcome.Ok, "2 files in 2 batches; at most 40 minutes."),
            new(PreflightName.DockerRuns, PreflightOutcome.Warning, "Docker runs are off; tests will not run."),
            new(PreflightName.Head, PreflightOutcome.Warning, "Head abc1234; 3 checks passing, 0 pending, 1 failing."),
        ]);
        var job = await ReviewAsync(store, 2, preflight);
        var notifier = new RecordingNotifier();

        await Worker(store, new ScriptedChat(Kernels.PresentingAnEmptyReview), notifier, new FakePullRequests(() => At(Head, 2))).RunReviewAsync(job, CancellationToken.None);

        Notes(Card(notifier.Posts.Last().Text)).ShouldBe(["Docker runs are off; tests will not run.", "Head abc1234; 3 checks passing, 0 pending, 1 failing."]);
    }

    [Fact]
    public async Task A_review_never_posts_a_block_of_kind_audit_and_calls_no_declared_write()
    {
        var store = Store();
        var job = await ReviewAsync(store, 2);
        var kernel = Kernels.Audit();
        var called = new ConcurrentQueue<string>();
        kernel.FunctionInvocationFilters.Add(new Recording(called));
        var chat = new ScriptedChat(async (history, turn, _) =>
        {
            await Kernels.ReadAsync(turn, $"src/M{BatchOf(history)}/F.cs");
            await Kernels.PresentReviewAsync(turn, AuditBlockForThisPullRequest($"src/M{BatchOf(history)}/F.cs"));
        });
        var notifier = new RecordingNotifier();

        await Worker(store, chat, notifier, new FakePullRequests(() => At(Head, 2)), kernel).RunReviewAsync(job, CancellationToken.None);

        called.Distinct().Order(StringComparer.Ordinal).ToList().ShouldBe(["GitHub.get_file_contents", "GitHub.present_review_findings", "GitHubActions.get_project_conventions"],
            "the reads, the conventions and the findings function only: no snapshot, no issue, no write");
        notifier.Posts.ShouldAllBe(p => p.Origin == "local:session");
        notifier.Posts.ShouldNotContain(p => p.Text.Contains("\"kind\":\"audit\"", StringComparison.Ordinal));
        Card(notifier.Posts.Last().Text).GetProperty("findings").GetArrayLength().ShouldBe(0, "an audit block a batch attached is never the review's");
    }

    [Fact]
    public async Task A_review_batch_is_instructed_with_the_code_review_steps_and_the_conventions_never_the_audits_pass_2()
    {
        var store = Store();
        var job = await ReviewAsync(store, 1);
        var chat = new ScriptedChat(Kernels.PresentingAnEmptyReview);

        await Worker(store, chat, new RecordingNotifier(), new FakePullRequests(() => At(Head, 1)), skills: new BothSkills()).RunReviewAsync(job, CancellationToken.None);

        chat.Turns.ShouldHaveSingleItem().System.ShouldBe(
            "## What to look for, in any language\nOnly the diff.\n\n## 9. Feedback\nAnchor each finding.\n\n"
            + "The project's conventions profile, the rules this review judges against:\n" + Kernels.Conventions);
    }

    [Fact]
    public async Task A_review_the_provider_limits_at_batch_2_pauses_and_a_start_resumes_there()
    {
        var store = Store();
        var job = await ReviewAsync(store, 3);
        var limited = true;
        var chat = new ScriptedChat((history, kernel, turn) => limited && BatchOf(history) == 2
            ? throw new LlmQuotaExceededException("usage_limit_reached")
            : Kernels.PresentingAnEmptyReview(history, kernel, turn));
        var notifier = new RecordingNotifier();
        var worker = Worker(store, chat, notifier, new FakePullRequests(() => At(Head, 3)));

        await worker.RunReviewAsync(job, CancellationToken.None);

        var paused = (await store.JobAsync(job.Id, CancellationToken.None))!;
        (paused.Status, paused.PausedReason).ShouldBe((AuditStatus.Paused, "the AI provider's usage limit was reached"));
        (await store.BatchesAsync(job.Id, CancellationToken.None)).Select(b => b.Status).ShouldBe([AuditStatus.Done, AuditStatus.Pending, AuditStatus.Pending]);
        notifier.Posts.Select(p => p.Text).ShouldBe(
        [
            "Review of acme/shop#12: 1 of 3 batches done, 1 of 3 files read, 0 findings so far, reading src/M2.",
            "The review of acme/shop#12 paused: the AI provider's usage limit was reached. Ask Scout to review acme/shop#12 again to resume it from the first batch not done.",
        ]);

        limited = false;
        var audits = new RepositoryAudits(
            store, new AuditSignal(), new AuditOptions(), new FakeSnapshots(), new FakeEnvironment(), () => Kernels.Audit(), () => null, NullLogger<RepositoryAudits>.Instance);
        var resumed = await audits.StartAsync("acme", "shop", 12, Head, At(Head, 3).Files, 1, 0, null, CancellationToken.None);
        await worker.RunReviewAsync((await store.JobAsync(job.Id, CancellationToken.None))!, CancellationToken.None);

        resumed.Sentence.ShouldStartWith("Resuming the review of acme/shop#12 at abc1234 from the first batch not done.");
        chat.Turns.Select(t => BatchOf(t.Request)).ShouldBe([1, 2, 2, 3]);
        (await store.JobAsync(job.Id, CancellationToken.None))!.Status.ShouldBe(AuditStatus.Done);
        Card(notifier.Posts.Last().Text).GetProperty("coverage").GetProperty("read").GetInt32().ShouldBe(3);
    }

    [Theory]
    [InlineData("GitHub fails")]
    [InlineData("GitHub does not find the pull request")]
    public async Task A_head_re_read_GitHub_fails_runs_no_turn_and_pauses_with_the_reason(string answer)
    {
        var store = Store();
        var job = await ReviewAsync(store, 2);
        var chat = new ScriptedChat(Kernels.PresentingAnEmptyReview);
        var notifier = new RecordingNotifier();
        var pullRequests = new FakePullRequests(() => answer == "GitHub fails" ? throw new HttpRequestException("reset") : null);

        await Worker(store, chat, notifier, pullRequests).RunReviewAsync(job, CancellationToken.None);

        chat.Turns.ShouldBeEmpty("a head re-read that fails runs no turn");
        var paused = (await store.JobAsync(job.Id, CancellationToken.None))!;
        (paused.Status, paused.PausedReason).ShouldBe((AuditStatus.Paused, AuditWorker.GitHubUnreadable), "a head re-read that fails pauses the review with the reason");
        (await store.BatchesAsync(job.Id, CancellationToken.None)).ShouldAllBe(b => b.Status == AuditStatus.Pending);
        notifier.Posts.ShouldHaveSingleItem().Text.ShouldBe(
            "The review of acme/shop#12 paused: GitHub could not be read. Ask Scout to review acme/shop#12 again to resume it from the first batch not done.");
    }

    [Fact]
    public async Task A_database_failure_saving_a_batch_posts_no_card_and_keeps_the_worker_alive()
    {
        var sqlite = Store();
        var job = await ReviewAsync(sqlite, 2);
        var store = new RacingStore(sqlite)
        {
            BeforeComplete = () => throw new AuditStoreException("Scout could not save the audit database (SQLite error 8).", new InvalidOperationException()),
        };
        var logger = new ErrorLogger<AuditWorker>();
        var notifier = new RecordingNotifier();
        using var worker = Worker(store, new ScriptedChat(Kernels.PresentingAnEmptyReview), notifier, new FakePullRequests(() => At(Head, 2)), logger: logger);

        await worker.StartAsync(CancellationToken.None);
        await Task.WhenAny(logger.Error, Task.Delay(TimeSpan.FromSeconds(10)));

        (await logger.Error).ShouldBe("The audit worker could not use its database: Scout could not save the audit database (SQLite error 8).");
        worker.ExecuteTask!.IsCompleted.ShouldBeFalse("the worker waits for the next start");
        notifier.Posts.ShouldBeEmpty();
        (await sqlite.JobAsync(job.Id, CancellationToken.None))!.Status.ShouldBe(AuditStatus.Running);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_post_the_notifier_fails_does_not_stop_the_review()
    {
        var store = Store();
        var job = await ReviewAsync(store, 3);
        var chat = new ScriptedChat(Kernels.PresentingAnEmptyReview);

        await Worker(store, chat, new RecordingNotifier(throws: true), new FakePullRequests(() => At(Head, 3))).RunReviewAsync(job, CancellationToken.None);

        chat.Turns.Count.ShouldBe(3);
        (await store.JobAsync(job.Id, CancellationToken.None))!.Status.ShouldBe(AuditStatus.Done);
        (await store.BatchesAsync(job.Id, CancellationToken.None)).ShouldAllBe(b => b.Status == AuditStatus.Done);
    }

    [Theory]
    [InlineData("refused", false)]
    [InlineData("throws", false)]
    [InlineData("refused", true)]
    public async Task A_card_the_findings_function_refuses_is_one_plain_sentence_with_verdict_and_coverage(string how, bool blocking)
    {
        var store = Store();
        var job = await ReviewAsync(store, 2);
        var kernel = Kernels.Audit(presentReview: _ => how == "throws"
            ? throw new HttpRequestException("reset")
            : "The findings are over 24,000 characters as a block; send fewer findings, the ten that matter most, with shorter why and fix texts.");
        var chat = new ScriptedChat((history, turn, _) => Kernels.PresentReviewAsync(turn, blocking && BatchOf(history) == 2 ? Block(("rate", "block", "src/M2/F.cs")) : Block()));
        var notifier = new RecordingNotifier();

        await Worker(store, chat, notifier, new FakePullRequests(() => At(Head, 2, withoutPatch: 1)), kernel).RunReviewAsync(job, CancellationToken.None);

        notifier.Posts.Last().Text.ShouldBe(blocking
            ? "The review of acme/shop#12 at abc1234 is done: ready after the blocking findings, 1 of 2 files read, 1 findings kept."
            : "The review of acme/shop#12 at abc1234 is done: no blocking findings in the files read, 1 of 2 files read, 0 findings kept.");
    }

    [Fact]
    public async Task A_card_holds_the_50_most_severe_findings_and_a_note_counting_the_rest()
    {
        var store = Store();
        var job = await ReviewAsync(store, 1);
        await store.CompleteBatchAsync(job.Id, 1, ["src/M1/F.cs"],
        [
            .. Enumerable.Range(1, 30).Select(i => new Finding($"n{i}", "nit", $"Nit {i}", "src/M1/F.cs", i, null, "Why.", "Fix.", null)),
            .. Enumerable.Range(1, 30).Select(i => new Finding($"f{i}", "fix", $"Fix {i}", "src/M1/F.cs", i, null, "Why.", "Fix.", null)),
            new Finding("b1", "block", "Block 1", "src/M1/F.cs", 1, null, "Why.", "Fix.", null),
        ], null, CancellationToken.None);
        var notifier = new RecordingNotifier();

        await Worker(store, new ScriptedChat(Kernels.PresentingAnEmptyReview), notifier, new FakePullRequests(() => At(Head, 1))).RunReviewAsync(job, CancellationToken.None);

        var card = Card(notifier.Posts.Last().Text);
        var ids = card.GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("id").GetString()).ToList();
        ids.Count.ShouldBe(AuditWorker.MaxCardFindings);
        ids.ShouldBe(["b1", .. Enumerable.Range(1, 30).Select(i => $"f{i}"), .. Enumerable.Range(1, 19).Select(i => $"n{i}")]);
        Notes(card).ShouldBe(["Scout's second reading did not run for 1 finding: it reached its 15 requests.", "11 more findings are not on the card."], "61 findings make 16 requests of four, one past the bound");
    }

    [Fact]
    public async Task A_review_whose_batches_presented_nothing_is_never_ready_to_merge()
    {
        var store = Store();
        var job = await ReviewAsync(store, 2);
        var notifier = new RecordingNotifier();

        await Worker(store, new ScriptedChat((_, _, _) => Task.CompletedTask), notifier, new FakePullRequests(() => At(Head, 2))).RunReviewAsync(job, CancellationToken.None);

        (await store.BatchesAsync(job.Id, CancellationToken.None)).Select(b => (b.Status, b.Failure)).ShouldBe(
            [(AuditStatus.NotCovered, (string?)AuditBatchRunner.NothingPresented), (AuditStatus.NotCovered, AuditBatchRunner.NothingPresented)],
            "a batch whose turns presented nothing is not covered, with the reason");
        var card = Card(notifier.Posts.Last().Text);
        card.GetProperty("verdict").GetString().ShouldBe(AuditWorker.NoBlockingInFilesRead, "a review whose batches presented nothing is never ready to merge");
        (card.GetProperty("coverage").GetProperty("read").GetInt32(), card.GetProperty("coverage").GetProperty("of").GetInt32()).ShouldBe((0, 2), "0 of 2 files read");
    }

    [Fact]
    public async Task A_card_that_cannot_be_built_leaves_the_review_running_for_the_next_wake()
    {
        var sqlite = Store();
        var job = await ReviewAsync(sqlite, 1);
        var store = new RacingStore(sqlite)
        {
            BeforePreflight = () => throw new AuditStoreException("Scout could not read the audit database (SQLite error 8).", new InvalidOperationException()),
        };
        var notifier = new RecordingNotifier();
        using var worker = Worker(store, new ScriptedChat(Kernels.PresentingAnEmptyReview), notifier, new FakePullRequests(() => At(Head, 1)));

        await Should.ThrowAsync<AuditStoreException>(() => worker.RunReviewAsync(job, CancellationToken.None));

        (await sqlite.JobAsync(job.Id, CancellationToken.None))!.Status.ShouldBe(AuditStatus.Running, "a card that cannot be built leaves the review running");
        notifier.Posts.ShouldNotContain(p => p.Text.Contains(FindingsBlock.Fence, StringComparison.Ordinal), "no card is posted for a review whose card could not be built");

        store.BeforePreflight = null;
        await worker.RunReviewAsync(job, CancellationToken.None);

        Card(notifier.Posts.Last().Text).GetProperty("verdict").GetString().ShouldBe(AuditWorker.NoBlockingInFilesRead, "the next wake builds the card and posts it");
        (await sqlite.JobAsync(job.Id, CancellationToken.None))!.Status.ShouldBe(AuditStatus.Done);
    }

    [Fact]
    public async Task A_card_takes_the_most_severe_findings_that_fit_and_counts_the_rest()
    {
        var store = Store();
        var job = await ReviewAsync(store, 1);
        var why = new string('w', 600);
        await store.CompleteBatchAsync(job.Id, 1, ["src/M1/F.cs"],
        [
            .. Enumerable.Range(1, 40).Select(i => new Finding($"f{i}", "fix", $"Fix {i}", "src/M1/F.cs", i, null, why, "Fix.", null)),
            .. Enumerable.Range(1, 10).Select(i => new Finding($"b{i}", "block", $"Block {i}", "src/M1/F.cs", i, null, why, "Fix.", null)),
        ], null, CancellationToken.None);
        var notifier = new RecordingNotifier();

        await Worker(store, new ScriptedChat(Kernels.PresentingAnEmptyReview), notifier, new FakePullRequests(() => At(Head, 1))).RunReviewAsync(job, CancellationToken.None);

        var card = Card(notifier.Posts.Last().Text);
        var ids = card.GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("id").GetString()!).ToList();
        ids.Count.ShouldBeInRange(11, 49, "a card takes the findings that fit its budget, fewer than the 50 kept");
        ids.Take(10).ShouldBe(Enumerable.Range(1, 10).Select(i => $"b{i}"), "the most severe findings come first");
        Notes(card).ShouldBe([$"{50 - ids.Count} more findings are not on the card."], "the note counts the findings left off the card");
    }

    private static string[] Titles(string request)
    {
        var lines = request.ReplaceLineEndings("\n").Split('\n');
        var at = Array.IndexOf(lines, AuditBatchRunner.TitlesAreData);
        at.ShouldBeGreaterThanOrEqualTo(0, "the request carries the earlier titles");
        using var titles = JsonDocument.Parse(lines[at + 1]);
        return [.. titles.RootElement.EnumerateArray().Select(t => t.GetString()!)];
    }

    [Fact]
    public async Task A_later_batch_is_told_the_titles_earlier_batches_found()
    {
        var store = Store();
        var job = await ReviewAsync(store, 2);
        var chat = new ScriptedChat((history, kernel, _) => Kernels.PresentReviewAsync(kernel, BatchOf(history) == 1 ? Block(("rate", "fix", "src/M1/F.cs")) : Block()));

        await Worker(store, chat, new RecordingNotifier(), new FakePullRequests(() => At(Head, 2))).RunReviewAsync(job, CancellationToken.None);

        Titles(chat.Turns.ElementAt(1).Request).ShouldBe(["Title rate"], "batch 2 is told the title batch 1 found, in another file");
    }

    [Fact]
    public async Task A_batch_is_told_at_most_100_earlier_titles_the_most_recent_first()
    {
        var store = Store();
        var job = await ReviewAsync(store, 2);
        await store.CompleteBatchAsync(job.Id, 1, ["src/M1/F.cs"],
            [.. Enumerable.Range(1, 120).Select(i => new Finding($"f{i}", "fix", $"Title {i}", "src/M1/F.cs", i, null, "Why.", "Fix.", null))], null, CancellationToken.None);
        var chat = new ScriptedChat(Kernels.PresentingAnEmptyReview);

        await Worker(store, chat, new RecordingNotifier(), new FakePullRequests(() => At(Head, 2))).RunReviewAsync(job, CancellationToken.None);

        Titles(chat.Turns.ShouldHaveSingleItem().Request).ShouldBe(
            Enumerable.Range(21, 100).Reverse().Select(i => $"Title {i}"), "at most 100 earlier titles, the most recent first");
    }

    [Fact]
    public async Task A_card_the_function_refuses_for_its_size_is_presented_again_with_fewer_findings()
    {
        var store = Store();
        var job = await ReviewAsync(store, 1);
        await store.CompleteBatchAsync(job.Id, 1, ["src/M1/F.cs"],
            [.. Enumerable.Range(1, 8).Select(i => new Finding($"f{i}", i <= 2 ? "block" : "fix", $"Finding {i}", "src/M1/F.cs", i, null, "Why.", "Fix.", null))], null, CancellationToken.None);
        var sizes = new List<int>();
        var kernel = Kernels.Audit(presentReview: arguments =>
        {
            var count = JsonDocument.Parse(arguments["findings_json"]!.ToString()!).RootElement.GetArrayLength();
            sizes.Add(count);
            return count > 2 ? "The findings are over 24,000 characters as a block; send fewer findings." : Kernels.ReviewCard(arguments);
        });
        var notifier = new RecordingNotifier();

        await Worker(store, new ScriptedChat(Kernels.PresentingAnEmptyReview), notifier, new FakePullRequests(() => At(Head, 1)), kernel).RunReviewAsync(job, CancellationToken.None);

        sizes.ShouldBe([8, 4, 2], "a card the function refuses is presented again with half as many findings");
        var card = Card(notifier.Posts.Last().Text);
        card.GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("id").GetString()).ShouldBe(["f1", "f2"], "the most severe findings stay on the card");
        Notes(card).ShouldBe(["6 more findings are not on the card."], "the note counts every finding left off");
    }

    [Fact]
    public async Task A_review_of_17_batches_runs_every_one()
    {
        var store = Store();
        var job = await ReviewAsync(store, 17);
        var chat = new ScriptedChat(Kernels.PresentingAnEmptyReview);
        var notifier = new RecordingNotifier();

        await Worker(store, chat, notifier, new FakePullRequests(() => At(Head, 17))).RunReviewAsync(job, CancellationToken.None);

        chat.Turns.Count.ShouldBe(17, "a review runs every batch, never stopping at a count of batches");
        (await store.BatchesAsync(job.Id, CancellationToken.None)).ShouldAllBe(b => b.Status == AuditStatus.Done);
        Card(notifier.Posts.Last().Text).GetProperty("coverage").GetProperty("read").GetInt32().ShouldBe(17);
    }

    /// <summary>A chat whose turns stay open a moment, counting how many are open together.</summary>
    private sealed class OpenTurns
    {
        private int _open;

        public int Most { get; private set; }

        public async Task TurnAsync(ChatHistory history, Kernel kernel, int turn)
        {
            var open = Interlocked.Increment(ref _open);
            lock (this)
            {
                Most = Math.Max(Most, open);
            }

            await Task.Delay(40);
            Interlocked.Decrement(ref _open);
            await Kernels.PresentingAnEmptyReview(history, kernel, turn);
        }
    }

    [Fact]
    public async Task Up_to_four_batches_run_at_once_and_each_is_saved_as_it_ends()
    {
        var store = Store();
        var job = await ReviewAsync(store, 9);
        var turns = new OpenTurns();
        var refused = new ConcurrentQueue<string?>();
        var chat = new ScriptedChat(async (history, kernel, turn) =>
        {
            refused.Enqueue((await kernel.InvokeAsync("GitHub", "get_file_contents", new KernelArguments
            {
                ["owner"] = "other",
                ["repo"] = "shop",
                ["path"] = "src/A.cs",
                ["reference"] = "main",
            })).GetValue<string>());
            await turns.TurnAsync(history, kernel, turn);
        });
        var notifier = new RecordingNotifier();

        await Worker(store, chat, notifier, new FakePullRequests(() => At(Head, 9)), parallel: 4).RunReviewAsync(job, CancellationToken.None);

        turns.Most.ShouldBe(4, "four batches run at once, never more");
        chat.Turns.Count.ShouldBe(9);
        refused.ShouldAllBe(answer => answer == AuditBatchRunner.OtherRepository(job), "every parallel turn keeps the batch's hold on the job's repository");
        (await store.BatchesAsync(job.Id, CancellationToken.None)).ShouldAllBe(b => b.Status == AuditStatus.Done);
        notifier.Posts.Count(p => p.Text.Contains(" batches done, ", StringComparison.Ordinal)).ShouldBe(9, "each batch is saved, with its line, as it ends");
    }

    [Fact]
    public async Task With_one_at_a_time_batches_run_in_order()
    {
        var store = Store();
        var job = await ReviewAsync(store, 5);
        var turns = new OpenTurns();
        var chat = new ScriptedChat(turns.TurnAsync);

        await Worker(store, chat, new RecordingNotifier(), new FakePullRequests(() => At(Head, 5)), parallel: 1).RunReviewAsync(job, CancellationToken.None);

        turns.Most.ShouldBe(1, "one batch at a time runs one turn at a time");
        chat.Turns.Select(t => BatchOf(t.Request)).ShouldBe([1, 2, 3, 4, 5], "one at a time, the batches run in their order");
    }

    [Fact]
    public async Task A_stop_during_a_wave_lets_it_end_and_starts_no_other()
    {
        var store = Store();
        var job = await ReviewAsync(store, 6);
        var stopped = 0;
        var chat = new ScriptedChat(async (history, kernel, turn) =>
        {
            if (Interlocked.Increment(ref stopped) == 1)
            {
                await store.StopAsync("acme", "shop", 12, CancellationToken.None);
            }

            await Task.Delay(20);
            await Kernels.PresentingAnEmptyReview(history, kernel, turn);
        });
        var notifier = new RecordingNotifier();

        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await Worker(store, chat, notifier, new FakePullRequests(() => At(Head, 6)), parallel: 3).RunReviewAsync(job, bound.Token);
        }
        catch (OperationCanceledException) when (bound.IsCancellationRequested)
        {
        }

        bound.IsCancellationRequested.ShouldBeFalse("the worker leaves a review stopped during a wave, never staying on it");
        chat.Turns.Count.ShouldBe(3, "a stop during a wave lets that wave end and starts no other");
        (await store.BatchesAsync(job.Id, CancellationToken.None)).Select(b => b.Status).ShouldBe(
            [AuditStatus.Done, AuditStatus.Done, AuditStatus.Done, AuditStatus.Pending, AuditStatus.Pending, AuditStatus.Pending],
            "the running wave's batches are saved and the next wave's stay pending");
        (await store.JobAsync(job.Id, CancellationToken.None))!.Status.ShouldBe(AuditStatus.Stopped);
    }

    [Fact]
    public async Task A_pause_then_a_stop_during_a_running_batch_posts_one_card()
    {
        var store = Store();
        var job = await ReviewAsync(store, 3);
        var signal = new AuditSignal();
        var reviews = new RepositoryAudits(store, signal, new AuditOptions(), new FakeSnapshots(), new FakeEnvironment(), () => Kernels.Audit(), () => null, NullLogger<RepositoryAudits>.Instance);
        var asked = 0;
        var chat = new ScriptedChat(async (history, kernel, turn) =>
        {
            if (Interlocked.Increment(ref asked) == 1)
            {
                await reviews.ControlAsync("acme", "shop", 12, RepositoryAudits.Pause, CancellationToken.None);
                await reviews.ControlAsync("acme", "shop", 12, RepositoryAudits.Stop, CancellationToken.None);
            }

            await Kernels.PresentingAnEmptyReview(history, kernel, turn);
        });
        var cards = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifier = new RecordingNotifier(onPost: text =>
        {
            if (text.Contains(FindingsBlock.Fence, StringComparison.Ordinal))
            {
                cards.TrySetResult();
            }

            return Task.CompletedTask;
        });
        using var worker = new AuditWorker(
            store, new AuditBatchRunner(Kernels.Audit(), chat, null, TimeSpan.FromMinutes(10), NullLogger<AuditBatchRunner>.Instance),
            notifier, signal, new FakePullRequests(() => At(Head, 3)), NullLogger<AuditWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await Task.WhenAny(cards.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        signal.Wake();
        await Task.Delay(500);
        await worker.StopAsync(CancellationToken.None);

        notifier.Posts.Count(p => p.Text.Contains(FindingsBlock.Fence, StringComparison.Ordinal)).ShouldBe(1, "a pause then a stop during a running batch posts the stopped card once");
    }

    [Fact]
    public async Task A_provider_limit_in_one_batch_of_a_wave_saves_the_others_and_pauses_once()
    {
        var store = Store();
        var job = await ReviewAsync(store, 6);
        var chat = new ScriptedChat(async (history, kernel, turn) =>
        {
            if (BatchOf(history) == 2)
            {
                throw new LlmQuotaExceededException("usage_limit_reached");
            }

            await Task.Delay(20);
            await Kernels.PresentingAnEmptyReview(history, kernel, turn);
        });
        var notifier = new RecordingNotifier();

        await Worker(store, chat, notifier, new FakePullRequests(() => At(Head, 6)), parallel: 3).RunReviewAsync(job, CancellationToken.None);

        (await store.BatchesAsync(job.Id, CancellationToken.None)).Select(b => b.Status).ShouldBe(
            [AuditStatus.Done, AuditStatus.Pending, AuditStatus.Done, AuditStatus.Pending, AuditStatus.Pending, AuditStatus.Pending],
            "the wave's other batches are saved, and the limited one stays pending");
        (await store.JobAsync(job.Id, CancellationToken.None))!.Status.ShouldBe(AuditStatus.Paused);
        notifier.Posts.Count(p => p.Text.StartsWith("The review of acme/shop#12 paused:", StringComparison.Ordinal)).ShouldBe(1, "a provider limit pauses the review once, with one notice");
    }

    [Fact]
    public async Task A_review_started_from_Discord_posts_no_progress_line_and_one_card()
    {
        var store = Store();
        var job = (await store.JobAsync(
            await store.CreateJobAsync("acme", "shop", 12, Head, "discord:guild/channel", Batches(3), null, CancellationToken.None), CancellationToken.None))!;
        var notifier = new RecordingNotifier();

        await Worker(store, new ScriptedChat(Kernels.PresentingAnEmptyReview), notifier, new FakePullRequests(() => At(Head, 3))).RunReviewAsync(job, CancellationToken.None);

        notifier.Posts.ShouldHaveSingleItem("a review started from Discord gets no line per batch, only the card at the end").Text.ShouldContain(FindingsBlock.Fence);
    }

    [Fact]
    public async Task What_the_owner_does_while_a_review_runs_is_posted_once_when_it_takes_effect()
    {
        var store = Store();
        var job = await ReviewAsync(store, 3);
        var reviews = new RepositoryAudits(store, new AuditSignal(), new AuditOptions(), new FakeSnapshots(), new FakeEnvironment(), () => Kernels.Audit(), () => null,
            NullLogger<RepositoryAudits>.Instance, () => new RecordingNotifier());
        var notifier = new RecordingNotifier();
        var turns = 0;
        var chat = new ScriptedChat(async (history, kernel, turn) =>
        {
            if (Interlocked.Increment(ref turns) == 1)
            {
                (await reviews.ControlAsync("acme", "shop", 12, RepositoryAudits.Pause, CancellationToken.None)).Applied.ShouldBeTrue();
            }

            await Kernels.PresentingAnEmptyReview(history, kernel, turn);
        });
        var worker = Worker(store, chat, notifier, new FakePullRequests(() => At(Head, 3)));

        await worker.RunReviewAsync(job, CancellationToken.None);
        var paused = notifier.Posts.Select(p => p.Text).ToList();
        var resumePosts = new RecordingNotifier();
        var resuming = new RepositoryAudits(store, new AuditSignal(), new AuditOptions(), new FakeSnapshots(), new FakeEnvironment(), () => Kernels.Audit(), () => null,
            NullLogger<RepositoryAudits>.Instance, () => resumePosts);
        (await resuming.ControlAsync("acme", "shop", 12, RepositoryAudits.Continue, CancellationToken.None)).Applied.ShouldBeTrue();
        turns = 1;
        var stopping = new ScriptedChat(async (history, kernel, turn) =>
        {
            (await reviews.ControlAsync("acme", "shop", 12, RepositoryAudits.Stop, CancellationToken.None)).Applied.ShouldBeTrue();
            await Kernels.PresentingAnEmptyReview(history, kernel, turn);
        });
        var stopNotices = new RecordingNotifier();
        await Worker(store, stopping, stopNotices, new FakePullRequests(() => At(Head, 3))).RunReviewAsync((await store.JobAsync(job.Id, CancellationToken.None))!, CancellationToken.None);

        paused.ShouldBe(
        [
            "Review of acme/shop#12: 1 of 3 batches done, 1 of 3 files read, 0 findings so far, reading src/M2.",
            "The review of acme/shop#12 paused: you paused it. Ask Scout to continue the review of acme/shop#12 to resume it from the first batch not done.",
        ], "a pause posts its one notice when the batch in progress ends");
        resumePosts.Posts.Select(p => p.Text).ShouldBe(["Review of acme/shop#12 resumed: 1 of 3 batches done, 1 of 3 files read, 0 findings so far."], "a continue posts one line to the desktop's chat");
        stopNotices.Posts.Select(p => p.Text).Last().ShouldStartWith("Review of acme/shop#12 at abc1234, stopped at your request:\n\n```scout-findings\n", customMessage: "a stop posts the card of what was read when the batch in progress ends");
        stopNotices.Posts.Count(p => p.Text.Contains(FindingsBlock.Fence, StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task A_stopped_review_posts_one_card_of_what_was_read()
    {
        var store = Store();
        var job = await ReviewAsync(store, 4);
        await store.CompleteBatchAsync(job.Id, 1, ["src/M1/F.cs"], [new Finding("rate", "block", "Tax rate doubled", "src/M1/F.cs", 3, null, "Why.", "Fix.", null)], null, CancellationToken.None);
        await store.StopAsync("acme", "shop", 12, CancellationToken.None);
        var notifier = new RecordingNotifier();

        await Worker(store, new ScriptedChat(Kernels.PresentingAnEmptyReview), notifier, new FakePullRequests(() => At(Head, 4))).RunReviewAsync(job, CancellationToken.None);

        var post = notifier.Posts.ShouldHaveSingleItem("a stopped review posts one card").Text;
        post.ShouldStartWith("Review of acme/shop#12 at abc1234, stopped at your request:\n\n```scout-findings\n", customMessage: "a stopped review posts the card of what was read under its own heading");
        var card = Card(post);
        Notes(card).ShouldBe(
            ["Scout's second reading did not run for 1 finding: the review was stopped.", "3 files not read: the review was stopped."],
            "the card says the second reading did not run and how many files were not read because the review was stopped");
        card.GetProperty("verdict").GetString().ShouldBe(AuditWorker.ReadyAfterBlocking, "the verdict follows the existing rule");
        (card.GetProperty("coverage").GetProperty("read").GetInt32(), card.GetProperty("coverage").GetProperty("of").GetInt32()).ShouldBe((1, 4));
    }

    [Fact]
    public async Task A_review_stopped_before_anything_was_read_says_so_in_one_sentence()
    {
        var store = Store();
        var job = await ReviewAsync(store, 2);
        await store.StopAsync("acme", "shop", 12, CancellationToken.None);
        var notifier = new RecordingNotifier();

        await Worker(store, new ScriptedChat(Kernels.PresentingAnEmptyReview), notifier, new FakePullRequests(() => At(Head, 2))).RunReviewAsync(job, CancellationToken.None);

        notifier.Posts.ShouldHaveSingleItem().Text.ShouldBe("The review of acme/shop#12 ended at your request: nothing was read.", "a review stopped before any file was read says so in one sentence");
    }

    [Fact]
    public async Task Stopping_a_paused_review_posts_one_card_of_what_was_read()
    {
        var store = Store();
        var job = await ReviewAsync(store, 3);
        await store.CompleteBatchAsync(job.Id, 1, ["src/M1/F.cs"], [], null, CancellationToken.None);
        await store.PauseAsync(job.Id, RepositoryAudits.PausedByOwner, CancellationToken.None);
        var signal = new AuditSignal();
        var card = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifier = new RecordingNotifier(onPost: text =>
        {
            card.TrySetResult();
            return Task.CompletedTask;
        });
        using var worker = new AuditWorker(
            store, new AuditBatchRunner(Kernels.Audit(), new ScriptedChat(Kernels.PresentingAnEmptyReview), null, TimeSpan.FromMinutes(10), NullLogger<AuditBatchRunner>.Instance),
            notifier, signal, new FakePullRequests(() => At(Head, 3)), NullLogger<AuditWorker>.Instance);
        var reviews = new RepositoryAudits(store, signal, new AuditOptions(), new FakeSnapshots(), new FakeEnvironment(), () => Kernels.Audit(), () => null, NullLogger<RepositoryAudits>.Instance);
        await worker.StartAsync(CancellationToken.None);

        (await reviews.ControlAsync("acme", "shop", 12, RepositoryAudits.Stop, CancellationToken.None)).Applied.ShouldBeTrue();
        await Task.WhenAny(card.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        signal.Wake();
        await Task.Delay(300);
        await worker.StopAsync(CancellationToken.None);

        var post = notifier.Posts.ShouldHaveSingleItem("stopping a paused review posts one card, and a later wake posts nothing more").Text;
        post.ShouldStartWith("Review of acme/shop#12 at abc1234, stopped at your request:\n\n```scout-findings\n", customMessage: "the card of what was read, under the stopped heading");
        Notes(Card(post)).ShouldBe(["2 files not read: the review was stopped."]);
    }

    private static string AuditBlockForThisPullRequest(string path) =>
        "```scout-findings\n" + JsonSerializer.Serialize(new
        {
            kind = "audit",
            repository = "acme/shop",
            number = 12,
            commit = "abc1234",
            verdict = "needs rework",
            findings = new[] { new { id = "paid", severity = "block", title = "Invoice errors read as paid", path, line = 12, why = "Why.", fix = "Fix." } },
        }) + "\n```";

    private sealed class Recording(ConcurrentQueue<string> called) : IFunctionInvocationFilter
    {
        public async Task OnFunctionInvocationAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
        {
            called.Enqueue($"{context.Function.PluginName}.{context.Function.Name}");
            await next(context);
        }
    }

    private sealed class BothSkills : ISkillCatalog
    {
        private static readonly Skill[] Skills =
        [
            new("code-audit", "audit", "## Pass 2: the audit\nBugs.\n\n## Opening the issues\nCall create_issue.", "code-audit.md"),
            new("code-review", "review",
                "# Code review\n\n## 3. Pipeline\nRun the tests.\n\n## What to look for, in any language\nOnly the diff.\n\n## 9. Feedback\nAnchor each finding.\n\n## 10. Decision and follow-up\nPost the review.",
                "code-review.md"),
        ];

        public IReadOnlyList<Skill> All() => Skills;

        public Skill? Find(string name) => Skills.FirstOrDefault(s => s.Name == name);
    }

    private static readonly ReviewInputs Kept = new(
        new ReviewPurpose("Checkout total", "Totals are never negative."),
        new ReviewRules([new("AGENTS.md", "Every write is declared.", Cut: false, ChangedHere: false)], [], Unreadable: false),
        new ReviewRemoved([], 0, 0, false));

    /// <summary>The line of JSON under <paramref name="sentence"/> in a batch's request, or null when the request has no such sentence.</summary>
    private static string? LineAfter(string request, string sentence)
    {
        var lines = request.ReplaceLineEndings("\n").Split('\n');
        var at = Array.IndexOf(lines, sentence);
        return at < 0 ? null : lines[at + 1];
    }

    [Fact]
    public async Task A_batch_after_a_pause_and_a_new_worker_gets_the_inputs_kept_at_the_start()
    {
        var store = Store();
        var job = await ReviewAsync(store, 3, inputs: Kept);
        var limited = true;
        var chat = new ScriptedChat((history, kernel, turn) => limited && BatchOf(history) == 2
            ? throw new LlmQuotaExceededException("usage_limit_reached")
            : Kernels.PresentingAnEmptyReview(history, kernel, turn));
        await Worker(store, chat, new RecordingNotifier(), new FakePullRequests(() => At(Head, 3))).RunReviewAsync(job, CancellationToken.None);
        (await store.JobAsync(job.Id, CancellationToken.None))!.Status.ShouldBe(AuditStatus.Paused);

        limited = false;
        await store.ResumeAsync(job.Id, "local:session", CancellationToken.None);
        await Worker(Store(), chat, new RecordingNotifier(), new FakePullRequests(() => At(Head, 3))).RunReviewAsync((await store.JobAsync(job.Id, CancellationToken.None))!, CancellationToken.None);

        chat.Turns.Select(t => BatchOf(t.Request)).ShouldBe([1, 2, 2, 3], "the review paused at batch 2 and a new worker ran it again and the batch after it");
        chat.Turns.Select(t => JsonSerializer.Deserialize<ReviewPurpose>(LineAfter(t.Request, AuditBatchRunner.PurposeIsData)!, JsonSerializerOptions.Web)).ShouldAllBe(
            sent => sent == Kept.Purpose, "every batch, before and after the pause and the new worker, is handed the purpose kept at the start");
        chat.Turns.Select(t => LineAfter(t.Request, AuditBatchRunner.RulesAreData)).Distinct().ShouldHaveSingleItem("every batch is handed the same rule files").ShouldNotBeNull();
        (await store.JobAsync(job.Id, CancellationToken.None))!.Status.ShouldBe(AuditStatus.Done);
    }

    [Fact]
    public async Task A_batch_is_handed_only_the_removals_of_its_own_files_and_of_the_code_they_tested()
    {
        var store = Store();
        AuditBatchPlan[] plan =
        [
            new("src/Cart", ["src/Cart/Cart.cs"], 10),
            new("src/Orders", ["src/Orders/Order.cs"], 10),
            new("src/Tax", ["src/Tax/Tax.cs"], 10),
        ];
        var inputs = Kept with
        {
            Removed = new ReviewRemoved(
            [
                new("tests/Shop.Tests/OrderTests.cs", RemovedEntry.TestFileDeleted, ["src/Orders/Order.cs"], 0, 0),
                new("src/Cart/Cart.cs", RemovedEntry.FileDeleted, [], 0, 0),
                new("src/Legacy/Export.cs", RemovedEntry.FileDeleted, [], 0, 0),
            ], 0, 0, false),
        };
        var id = await store.CreateJobAsync("acme", "shop", 12, Head, "local:session", plan, null, CancellationToken.None, inputs);
        var files = new ReviewPullRequest(Head, [.. plan.Select(b => new ReviewFile(b.Files[0], "@@ -1 +1 @@\n+line"))]);
        var chat = new ScriptedChat(Kernels.PresentingAnEmptyReview);

        await Worker(store, chat, new RecordingNotifier(), new FakePullRequests(() => files)).RunReviewAsync((await store.JobAsync(id, CancellationToken.None))!, CancellationToken.None);

        string[] Handed(int batch)
        {
            var line = LineAfter(chat.Turns.Single(t => BatchOf(t.Request) == batch).Request, AuditBatchRunner.RemovedIsData);
            if (line is null)
            {
                return [];
            }

            using var data = JsonDocument.Parse(line);
            return [.. data.RootElement.EnumerateArray().Select(e => e.GetProperty("path").GetString()!)];
        }

        Handed(1).ShouldBe(["src/Cart/Cart.cs"], "a batch is handed the removal of its own file");
        Handed(2).ShouldBe(["tests/Shop.Tests/OrderTests.cs"], "a batch with the code a deleted test covered is handed that test's removal");
        Handed(3).ShouldBeEmpty("a batch with no related removal is handed none, and no sentence for it");
    }

    [Fact]
    public async Task The_cards_notes_count_what_was_removed_and_never_name_a_path()
    {
        var store = Store();
        var preflight = new ReviewPreflight([new(PreflightName.Duplicate, PreflightOutcome.Warning, "A review of acme/shop#12 at abc1234 was stopped; this start reviews it again.")]);
        var inputs = new ReviewInputs(
            new ReviewPurpose("Checkout total", null, 7, IssueUnread: true),
            new ReviewRules([], [], Unreadable: true),
            new ReviewRemoved(
            [
                new("tests/Shop.Tests/CartTests.cs", RemovedEntry.TestFileDeleted, ["src/Shop/Cart.cs"], 0, 0),
                new("src/Legacy/Export.cs", RemovedEntry.FileDeleted, [], 0, 0),
            ], 0, 0, false));
        var job = await ReviewAsync(store, 1, preflight, inputs);
        var notifier = new RecordingNotifier();

        await Worker(store, new ScriptedChat(Kernels.PresentingAnEmptyReview), notifier, new FakePullRequests(() => At(Head, 1))).RunReviewAsync(job, CancellationToken.None);

        var notes = Notes(Card(notifier.Posts.Last().Text));
        notes.ShouldBe(
        [
            "A review of acme/shop#12 at abc1234 was stopped; this start reviews it again.",
            "The pull request deletes 2 files, 1 of them a test file.",
            "1 deleted or emptied test file leaves code named like it in the repository.",
            "The linked issue could not be read.",
            "The project's rule files could not be read.",
        ], "the card's notes count what was removed and say which input could not be read, after the checks' warnings");
        notes.Skip(1).ShouldNotContain(
            n => n.Contains('/', StringComparison.Ordinal) || n.Contains("Checkout", StringComparison.Ordinal) || n.Contains("Cart", StringComparison.Ordinal),
            "a note of the inputs never names a path or a word of the pull request");
    }

    [Fact]
    public async Task An_inputs_read_the_database_refuses_leaves_the_review_running_and_the_next_wake_runs_it()
    {
        var sqlite = Store();
        var job = await ReviewAsync(sqlite, 1, inputs: Kept);
        var store = new RacingStore(sqlite)
        {
            BeforeInputs = () => throw new AuditStoreException("Scout could not read the audit database (SQLite error 8).", new InvalidOperationException()),
        };
        var chat = new ScriptedChat(Kernels.PresentingAnEmptyReview);
        var notifier = new RecordingNotifier();
        var worker = Worker(store, chat, notifier, new FakePullRequests(() => At(Head, 1)));

        await Should.ThrowAsync<AuditStoreException>(() => worker.RunReviewAsync(job, CancellationToken.None));

        chat.Turns.ShouldBeEmpty("no batch runs without its inputs");
        (await sqlite.JobAsync(job.Id, CancellationToken.None))!.Status.ShouldBe(AuditStatus.Running, "an inputs read the database refuses leaves the review running");
        (await sqlite.BatchesAsync(job.Id, CancellationToken.None)).ShouldAllBe(b => b.Status == AuditStatus.Pending, "no batch was begun");

        store.BeforeInputs = null;
        await worker.RunReviewAsync(job, CancellationToken.None);

        LineAfter(chat.Turns.ShouldHaveSingleItem("the next wake runs the batch").Request, AuditBatchRunner.PurposeIsData).ShouldNotBeNull("the batch is handed its inputs");
        (await sqlite.JobAsync(job.Id, CancellationToken.None))!.Status.ShouldBe(AuditStatus.Done);
    }

    [Theory]
    [InlineData(ReviewOffers.RequestChanges, AuditWorker.ReadyAfterBlocking)]
    [InlineData(ReviewOffers.Approve, AuditWorker.ReadyToMerge)]
    [InlineData(ReviewOffers.Wait, AuditWorker.ReadyToMerge)]
    [InlineData(ReviewOffers.Comment, AuditWorker.NoBlockingInFilesRead)]
    [InlineData(ReviewOffers.None, AuditWorker.NoBlockingInFilesRead)]
    public void V7_the_verdict_sentence_follows_the_offer_and_never_disagrees_with_it(string offered, string sentence) =>
        AuditWorker.VerdictOf(new Offer(offered, "r", offered == ReviewOffers.RequestChanges)).ShouldBe(
            sentence, "ready to merge only when Scout offers approval, ready after the blocking findings only when it requests changes");

    [Fact]
    public void The_counts_of_a_fixed_set_of_batches_are_locked()
    {
        IReadOnlyList<AuditBatch> batches =
        [
            new(1, "Orders", ["src/A.cs", "src/B.cs"], AuditStatus.Done, ["src/A.cs", "src/B.cs"], null),
            new(2, "Cart", ["src/B.cs", "src/C.cs"], AuditStatus.Done, ["src/B.cs"], null),
            new(3, "Billing", ["src/D.cs"], AuditStatus.NotCovered, ["src/D.cs"], "the AI model did not answer in time"),
            new(4, "Docs", ["docs/E.md"], AuditStatus.Pending, [], null),
            new(5, RepositoryAudits.NoDiffArea, ["bin/F.dll"], AuditStatus.NotCovered, [], "no diff"),
        ];

        (AuditWorker.FilesRead(batches), AuditWorker.ReviewCounts(batches)).ShouldBe(
            (2, (3, 4, 2, 7, "Docs")),
            "files read count each file of a finished batch once; the counts leave out the batches kept not read from the start");
    }

    [Fact]
    public async Task The_card_order_of_a_fixed_set_of_findings_is_locked()
    {
        var store = Store();
        var job = await ReviewAsync(store, 1);
        await store.CompleteBatchAsync(job.Id, 1, ["src/M1/F.cs"],
        [
            new Finding("n1", "nit", "Nit 1", "src/M1/F.cs", 1, null, "W.", "F.", null),
            new Finding("f1", "fix", "Fix 1", "src/M1/F.cs", 2, null, "W.", "F.", null),
            new Finding("b1", "block", "Block 1", "src/M1/F.cs", 3, null, "W.", "F.", null),
            new Finding("f2", "fix", "Fix 2", "src/M1/F.cs", 4, null, "W.", "F.", null),
        ], null, CancellationToken.None);
        var notifier = new RecordingNotifier();

        await Worker(store, new ScriptedChat(Kernels.PresentingAnEmptyReview), notifier, new FakePullRequests(() => At(Head, 1))).RunReviewAsync(job, CancellationToken.None);

        Card(notifier.Posts.Last().Text).GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("id").GetString()).ShouldBe(
            ["b1", "f1", "f2", "n1"], "the card orders block, fix, nit, keeping the stored order within a severity");
    }

    [Fact]
    public async Task The_card_names_the_area_and_the_first_problem_of_each_batch_that_left_findings_out()
    {
        var store = Store();
        var job = await ReviewAsync(store, 2);
        await store.CompleteBatchAsync(job.Id, 1, ["src/M1/F.cs"], [], "1 finding left out: it needs a fix.", CancellationToken.None);
        await store.CompleteBatchAsync(job.Id, 2, ["src/M2/F.cs"], [], null, CancellationToken.None);
        var notifier = new RecordingNotifier();

        await Worker(store, new ScriptedChat(Kernels.PresentingAnEmptyReview), notifier, new FakePullRequests(() => At(Head, 2))).RunReviewAsync(job, CancellationToken.None);

        var card = Card(notifier.Posts.Last().Text);
        Notes(card).ShouldBe(["src/M1: 1 finding left out: it needs a fix."], "the finding left out is never silent: its batch's area and its problem are on the card");
        card.GetProperty("verdict").GetString().ShouldBe(AuditWorker.NoBlockingInFilesRead, "a finding left out never changes the verdict");
    }

    [Fact]
    public void The_notes_on_findings_left_out_stop_at_three_and_count_the_other_batches()
    {
        IReadOnlyList<AuditBatch> Left(int count) =>
            [.. Enumerable.Range(1, count).Select(i => new AuditBatch(i, $"src/M{i}", [], AuditStatus.Done, [], null, null, $"1 finding left out: it needs a fix ({i})."))];
        var long_ = new AuditBatch(1, "src/" + new string('a', 400), [], AuditStatus.Done, [], null, null, "1 finding left out: it needs a fix.");

        AuditWorker.LeftOut(Left(3)).Count.ShouldBe(3, "three batches show three notes");
        AuditWorker.LeftOut(Left(4)).ShouldBe(
            ["src/M1: 1 finding left out: it needs a fix (1).", "src/M2: 1 finding left out: it needs a fix (2).", "src/M3: 1 finding left out: it needs a fix (3).", "1 more batch left findings out."],
            "past three batches one line counts the rest");
        AuditWorker.LeftOut(Left(6))[^1].ShouldBe("3 more batches left findings out.", "the closing line counts every other batch");
        AuditWorker.LeftOut([long_]).ShouldHaveSingleItem().Length.ShouldBe(FindingLimits.MaxNoteLength, "a note is cut to the note length");
        (AuditWorker.MaxLeftOutNotes + 1 + 5 + 9 + 1).ShouldBeLessThanOrEqualTo(FindingLimits.MaxNotes - 1, "the left-out notes leave room for one not-read reason beside the other notes at their most: five preflight warnings, nine on the inputs, one on the findings not on the card");
    }

    [Fact]
    public async Task A_batch_whose_findings_and_sentence_cannot_be_saved_posts_no_card_and_keeps_the_batch_to_run_again()
    {
        var sqlite = Store();
        var job = await ReviewAsync(sqlite, 1);
        var store = new RacingStore(sqlite)
        {
            BeforeComplete = () => throw new AuditStoreException("Scout could not save the audit database (SQLite error 8).", new InvalidOperationException()),
        };
        var block = "```scout-findings\n{\"repository\":\"acme/shop\",\"number\":12,\"commit\":\"" + Head
            + "\",\"verdict\":\"ok\",\"findings\":[{\"id\":\"a\",\"severity\":\"fix\",\"title\":\"T\",\"path\":\"src/M1/F.cs\",\"line\":3,\"why\":\"W\"}]}\n```";
        var logger = new ErrorLogger<AuditWorker>();
        var notifier = new RecordingNotifier();
        using var worker = Worker(store, new ScriptedChat((_, kernel, _) => Kernels.PresentReviewAsync(kernel, block)), notifier, new FakePullRequests(() => At(Head, 1)), logger: logger);

        await worker.StartAsync(CancellationToken.None);
        await Task.WhenAny(logger.Error, Task.Delay(TimeSpan.FromSeconds(10)));
        await worker.StopAsync(CancellationToken.None);

        (await logger.Error).ShouldBe("The audit worker could not use its database: Scout could not save the audit database (SQLite error 8).");
        notifier.Posts.ShouldBeEmpty("no card is posted while the batch could not be saved");
        (await sqlite.BatchesAsync(job.Id, CancellationToken.None)).Select(b => (b.Status, b.LeftOut)).ShouldBe(
            [(AuditStatus.Pending, (string?)null)], "the batch stays pending with no sentence, so its next run reads it again");
    }

    private const string Passed = """{"ran":true,"image":"mcr.microsoft.com/dotnet/sdk:10.0","command":"dotnet test","exitCode":0,"durationSeconds":42,"output":"Passed!"}""";
    private const string NoRuntimeAnswer = """{"ran":false,"reason":"No container runtime is configured.","durationSeconds":0,"output":""}""";
    private const string Refused = "The owner has not granted the Local container write of the GitHub Feature in Scout Desktop.";

    /// <summary>A kernel whose test function counts its calls and answers <paramref name="answer"/>, and whose card carries a run kept when the function said it ran.</summary>
    private static (Kernel Kernel, List<KernelArguments> Calls) Testing(Func<CancellationToken, Task<string>> answer)
    {
        var calls = new List<KernelArguments>();
        var ran = false;
        var kernel = Kernels.Audit(
            presentReview: arguments =>
            {
                var answer = Kernels.ReviewCard(arguments);
                if (ran)
                {
                    foreach (var block in TurnAttachments.Take())
                    {
                        TurnAttachments.Attach(block.Replace("\"findings\":", "\"tests\":{\"ran\":true,\"exitCode\":0},\"findings\":", StringComparison.Ordinal));
                    }
                }

                return answer;
            },
            runTests: async (arguments, ct) =>
            {
                lock (calls)
                {
                    calls.Add(arguments);
                }

                var given = await answer(ct);
                ran = given.Contains("\"ran\":true", StringComparison.Ordinal);
                return given;
            });
        return (kernel, calls);
    }

    private static JsonElement Tests(JsonElement card) => card.GetProperty("tests");

    [Fact]
    public async Task T1_a_review_runs_the_tests_once_for_its_head_without_the_model_asking_and_its_progress_says_so()
    {
        var store = Store();
        var job = await ReviewAsync(store, 2);
        var (kernel, calls) = Testing(_ => Task.FromResult(Passed));
        var notifier = new RecordingNotifier();

        await Worker(store, new ScriptedChat(Kernels.PresentingAnEmptyReview), notifier, new FakePullRequests(() => At(Head, 2)), kernel).RunReviewAsync(job, CancellationToken.None);

        var call = calls.ShouldHaveSingleItem("the job runs the tests once, by code");
        (call["owner"], call["repo"], call["number"]).ShouldBe(((object?)"acme", (object?)"shop", (object?)12), "for the job's pull request");
        notifier.Posts.Select(p => p.Text).Where(t => t.StartsWith("Review of acme/shop#12: ", StringComparison.Ordinal))
            .Select(t => t.Split('\n')).Select(l => (Lines: l.Length, First: l[0].EndsWith('.'))).ToList()
            .ShouldAllBe(l => l.Lines == 2 && l.First, "the run's state is a second line, the first left as main reads it");
        (Tests(Card(notifier.Posts.Last().Text)).GetProperty("ran").GetBoolean()).ShouldBeTrue("the card carries the run kept for the head");
    }

    [Theory]
    [InlineData("refused for the grant", Refused, Refused)]
    [InlineData("no runtime", NoRuntimeAnswer, "No container runtime is configured.")]
    public async Task T2_T3_without_the_grant_or_a_runtime_nothing_runs_and_the_card_says_why_never_that_no_run_was_kept(string why, string answer, string reason)
    {
        var store = Store();
        var job = await ReviewAsync(store, 1);
        var (kernel, _) = Testing(_ => Task.FromResult(answer));
        var notifier = new RecordingNotifier();

        await Worker(store, new ScriptedChat(Kernels.PresentingAnEmptyReview), notifier, new FakePullRequests(() => At(Head, 1)), kernel).RunReviewAsync(job, CancellationToken.None);

        var tests = Tests(Card(notifier.Posts.Last().Text));
        (tests.GetProperty("ran").GetBoolean(), tests.GetProperty("reason").GetString()).ShouldBe((false, reason), $"the card says why the tests did not run when {why}");
        tests.GetProperty("reason").GetString().ShouldNotBe(AuditWorker.TestsNotKept);
    }

    [Fact]
    public async Task T4_a_run_that_throws_does_not_stop_the_review_and_the_card_says_what_happened()
    {
        var store = Store();
        var job = await ReviewAsync(store, 2);
        var (kernel, _) = Testing(_ => throw new InvalidOperationException("boom"));
        var notifier = new RecordingNotifier();

        await Worker(store, new ScriptedChat(Kernels.PresentingAnEmptyReview), notifier, new FakePullRequests(() => At(Head, 2)), kernel).RunReviewAsync(job, CancellationToken.None);

        (await store.JobAsync(job.Id, CancellationToken.None))!.Status.ShouldBe(AuditStatus.Done, "the batches are read and the review ends");
        Tests(Card(notifier.Posts.Last().Text)).GetProperty("reason").GetString().ShouldStartWith("The tests could not be run in Scout (", Case.Sensitive, customMessage: "the card says the run failed in Scout");
    }

    [Fact]
    public async Task T4_a_run_still_going_when_the_wait_ends_is_cut_and_the_card_is_posted_saying_so()
    {
        var store = Store();
        var job = await ReviewAsync(store, 1);
        var cut = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (kernel, _) = Testing(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => cut.TrySetResult(true), TaskScheduler.Default);
            ct.ThrowIfCancellationRequested();
            return Passed;
        });
        var notifier = new RecordingNotifier();
        var worker = new AuditWorker(store, new AuditBatchRunner(kernel, new ScriptedChat(Kernels.PresentingAnEmptyReview), null, TimeSpan.FromMinutes(10), NullLogger<AuditBatchRunner>.Instance),
            notifier, new AuditSignal(), new FakePullRequests(() => At(Head, 1)), NullLogger<AuditWorker>.Instance)
        { TestsWait = TimeSpan.FromMilliseconds(200) };

        await worker.RunReviewAsync(job, CancellationToken.None);

        (await store.JobAsync(job.Id, CancellationToken.None))!.Status.ShouldBe(AuditStatus.Done, "the review ends even though its tests did not");
        Tests(Card(notifier.Posts.Last().Text)).GetProperty("reason").GetString().ShouldBe(AuditWorker.TestsOverTheWait, "the card says the run did not end in time");
        (await cut.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue("the run still going is cut short");
    }

    [Fact]
    public async Task T4_the_grant_withdrawn_between_the_start_and_the_run_gives_the_gate_s_refusal_on_the_card()
    {
        var store = Store();
        var job = await ReviewAsync(store, 1);
        var granted = true;
        var (kernel, _) = Testing(_ => Task.FromResult(granted ? Passed : Refused));
        granted = false;
        var notifier = new RecordingNotifier();

        await Worker(store, new ScriptedChat(Kernels.PresentingAnEmptyReview), notifier, new FakePullRequests(() => At(Head, 1)), kernel).RunReviewAsync(job, CancellationToken.None);

        (await store.JobAsync(job.Id, CancellationToken.None))!.Status.ShouldBe(AuditStatus.Done);
        Tests(Card(notifier.Posts.Last().Text)).GetProperty("reason").GetString().ShouldBe(Refused, "the gate decides at the run, and its refusal is what the card says");
    }

    [Fact]
    public async Task T5_a_review_resumed_in_the_same_worker_runs_its_tests_once_and_a_new_worker_after_a_restart_runs_them_again_since_nothing_is_kept()
    {
        var store = Store();
        var job = await ReviewAsync(store, 2);
        var (kernel, calls) = Testing(_ => Task.FromResult(Passed));
        var chat = new ScriptedChat(Kernels.PresentingAnEmptyReview);
        var pullRequests = new FakePullRequests(() => At(Head, 2));
        var worker = Worker(store, chat, new RecordingNotifier(), pullRequests, kernel);
        await store.PauseAsync(job.Id, "you paused it", CancellationToken.None);

        await worker.RunReviewAsync(job, CancellationToken.None);
        await store.ResumeAsync(job.Id, null, CancellationToken.None);
        await worker.RunReviewAsync(job, CancellationToken.None);
        var inOneWorker = calls.Count;
        await Worker(store, chat, new RecordingNotifier(), pullRequests, kernel).RunReviewAsync(job, CancellationToken.None);

        (inOneWorker, calls.Count).ShouldBe((1, 2), "one run per review in a worker; a new worker keeps nothing of the run, so it runs the tests again");
    }

    [Fact]
    public async Task A_review_stopped_while_its_tests_run_cuts_the_run_and_its_card_says_so()
    {
        var store = Store();
        var job = await ReviewAsync(store, 2);
        var (kernel, _) = Testing(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Passed;
        });
        var notifier = new RecordingNotifier();
        var chat = new ScriptedChat(async (_, k, turn) =>
        {
            await Kernels.PresentingAnEmptyReview(default!, k, turn);
            await store.StopAsync("acme", "shop", 12, CancellationToken.None);
        });

        await Worker(store, chat, notifier, new FakePullRequests(() => At(Head, 2)), kernel).RunReviewAsync(job, CancellationToken.None);

        Tests(Card(notifier.Posts.Last().Text)).GetProperty("reason").GetString().ShouldBe(AuditWorker.TestsStoppedWithReview, "stopping the review cuts its run, and nothing is kept");
    }

    [Fact]
    public async Task A_review_whose_card_is_posted_forgets_its_test_run()
    {
        var store = Store();
        var job = await ReviewAsync(store, 1);
        var (kernel, calls) = Testing(_ => Task.FromResult(Passed));
        var worker = Worker(store, new ScriptedChat(Kernels.PresentingAnEmptyReview), new RecordingNotifier(), new FakePullRequests(() => At(Head, 1)), kernel);

        await worker.RunReviewAsync(job, CancellationToken.None);

        (calls.Count, worker.HoldsTests(job.Id)).ShouldBe((1, false), "the run ran once and is forgotten, its token freed, once the card is posted");
    }

    [Fact]
    public async Task F1_the_review_holds_the_run_s_facts_as_data_when_its_card_is_built()
    {
        const string Answer = """{"ran":true,"command":"dotnet test 'Shop.slnx' --no-restore","exitCode":1,"durationSeconds":240,"output":"","unrunnable":"…","projects":[{"name":"Shop.Tests","passed":827,"failed":0,"skipped":0,"couldNotRun":false},{"name":"Shop.IntegrationTests","passed":0,"failed":133,"skipped":0,"couldNotRun":true}],"projectsPartial":false}""";
        var store = Store();
        var job = await ReviewAsync(store, 1);
        var (kernel, _) = Testing(_ => Task.FromResult(Answer));
        var worker = Worker(store, new ScriptedChat(Kernels.PresentingAnEmptyReview), new RecordingNotifier(), new FakePullRequests(() => At(Head, 1)), kernel);

        await worker.RunReviewAsync(job, CancellationToken.None);

        var facts = worker.LastCardTests.ShouldNotBeNull("the card is built holding the run's facts");
        (facts.Ran, facts.Command, facts.ExitCode, facts.Partial, facts.Built).ShouldBe(
            (true, (string?)"dotnet test 'Shop.slnx' --no-restore", (int?)1, false, (bool?)true), "the run ran, its command and exit code, and its test projects built");
        facts.Projects.ShouldBe(
            [new TestProjectFacts("Shop.Tests", 827, 0, 0, false), new TestProjectFacts("Shop.IntegrationTests", 0, 133, 0, true)],
            "each test project's counts reach the card as data");
    }

    [Fact]
    public void A_refusal_or_a_run_with_no_summaries_gives_facts_that_say_so_and_never_claim_a_build()
    {
        AuditWorker.FactsOf(Refused).ShouldBe(new TestRunFacts(false, Refused, null, null, [], false), "the gate's refusal is a run that did not run, for that sentence");
        AuditWorker.FactsOf(NoRuntimeAnswer).ShouldSatisfyAllConditions(
            "no runtime: not run, its reason, no project, nothing said of a build",
            f => (f.Ran, f.Reason, f.Projects.Count, f.Built).ShouldBe((false, (string?)"No container runtime is configured.", 0, (bool?)null)));
    }

    private const string RefusalEvidence = "if (queue is null) Guard.Refuse(\"no queue\");";

    private const string RemovedLine = "if (queue is null) return NotFound();";

    private const string MeasuredTests = """{"ran":true,"command":"dotnet test 'Shop.slnx' --no-restore","exitCode":1,"durationSeconds":240,"output":"","projects":[{"name":"Shop.Tests","passed":827,"failed":0,"skipped":0,"couldNotRun":false},{"name":"Shop.Api.Tests","passed":1130,"failed":0,"skipped":0,"couldNotRun":false},{"name":"Shop.IntegrationTests","passed":0,"failed":133,"skipped":0,"couldNotRun":true}],"projectsPartial":false}""";

    /// <summary>
    /// The measured shape: blocking findings on sibling functions of one file, each quoting a lookup checked by a refusal
    /// helper whose declaration, marked as never returning, is in another file of the head.
    /// </summary>
    private async Task<(RecordingNotifier Notifier, ScriptedChat Chat, SqliteAuditStore Store, AuditJob Job)> ContestedAsync(
        Func<string, Task<string>>? contest, int findings = 3, TimeSpan? contestTime = null, Func<SqliteAuditStore, AuditJob, Task>? before = null,
        ReviewInputs? inputs = null, string why = "The compiler cannot infer Refuse never returns.", TimeSpan? contestGroupTime = null)
    {
        var root = Path.Combine(_folder.Path, "snapshot");
        Directory.CreateDirectory(Path.Combine(root, "src", "M1"));
        Directory.CreateDirectory(Path.Combine(root, "src", "Shop"));
        File.WriteAllText(Path.Combine(root, "src", "M1", "F.cs"), string.Concat(Enumerable.Range(1, 30).Select(n => n == 2 ? RefusalEvidence + "\n" : $"// line {n}\n")));
        File.WriteAllText(Path.Combine(root, "src", "Shop", "Guard.cs"), "namespace Shop;\n\npublic static class Guard\n{\n    [DoesNotReturn]\n    public static void Refuse(string reason) =>\n        throw new InvalidOperationException(reason);\n}\n");
        var snapshots = new FakeSnapshots();
        snapshots.Taken.Add(new AuditSnapshot("acme", "shop", Head, root, DateTimeOffset.UnixEpoch));
        var store = Store();
        var job = await ReviewAsync(store, 1, inputs: inputs);
        if (before is not null)
        {
            await before(store, job);
        }

        var pullRequest = new ReviewPullRequest(Head, [new ReviewFile("src/M1/F.cs", $"@@ -1,2 +1,2 @@\n var queue = Find(name);\n-{RemovedLine}\n+{RefusalEvidence}")]);
        var block = "```scout-findings\n" + JsonSerializer.Serialize(new
        {
            repository = "acme/shop",
            number = 12,
            commit = "abc1234",
            verdict = "needs rework",
            findings = Enumerable.Range(1, findings).Select(n => new { id = $"r{n}", severity = "block", title = $"Null lookup {n} dereferenced", path = "src/M1/F.cs", line = 2, why, fix = "Return after it.", evidence = RefusalEvidence }),
        }) + "\n```";
        var (kernel, _) = Testing(_ => Task.FromResult(MeasuredTests));
        var chat = new ScriptedChat((_, k, _) => Kernels.PresentReviewAsync(k, block));
        if (contest is not null)
        {
            chat.Contest = contest;
        }

        var notifier = new RecordingNotifier();
        await Worker(store, chat, notifier, new FakePullRequests(() => pullRequest), kernel, snapshots: snapshots, contestTime: contestTime, contestGroupTime: contestGroupTime).RunReviewAsync(job, CancellationToken.None);
        return (notifier, chat, store, job);
    }

    private static string Answer(params (string Id, string Status)[] entries) =>
        JsonSerializer.Serialize(entries.Select(e => new { id = e.Id, status = e.Status, reason = $"Guard.Refuse is marked as never returning, so {e.Id} cannot be null after it.", scenario = "A null lookup reaches line 2." }));

    private static string[] Ids(JsonElement card) => [.. card.GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("id").GetString()!)];

    [Fact]
    public async Task K1_K4_K5_the_measured_false_findings_are_refuted_by_the_second_reading_handed_the_definition_and_the_run_s_facts()
    {
        var (notifier, chat, _, _) = await ContestedAsync(_ => Task.FromResult(Answer(("r1", "refuted"), ("r2", "refuted"), ("r3", "refuted"))));

        var card = Card(notifier.Posts.Last().Text);
        Ids(card).ShouldBeEmpty("a finding the second reading refutes is not on the card");
        Notes(card).ShouldContain("3 findings refuted by Scout's second reading: Null lookup 1 dereferenced: Guard.Refuse is marked as never returning, so r1 cannot be null after it.", "the card says how many were refuted, with the first one's title and reason");
        card.GetProperty("verdict").GetString().ShouldBe(AuditWorker.NoBlockingInFilesRead, "the verdict follows what is kept: nothing blocks, and Scout does not approve on its own");
        var (request, settings, kernel) = chat.Contests.ShouldHaveSingleItem();
        request.ShouldSatisfyAllConditions(
            "the second reading is handed the definition of the name the quote calls and the run's facts",
            r => r.ShouldContain("--- src/Shop/Guard.cs:6\nnamespace Shop;\n\npublic static class Guard\n{\n    [DoesNotReturn]\n    public static void Refuse(string reason) =>", Case.Sensitive),
            r => r.ShouldContain("- Shop.Tests: 827 passed, 0 failed, 0 skipped.", Case.Sensitive),
            r => r.ShouldContain("- Shop.IntegrationTests: 0 passed, 133 failed, 0 skipped; its failures could not run", Case.Sensitive),
            r => r.ShouldContain(ContestRequest.FactsScope, Case.Sensitive),
            r => r.ShouldContain($"2: {RefusalEvidence}", Case.Sensitive));
        kernel.Plugins.SelectMany(p => p.Select(f => $"{p.Name}.{f.Name}")).Order(StringComparer.Ordinal).ToArray().ShouldBe(
            ["GitHub.get_file_contents", "GitHub.list_repository_files", "GitHub.search_code"],
            "the second reading is offered the three reads of the code and no function that writes or presents");
        settings?.FunctionChoiceBehavior.ShouldNotBeNull("the reads can be called");
        kernel.FunctionInvocationFilters.Select(f => f.GetType().Name).ShouldBe(["HeldToJobRepository"], "the reads are held to the job's repository, as a batch's are");
        request.ShouldStartWith(ContestRequest.ReadAt("acme", "shop", Head), Case.Sensitive, "the request says where to read");
        notifier.Posts.Select(p => p.Text).ShouldContain(t => t.EndsWith("\n" + AuditWorker.SecondReadingLine(3), StringComparison.Ordinal), "the progress says the second reading runs, on its own line");
    }

    [Fact]
    public async Task Findings_the_second_reading_says_hold_are_kept_as_they_are()
    {
        var (notifier, chat, _, _) = await ContestedAsync(null);

        var card = Card(notifier.Posts.Last().Text);
        Ids(card).ShouldBe(["r1", "r2", "r3"], "findings that hold are kept");
        card.GetProperty("verdict").GetString().ShouldBe(AuditWorker.ReadyAfterBlocking, "findings that hold keep their severity and the verdict");
        Notes(card).ShouldNotContain(n => n.Contains("second reading", StringComparison.Ordinal), "nothing to say when every finding holds");
        chat.Contests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task K2_a_finding_the_second_reading_cannot_confirm_is_kept_unconfirmed_and_never_blocking()
    {
        var (notifier, _, _, _) = await ContestedAsync(_ => Task.FromResult(Answer(("r1", "uncertain"))), findings: 1);

        var card = Card(notifier.Posts.Last().Text);
        var kept = card.GetProperty("findings").EnumerateArray().ShouldHaveSingleItem();
        (kept.GetProperty("severity").GetString(), kept.GetProperty("title").GetString()).ShouldBe(("fix", "Unconfirmed: Null lookup 1 dereferenced"), "an unconfirmed finding is kept, never blocking, and says so");
        card.GetProperty("verdict").GetString().ShouldBe(AuditWorker.NoBlockingInFilesRead, "an unconfirmed finding never blocks the verdict");
        card.GetProperty("offer").GetProperty("verdict").GetString().ShouldBe(ReviewOffers.Comment, "V2 an unconfirmed finding offers a comment, never approval");
        Notes(card).ShouldContain("1 finding could not be confirmed by Scout's second reading; it is kept as unconfirmed and does not block.");
    }

    [Fact]
    public async Task A_missing_a_duplicated_and_an_unknown_id_refute_nothing_but_the_valid_entry()
    {
        const string Mixed = """[{"id":"r1","status":"refuted","reason":"Refuse never returns."},{"id":"r2","status":"refuted","reason":"a"},{"id":"r2","status":"refuted","reason":"b"},{"id":"x9","status":"refuted","reason":"c"}]""";

        var (notifier, _, _, _) = await ContestedAsync(_ => Task.FromResult(Mixed));

        var card = Card(notifier.Posts.Last().Text);
        card.GetProperty("findings").EnumerateArray().Select(f => (f.GetProperty("id").GetString(), f.GetProperty("severity").GetString())).ShouldBe(
            [("r2", "fix"), ("r3", "fix")], "only the one valid refutation removes a finding; a duplicated or missing entry is unconfirmed");
    }

    public static TheoryData<string, Func<string, Task<string>>> FailedReadings => new()
    {
        { AuditWorker.ContestUnreadable, _ => Task.FromResult("They all look false to me.") },
        { AuditWorker.ContestFailed, _ => throw new HttpRequestException("reset") },
        { AuditWorker.ContestOverTime, _ => Task.Delay(Timeout.Infinite).ContinueWith(_ => "[]", TaskScheduler.Default) },
    };

    [Theory]
    [MemberData(nameof(FailedReadings))]
    public async Task K3_an_unreadable_failed_or_timed_out_second_reading_leaves_every_finding_as_it_was_and_the_card_says_it_did_not_run(string reason, Func<string, Task<string>> contest)
    {
        var (notifier, chat, store, job) = await ContestedAsync(contest, contestTime: TimeSpan.FromMilliseconds(300));

        chat.Contests.ShouldAllBe(c => c.Kernel.Plugins.SelectMany(p => p).Count() == 3, "the turn that failed was offered the reads");

        var card = Card(notifier.Posts.Last().Text);
        Ids(card).ShouldBe(["r1", "r2", "r3"], "no finding is removed");
        card.GetProperty("verdict").GetString().ShouldBe(AuditWorker.ReadyAfterBlocking, "no finding is weakened, so the verdict stays");
        card.GetProperty("findings").EnumerateArray().ShouldAllBe(f => f.GetProperty("severity").GetString() == "block");
        Notes(card).ShouldContain($"Scout's second reading did not run for 3 findings: {reason}", "the card says the second reading did not run, and why");
        (await store.FindingStatesAsync(job.Id, CancellationToken.None)).ShouldBeEmpty("nothing is recorded for a reading that did not run");
    }

    [Fact]
    public async Task S4_with_the_record_on_the_second_reading_s_calls_and_results_are_in_its_record()
    {
        var folder = Path.Combine(_folder.Path, "records");
        var root = Path.Combine(_folder.Path, "snapshot");
        Directory.CreateDirectory(Path.Combine(root, "src", "M1"));
        File.WriteAllText(Path.Combine(root, "src", "M1", "F.cs"), string.Concat(Enumerable.Range(1, 30).Select(n => n == 2 ? RefusalEvidence + "\n" : $"// line {n}\n")));
        var snapshots = new FakeSnapshots();
        snapshots.Taken.Add(new AuditSnapshot("acme", "shop", Head, root, DateTimeOffset.UnixEpoch));
        var store = Store();
        var job = await ReviewAsync(store, 1);
        var pullRequest = new ReviewPullRequest(Head, [new ReviewFile("src/M1/F.cs", $"@@ -1,2 +1,2 @@\n var queue = Find(name);\n-{RemovedLine}\n+{RefusalEvidence}")]);
        var block = "```scout-findings\n" + JsonSerializer.Serialize(new
        {
            repository = "acme/shop",
            number = 12,
            commit = "abc1234",
            verdict = "needs rework",
            findings = new[] { new { id = "r1", severity = "block", title = "Null lookup dereferenced", path = "src/M1/F.cs", line = 2, why = "Why.", fix = "Fix.", evidence = RefusalEvidence } },
        }) + "\n```";
        var chat = new ScriptedChat((_, k, _) => Kernels.PresentReviewAsync(k, block))
        {
            ContestReads = history =>
            {
                history.Add(new ChatMessageContent(AuthorRole.Assistant, [new FunctionCallContent("get_file_contents", "GitHub", "c1", new KernelArguments { ["path"] = "src/Shop/Queue.cs", ["firstLine"] = "40" })]));
                history.Add(new ChatMessageContent(AuthorRole.Tool, [new FunctionResultContent("get_file_contents", "GitHub", "c1", "src/Shop/Queue.cs (900 bytes) at abc1234, lines 40 to 60 of 60; the file ends there:\nclass Queue")]));
                return Task.CompletedTask;
            },
        };
        await Worker(store, chat, new RecordingNotifier(), new FakePullRequests(() => pullRequest), snapshots: snapshots, recordFolder: folder).RunReviewAsync(job, CancellationToken.None);

        var recorded = Path.Combine(folder, job.Id, "contest-1.jsonl");
        File.Exists(recorded).ShouldBeTrue("with the record on, the second reading's turn is recorded");
        using var line = JsonDocument.Parse(File.ReadAllLines(recorded).ShouldHaveSingleItem("one line per group"));
        var call = line.RootElement.GetProperty("calls").EnumerateArray().ShouldHaveSingleItem("the turn's call is in its record");
        (call.GetProperty("function").GetString(), call.GetProperty("arguments").GetProperty("path").GetString()).ShouldBe(("GitHub.get_file_contents", "src/Shop/Queue.cs"));
        line.RootElement.GetProperty("results").EnumerateArray().ShouldHaveSingleItem().GetProperty("start").GetString().ShouldStartWith("src/Shop/Queue.cs (900 bytes)", Case.Sensitive, "and its result");
    }

    [Fact]
    public async Task A3_A4_a_held_finding_takes_the_second_reading_s_severity_and_its_scenario_reaches_the_card()
    {
        var (notifier, _, store, job) = await ContestedAsync(_ => Task.FromResult(JsonSerializer.Serialize(new object[]
        {
            new { id = "r1", status = "holds", reason = "Guard.cs:6 returns.", scenario = "A refused name reaches F.cs:2 and the lookup is null.", severity = "fix", because = "a wrong result or a missing protection" },
            new { id = "r2", status = "holds", reason = "Guard.cs:6 returns.", scenario = "A refused name reaches F.cs:2.", severity = "block", because = "it looks bad" },
            new { id = "r3", status = "holds", reason = "Guard.cs:6 returns.", scenario = "A refused name loses the order at F.cs:2.", severity = "block", because = "lost or corrupted data" },
        })));

        var card = Card(notifier.Posts.Last().Text);
        card.GetProperty("findings").EnumerateArray().Select(f => (f.GetProperty("id").GetString(), f.GetProperty("severity").GetString())).ShouldBe(
            [("r3", "block"), ("r1", "fix"), ("r2", "fix")], "the second reading's severity replaces the first's, and a block without a named reason is a fix");
        card.GetProperty("findings").EnumerateArray().First(f => f.GetProperty("id").GetString() == "r1").GetProperty("why").GetString()
            .ShouldEndWith(" Scenario: A refused name reaches F.cs:2 and the lookup is null.", Case.Sensitive, "the scenario is the why's last sentence on the card");
        Notes(card).ShouldContain("2 findings' severities were changed by Scout's second reading.", "the card says how many severities changed");
        (await store.FindingsAsync(job.Id, CancellationToken.None)).First(f => f.Id == "r1").Severity.ShouldBe("fix", "the stored finding carries what the second reading concluded, for a review that continues");
    }

    [Fact]
    public async Task A5_a_group_that_runs_out_of_its_time_leaves_its_findings_as_they_were_and_the_next_group_still_runs()
    {
        var calls = 0;
        var (notifier, _, _, _) = await ContestedAsync(
            _ => Interlocked.Increment(ref calls) == 1
                ? Task.Delay(Timeout.Infinite).ContinueWith(_ => "[]", TaskScheduler.Default)
                : Task.FromResult(Answer(("r5", "refuted"))),
            findings: 5,
            contestTime: TimeSpan.FromSeconds(5),
            contestGroupTime: TimeSpan.FromMilliseconds(300));

        var card = Card(notifier.Posts.Last().Text);
        Ids(card).ShouldBe(["r1", "r2", "r3", "r4"], "the group that ran out of time keeps its findings, and the next group's refutation is applied");
        card.GetProperty("findings").EnumerateArray().ShouldAllBe(f => f.GetProperty("severity").GetString() == "block", "the findings left as the first reading gave them");
        Notes(card).ShouldContain($"Scout's second reading did not run for 4 findings: {AuditWorker.ContestOverTime}", "the card says which did not run");
    }

    private const string Marker = "MARKER-7f3a";

    private async Task<(SqliteAuditStore Store, AuditJob Job, ReviewPullRequest PullRequest, FakeSnapshots Snapshots)> OneFindingReviewAsync()
    {
        var root = Path.Combine(_folder.Path, "snapshot");
        Directory.CreateDirectory(Path.Combine(root, "src", "M1"));
        File.WriteAllText(Path.Combine(root, "src", "M1", "F.cs"), string.Concat(Enumerable.Range(1, 30).Select(n => n == 2 ? RefusalEvidence + "\n" : $"// line {n}\n")));
        var snapshots = new FakeSnapshots();
        snapshots.Taken.Add(new AuditSnapshot("acme", "shop", Head, root, DateTimeOffset.UnixEpoch));
        var store = Store();
        var job = await ReviewAsync(store, 1);
        var pullRequest = new ReviewPullRequest(Head, [new ReviewFile("src/M1/F.cs", $"@@ -1,2 +1,2 @@\n var queue = Find(name);\n-{RemovedLine}\n+{RefusalEvidence}")]);
        return (store, job, pullRequest, snapshots);
    }

    private static string OneFinding(string? evidence) => "```scout-findings\n" + JsonSerializer.Serialize(new
    {
        repository = "acme/shop",
        number = 12,
        commit = "abc1234",
        verdict = "needs rework",
        findings = new[] { new { id = "r1", severity = "block", title = "Null lookup dereferenced", path = "src/M1/F.cs", line = 2, why = "Why " + Marker, fix = "Fix.", evidence } },
    }) + "\n```";

    private static ScriptedChat Reading(Func<int, string> block) => new((history, kernel, turn) =>
    {
        history.Add(new ChatMessageContent(AuthorRole.Assistant, [new FunctionCallContent("get_file_contents", "GitHub", "c" + turn, new KernelArguments { ["path"] = "src/M1/F.cs", ["reference"] = Marker })]));
        history.Add(new ChatMessageContent(AuthorRole.Tool, [new FunctionResultContent("get_file_contents", "GitHub", "c" + turn, "file text " + Marker)]));
        history.Add(new ChatMessageContent(AuthorRole.Assistant, "answer " + Marker));
        return Kernels.PresentReviewAsync(kernel, block(turn));
    });

    private async Task<List<Dictionary<string, object?>>> TurnRowsAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_folder.Path, "audit.db")};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM audit_turn ORDER BY number";
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync())
        {
            rows.Add(Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, i => reader.IsDBNull(i) ? null : reader.GetValue(i)));
        }

        return rows;
    }

    [Fact]
    public async Task N1_N5_every_turn_of_a_review_writes_one_row_of_numbers_and_names_the_extra_request_and_the_second_reading_included()
    {
        var (store, job, pullRequest, snapshots) = await OneFindingReviewAsync();
        var chat = Reading(turn => OneFinding(turn == 1 ? null : RefusalEvidence));

        await Worker(store, chat, new RecordingNotifier(), new FakePullRequests(() => pullRequest), snapshots: snapshots).RunReviewAsync(job, CancellationToken.None);

        var rows = await TurnRowsAsync();
        rows.Select(r => ((long?)r["batch"], (string)r["reading"]!, (long?)r["contest_group"], (long)r["attempt"]!, (long)r["extra_request"]!)).ShouldBe(
            [(1, "general", null, 1, 0), (1, "general", null, 2, 1), (1, "removed", null, 1, 0), (null, "contest", 1, 1, 0)],
            "one row per turn: the general reading, its extra request for the finding with no evidence, the removed reading, and the second reading's group");
        rows[0].ShouldSatisfyAllConditions(
            "the first turn's numbers",
            r => ((long)r["presented"]!, (long)r["placed"]!, (long)r["left_out"]!).ShouldBe((1L, 0L, 1L)),
            r => ((string)r["calls"]!).ShouldBe("{\"GitHub.get_file_contents\":1}"));
        rows[3].ShouldSatisfyAllConditions(
            "the second reading's counts",
            r => ((long?)r["held"], (long?)r["refuted"], (long?)r["uncertain"]).ShouldBe((1L, 0L, 0L)));
        rows.SelectMany(r => r.Values).OfType<string>().ShouldAllBe(v => !v.Contains(Marker, StringComparison.Ordinal), "no row holds the answer's text, a file's content or a function's arguments");
        (await store.JobAsync(job.Id, CancellationToken.None)).ShouldNotBeNull();
    }

    [Fact]
    public async Task N2_the_card_s_count_of_turns_and_its_focused_note_are_read_from_the_rows_and_survive_a_restart_of_the_worker()
    {
        var (store, job, pullRequest, snapshots) = await OneFindingReviewAsync();
        await store.AddTurnAsync(job.Id, new AuditTurn(1, ReviewReadings.Requests, null, 1, false, 10, 100, new Dictionary<string, int>(), 0, 0, 0, "The turn timed out."), CancellationToken.None);
        await store.AddTurnAsync(job.Id, new AuditTurn(1, ReviewReadings.Requests, null, 2, false, 10, 100, new Dictionary<string, int>(), 0, 0, 0, "The turn timed out."), CancellationToken.None);
        var notifier = new RecordingNotifier();
        var chat = Reading(_ => OneFinding(RefusalEvidence));

        await Worker(store, chat, notifier, new FakePullRequests(() => pullRequest), snapshots: snapshots).RunReviewAsync(job, CancellationToken.None);

        var notes = AllNotes(Card(notifier.Posts.Last().Text));
        notes.ShouldContain("1 focused reading could not be completed; its findings may be missing.", "a reading an earlier worker run could not complete is counted from its rows");
        notes.ShouldContain(string.Create(CultureInfo.InvariantCulture, $"Scout asked the model {2 + chat.Turns.Count + chat.Contests.Count} times for this review."), "the turns of the earlier worker run are counted with this run's");
    }

    [Fact]
    public async Task N3_a_row_that_cannot_be_written_does_not_fail_the_turn()
    {
        var (inner, job, pullRequest, snapshots) = await OneFindingReviewAsync();
        var store = new RacingStore(inner) { OnTurn = _ => throw new AuditStoreException("The audit database could not be written.", new IOException("disk full")) };
        var notifier = new RecordingNotifier();

        await Worker(store, Reading(_ => OneFinding(RefusalEvidence)), notifier, new FakePullRequests(() => pullRequest), snapshots: snapshots).RunReviewAsync(job, CancellationToken.None);

        Ids(Card(notifier.Posts.Last().Text)).ShouldBe(["r1"], "the review ends with its finding whatever the rows");
        (await TurnRowsAsync()).ShouldBeEmpty("no row was written");
    }

    [Fact]
    public async Task The_version_and_the_chat_model_that_run_a_review_are_written_on_its_job()
    {
        var (store, job, pullRequest, snapshots) = await OneFindingReviewAsync();

        await Worker(store, Reading(_ => OneFinding(RefusalEvidence)), new RecordingNotifier(), new FakePullRequests(() => pullRequest), snapshots: snapshots).RunReviewAsync(job, CancellationToken.None);

        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_folder.Path, "audit.db")};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT scout_version, chat_model FROM audit_job";
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        (reader.IsDBNull(0) ? null : reader.GetString(0)).ShouldBe(AuditBatchRunner.ScoutVersion, "the version that ran it");
        reader.GetString(1).ShouldStartWith("Test/", Case.Sensitive, "the provider and model names that ran it");
    }

    [Fact]
    public async Task A_second_reading_that_fails_is_logged_by_the_kind_of_its_failure_never_its_message()
    {
        const string Marker = "MARKER-provider-text-9c1";
        var logger = new WholeEntryLogger();
        var root = Path.Combine(_folder.Path, "snapshot");
        Directory.CreateDirectory(Path.Combine(root, "src", "M1"));
        File.WriteAllText(Path.Combine(root, "src", "M1", "F.cs"), string.Concat(Enumerable.Range(1, 30).Select(n => n == 2 ? RefusalEvidence + "\n" : $"// line {n}\n")));
        var snapshots = new FakeSnapshots();
        snapshots.Taken.Add(new AuditSnapshot("acme", "shop", Head, root, DateTimeOffset.UnixEpoch));
        var store = Store();
        var job = await ReviewAsync(store, 1);
        var pullRequest = new ReviewPullRequest(Head, [new ReviewFile("src/M1/F.cs", $"@@ -1,2 +1,2 @@\n var queue = Find(name);\n-{RemovedLine}\n+{RefusalEvidence}")]);
        var block = "```scout-findings\n" + JsonSerializer.Serialize(new
        {
            repository = "acme/shop",
            number = 12,
            commit = "abc1234",
            verdict = "needs rework",
            findings = new[] { new { id = "r1", severity = "block", title = "Null lookup dereferenced", path = "src/M1/F.cs", line = 2, why = "Why.", fix = "Fix.", evidence = RefusalEvidence } },
        }) + "\n```";
        var chat = new ScriptedChat((_, k, _) => Kernels.PresentReviewAsync(k, block)) { Contest = _ => throw new HttpRequestException(Marker) };

        await Worker(store, chat, new RecordingNotifier(), new FakePullRequests(() => pullRequest), snapshots: snapshots, logger: logger).RunReviewAsync(job, CancellationToken.None);

        logger.Entries.ShouldContain(e => e.Contains("second reading failed (HttpRequestException)", StringComparison.Ordinal), "the failure is logged by its kind");
        logger.Entries.ShouldNotContain(e => e.Contains(Marker, StringComparison.Ordinal), "no log line holds the provider's message, nor an exception that carries it");
    }

    /// <summary>A logger that keeps each line with the message of any exception passed with it.</summary>
    private sealed class WholeEntryLogger : ILogger<AuditWorker>
    {
        public ConcurrentQueue<string> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue($"{formatter(state, exception)} {exception?.Message}");
    }

    [Fact]
    public async Task More_findings_than_a_group_holds_are_read_in_groups_of_four()
    {
        var (_, chat, _, _) = await ContestedAsync(null, findings: 6);

        chat.Contests.Select(c => c.Request.Split('\n').Count(line => line.StartsWith("Finding r", StringComparison.Ordinal)))
            .ShouldBe([4, 2], "four findings per request at most");
    }

    [Fact]
    public async Task K6_an_outcome_is_recorded_and_a_finding_that_has_one_is_not_read_again()
    {
        var (_, _, store, job) = await ContestedAsync(_ => Task.FromResult(Answer(("r1", "refuted"), ("r2", "uncertain"), ("r3", "holds"))));

        (await store.FindingStatesAsync(job.Id, CancellationToken.None)).ShouldBe(
            new Dictionary<string, (string State, string? Reason)>
            {
                ["r1"] = ("refuted", "Guard.Refuse is marked as never returning, so r1 cannot be null after it."),
                ["r2"] = ("uncertain", "Guard.Refuse is marked as never returning, so r2 cannot be null after it."),
                ["r3"] = ("held", null),
            },
            "each outcome is recorded in the finding's state, with the reason for refuted and uncertain");

        var again = new ScriptedChat(Kernels.PresentingAnEmptyReview);
        var notifier = new RecordingNotifier();
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_folder.Path, "audit.db")};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var restart = connection.CreateCommand();
            restart.CommandText = "UPDATE audit_job SET status = $running WHERE id = $id;";
            restart.Parameters.AddWithValue("$running", AuditStatus.Running);
            restart.Parameters.AddWithValue("$id", job.Id);
            (await restart.ExecuteNonQueryAsync()).ShouldBe(1, "the job is running again, as after a worker restart before its card");
        }

        await Worker(store, again, notifier, new FakePullRequests(() => At(Head, 1))).RunReviewAsync((await store.JobAsync(job.Id, CancellationToken.None))!, CancellationToken.None);

        again.Contests.ShouldBeEmpty("a finding that already has an outcome is not read again");
        var card = Card(notifier.Posts.Last().Text);
        Ids(card).ShouldBe(["r3", "r2"], "the recorded outcomes still decide the card: r1 refuted, r2 unconfirmed after the blocking r3");
        Notes(card).ShouldContain(n => n.StartsWith("1 finding refuted by Scout's second reading: Null lookup 1 dereferenced:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task N1_a_finding_that_cites_a_request_is_judged_against_the_request_the_second_reading_is_handed()
    {
        const string Description = "## What was asked\n\n- R2: A queue that does not exist is refused with one sentence and nothing is read.\n";
        var inputs = new ReviewInputs(new ReviewPurpose("Refuse unknown queues", Description), new ReviewRules([], [], Unreadable: false), new ReviewRemoved([], 0, 0, false));

        var (notifier, chat, _, _) = await ContestedAsync(
            request => Task.FromResult(request.Contains("- R2: A queue that does not exist is refused with one sentence and nothing is read.", StringComparison.Ordinal)
                ? Answer(("r1", "refuted"))
                : Answer(("r1", "uncertain"))),
            findings: 1,
            inputs: inputs,
            why: "R2 asks for the lookup to return 404, and the code refuses instead.");

        Ids(Card(notifier.Posts.Last().Text)).ShouldBeEmpty("a finding whose cited request the supplied request contradicts can be refuted");
        chat.Contests.ShouldHaveSingleItem().Request.ShouldContain(
            "What the pull request asks, from the title and description its author wrote:\nTitle: Refuse unknown queues\nThe requests its description lists:\n- R2: ", Case.Sensitive,
            "the second reading is handed the pull request's requests, once, read from its kept description");
    }

    [Fact]
    public async Task N2_N3_the_second_reading_is_handed_the_hunk_with_its_removed_lines_and_the_test_files_that_name_the_file()
    {
        var (_, chat, _, _) = await ContestedAsync(null, findings: 1, before: (_, _) =>
        {
            var tests = Path.Combine(_folder.Path, "snapshot", "tests", "M1.Tests");
            Directory.CreateDirectory(tests);
            File.WriteAllText(Path.Combine(tests, "QueueTests.cs"), "public class QueueTests\n{\n    [Fact] public void Refuses() => Guard.Refuse(\"x\");\n}\n");
            return Task.CompletedTask;
        });

        chat.Contests.ShouldHaveSingleItem().Request.ShouldSatisfyAllConditions(
            "the hunk that holds the finding's line, removed line included, and the test files that name a name its evidence calls",
            r => r.ShouldContain($"The change to this file, the hunk that holds the line:\n@@ -1,2 +1,2 @@\n var queue = Find(name);\n-{RemovedLine}\n+{RefusalEvidence}\n", Case.Sensitive),
            r => r.ShouldContain("Test files that name the file or a name its evidence calls:\nTest files that name it: tests/M1.Tests/QueueTests.cs\n--- tests/M1.Tests/QueueTests.cs:3\n", Case.Sensitive));
    }

    [Fact]
    public async Task N3_a_finding_no_test_file_names_says_so_and_a_snapshot_without_a_template_still_lists_no_request()
    {
        var (_, chat, _, _) = await ContestedAsync(null, findings: 1);

        chat.Contests.ShouldHaveSingleItem().Request.ShouldSatisfyAllConditions(
            "nothing is left silent",
            r => r.ShouldContain($"Test files that name the file or a name its evidence calls:\n{DefinitionReader.NoTestFile}", Case.Sensitive),
            r => r.ShouldContain("What the pull request asks is not known to this review: its title and description were not kept.", Case.Sensitive));
    }

    [Fact]
    public async Task N2_when_the_change_cannot_be_had_at_card_time_the_request_says_so()
    {
        var (_, _, store, job) = await ContestedAsync(_ => throw new HttpRequestException("reset"), findings: 1);
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_folder.Path, "audit.db")};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var restart = connection.CreateCommand();
            restart.CommandText = "UPDATE audit_job SET status = $running WHERE id = $id;";
            restart.Parameters.AddWithValue("$running", AuditStatus.Running);
            restart.Parameters.AddWithValue("$id", job.Id);
            (await restart.ExecuteNonQueryAsync()).ShouldBe(1);
        }

        var again = new ScriptedChat(Kernels.PresentingAnEmptyReview);
        var snapshots = new FakeSnapshots();
        snapshots.Taken.Add(new AuditSnapshot("acme", "shop", Head, Path.Combine(_folder.Path, "snapshot"), DateTimeOffset.UnixEpoch));
        await Worker(store, again, new RecordingNotifier(), new FakePullRequests(() => null), snapshots: snapshots).RunReviewAsync((await store.JobAsync(job.Id, CancellationToken.None))!, CancellationToken.None);

        again.Contests.ShouldHaveSingleItem().Request.ShouldContain(
            $"The change to this file, the hunk that holds the line:\n{ContestRequest.NoHunk}\n", Case.Sensitive, "a change that cannot be had is said, never left out");
    }

    private const string BigFile = "src/Big.cs";

    /// <summary>A diff larger than one batch, in hunks of twenty added lines.</summary>
    private static string BigDiff(int hunks = 60) =>
        string.Join('\n', Enumerable.Range(0, hunks).Select(h =>
            $"@@ -{1 + (h * 30)},20 +{1 + (h * 30)},20 @@\n" + string.Join('\n', Enumerable.Range(0, 20).Select(i => $"+line {h}.{i} {new string('a', 50)}"))));

    private static IReadOnlyList<PatchPart> PartsOfBig(string diff) =>
        RepositoryAudits.PartsOf(BigFile, diff, RepositoryAudits.Room(new AuditOptions().MaxBatchCharacters, null));

    /// <summary>A review planned with the big file in its parts, as the start plans it.</summary>
    private static async Task<AuditJob> PartsReviewAsync(SqliteAuditStore store, int of) =>
        (await store.JobAsync(
            await store.CreateJobAsync(
                "acme", "shop", 12, Head, "local:session",
                [.. Enumerable.Range(1, of).Select(k => new AuditBatchPlan(RepositoryAudits.PartArea(BigFile, k, of), [BigFile], 10))],
                null, CancellationToken.None),
            CancellationToken.None))!;

    [Fact]
    public async Task P2_P4_each_part_batch_finds_its_part_again_when_the_job_is_taken_up_and_a_finding_keeps_the_whole_file_s_line()
    {
        var diff = BigDiff();
        var parts = PartsOfBig(diff);
        var store = Store();
        var job = await PartsReviewAsync(store, parts.Count);
        await store.BeginBatchAsync(job.Id, 1, CancellationToken.None);
        await store.CompleteBatchAsync(job.Id, 1, [BigFile], [], null, CancellationToken.None);
        var (secondLine, secondText) = DiffLines.RightSide(parts[1].Patch)[5];
        var block = "```scout-findings\n" + JsonSerializer.Serialize(new
        {
            repository = "acme/shop",
            number = 12,
            commit = "abc1234",
            verdict = "needs rework",
            findings = new[] { new { id = "p2", severity = "fix", title = "In part two", path = BigFile, line = 1, why = "Why.", fix = "Fix.", evidence = secondText } },
        }) + "\n```";
        var chat = new ScriptedChat((history, kernel, _) =>
            history[1].Content!.Contains("part 2 of", StringComparison.Ordinal) ? Kernels.PresentReviewAsync(kernel, block) : Kernels.PresentingAnEmptyReview(history, kernel, 0));

        await Worker(store, chat, new RecordingNotifier(), new FakePullRequests(() => new ReviewPullRequest(Head, [new ReviewFile(BigFile, diff)]))).RunReviewAsync(job, CancellationToken.None);

        var requests = chat.Turns.Select(t => t.Request).ToList();
        requests.Count.ShouldBe(parts.Count - 1, "the part read before the job was taken up is not read again");
        for (var k = 2; k <= parts.Count; k++)
        {
            var lines = DiffLines.RightSide(parts[k - 1].Patch);
            var request = requests.Single(r => r.Contains($"part {k} of {parts.Count}", StringComparison.Ordinal));
            request.ShouldContain(AuditBatchRunner.PartSentence(BigFile, k, parts.Count, lines), Case.Sensitive, "the batch says which part it reads and which lines");
            request.ShouldContain(lines[0].Text, Case.Sensitive, "the batch is handed its own part");
            request.ShouldNotContain(DiffLines.RightSide(parts[0].Patch)[0].Text, Case.Sensitive, "and no line of another part");
        }

        (await store.FindingsAsync(job.Id, CancellationToken.None)).ShouldHaveSingleItem().Line.ShouldBe(
            secondLine, "the quote found in the part gives the whole file's line");
        AuditWorker.FilesRead(await store.BatchesAsync(job.Id, CancellationToken.None)).ShouldBe(1, "the file is read once every part was read");
    }

    [Fact]
    public async Task A_part_whose_file_s_diff_changed_size_before_its_wave_is_not_read_and_says_why()
    {
        var parts = PartsOfBig(BigDiff());
        var store = Store();
        var job = await PartsReviewAsync(store, parts.Count);
        var chat = new ScriptedChat(Kernels.PresentingAnEmptyReview);

        await Worker(store, chat, new RecordingNotifier(), new FakePullRequests(() => new ReviewPullRequest(Head, [new ReviewFile(BigFile, BigDiff(200))]))).RunReviewAsync(job, CancellationToken.None);

        (await store.BatchesAsync(job.Id, CancellationToken.None)).Select(b => (b.Status, b.Failure)).ShouldAllBe(
            b => b.Status == AuditStatus.NotCovered && b.Failure == RepositoryAudits.PartChanged,
            "a part is never read against a split it was not planned for");
        chat.Turns.ShouldBeEmpty();
    }

    [Fact]
    public void P3_P5_a_file_is_read_only_when_every_part_was_read_and_a_part_that_cannot_be_read_is_said_with_its_reason()
    {
        IReadOnlyList<AuditBatch> batches =
        [
            new(1, "src", ["src/A.cs"], AuditStatus.Done, ["src/A.cs"], null),
            new(2, RepositoryAudits.PartArea(BigFile, 1, 3), [BigFile], AuditStatus.Done, [BigFile], null),
            new(3, RepositoryAudits.PartArea(BigFile, 2, 3), [BigFile], AuditStatus.Done, [BigFile], null),
            new(4, RepositoryAudits.PartArea(BigFile, 3, 3), [BigFile], AuditStatus.NotCovered, [], "line 61 of the diff is 70,000 characters, more than a batch holds"),
        ];

        (AuditWorker.FilesRead(batches), AuditWorker.FilesChanged(batches)).ShouldBe((1, 2), "a partly read file is not read, and a file counts once however many parts it has");
        AuditWorker.NotRead(batches, stopped: false).ShouldBe(
            [
                "2 of 3 parts of src/Big.cs were read.",
                "Part 3 of src/Big.cs was not read: line 61 of the diff is 70,000 characters, more than a batch holds.",
            ],
            "the note says how many parts were read, and why a part could not be");
        AuditWorker.FilesRead([.. batches.Take(3), batches[3] with { Status = AuditStatus.Done, FilesRead = [BigFile], Failure = null }])
            .ShouldBe(2, "once every part was read, the file is read");
    }

    [Fact]
    public async Task F4_the_card_says_when_focused_readings_could_not_be_completed()
    {
        var store = Store();
        var job = await ReviewAsync(store, 1);
        var chat = new ScriptedChat((history, kernel, turn) => history[1].Content!.Contains(ReviewReadings.RemovedTask, StringComparison.Ordinal)
            ? throw new HttpRequestException("reset")
            : Kernels.PresentingAnEmptyReview(history, kernel, turn));
        var notifier = new RecordingNotifier();

        await Worker(store, chat, notifier, new FakePullRequests(() => new ReviewPullRequest(Head, [new ReviewFile("src/M1/F.cs", "@@ -1 +1 @@\n-old\n+line 1")])))
            .RunReviewAsync(job, CancellationToken.None);

        Notes(Card(notifier.Posts.Last().Text)).ShouldContain(
            "1 focused reading could not be completed; its findings may be missing.", "the card says a focused reading was not completed");
        AuditWorker.FocusedNote(3).ShouldBe("3 focused readings could not be completed; their findings may be missing.");
        AuditWorker.FocusedNote(0).ShouldBeNull("nothing is said when every focused reading was completed");
    }
}
