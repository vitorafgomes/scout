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
        ILogger<AuditWorker>? logger = null, int parallel = 1) =>
        new(store, new AuditBatchRunner(kernel ?? Kernels.Audit(), chat, skills, TimeSpan.FromMinutes(10), NullLogger<AuditBatchRunner>.Instance),
            notifier, new AuditSignal(), pullRequests, logger ?? NullLogger<AuditWorker>.Instance,
            Microsoft.Extensions.Options.Options.Create(new AuditOptions { ReviewParallelBatches = parallel }));

    private static string Block(params (string Id, string Severity, string Path)[] findings) =>
        "```scout-findings\n" + JsonSerializer.Serialize(new
        {
            repository = "acme/shop",
            number = 12,
            commit = "abc1234",
            verdict = "needs rework",
            findings = findings.Select(f => new { id = f.Id, severity = f.Severity, title = $"Title {f.Id}", path = f.Path, line = 3, why = "Why.", fix = "Fix." }),
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

    private static string[] Notes(JsonElement card) =>
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
        posts.Count.ShouldBe(4, "one progress line per batch and one card at the end");
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
    [InlineData("block", AuditWorker.ReadyAfterBlocking)]
    [InlineData("fix", AuditWorker.ReadyToMerge)]
    [InlineData("nit", AuditWorker.ReadyToMerge)]
    [InlineData("", AuditWorker.ReadyToMerge)]
    public async Task The_review_ends_with_one_block_with_the_verdict_from_severities_and_read_N_of_M(string severity, string verdict)
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
        card.GetProperty("read").GetString().ShouldBe("2 of 2 files read", "Scout's code computes the read N of M line");
        (card.GetProperty("coverage").GetProperty("read").GetInt32(), card.GetProperty("coverage").GetProperty("of").GetInt32()).ShouldBe((2, 2));
        card.GetProperty("findings").GetArrayLength().ShouldBe(severity.Length > 0 ? 1 : 0);
    }

    [Theory]
    [InlineData(false, true, false, AuditWorker.ReadyToMerge)]
    [InlineData(false, true, true, AuditWorker.ReadyToMerge)]
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
            .. Enumerable.Range(1, 30).Select(i => new AuditFinding($"n{i}", "nit", $"Nit {i}", "src/M1/F.cs", i, null, "Why.", "Fix.", null)),
            .. Enumerable.Range(1, 30).Select(i => new AuditFinding($"f{i}", "fix", $"Fix {i}", "src/M1/F.cs", i, null, "Why.", "Fix.", null)),
            new AuditFinding("b1", "block", "Block 1", "src/M1/F.cs", 1, null, "Why.", "Fix.", null),
        ], CancellationToken.None);
        var notifier = new RecordingNotifier();

        await Worker(store, new ScriptedChat(Kernels.PresentingAnEmptyReview), notifier, new FakePullRequests(() => At(Head, 1))).RunReviewAsync(job, CancellationToken.None);

        var card = Card(notifier.Posts.Last().Text);
        var ids = card.GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("id").GetString()).ToList();
        ids.Count.ShouldBe(AuditWorker.MaxCardFindings);
        ids.ShouldBe(["b1", .. Enumerable.Range(1, 30).Select(i => $"f{i}"), .. Enumerable.Range(1, 19).Select(i => $"n{i}")]);
        Notes(card).ShouldBe(["11 more findings are not on the card."]);
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

        Card(notifier.Posts.Last().Text).GetProperty("verdict").GetString().ShouldBe(AuditWorker.ReadyToMerge, "the next wake builds the card and posts it");
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
            .. Enumerable.Range(1, 40).Select(i => new AuditFinding($"f{i}", "fix", $"Fix {i}", "src/M1/F.cs", i, null, why, "Fix.", null)),
            .. Enumerable.Range(1, 10).Select(i => new AuditFinding($"b{i}", "block", $"Block {i}", "src/M1/F.cs", i, null, why, "Fix.", null)),
        ], CancellationToken.None);
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
            [.. Enumerable.Range(1, 120).Select(i => new AuditFinding($"f{i}", "fix", $"Title {i}", "src/M1/F.cs", i, null, "Why.", "Fix.", null))], CancellationToken.None);
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
            [.. Enumerable.Range(1, 8).Select(i => new AuditFinding($"f{i}", i <= 2 ? "block" : "fix", $"Finding {i}", "src/M1/F.cs", i, null, "Why.", "Fix.", null))], CancellationToken.None);
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
        await store.CompleteBatchAsync(job.Id, 1, ["src/M1/F.cs"], [new AuditFinding("rate", "block", "Tax rate doubled", "src/M1/F.cs", 3, null, "Why.", "Fix.", null)], CancellationToken.None);
        await store.StopAsync("acme", "shop", 12, CancellationToken.None);
        var notifier = new RecordingNotifier();

        await Worker(store, new ScriptedChat(Kernels.PresentingAnEmptyReview), notifier, new FakePullRequests(() => At(Head, 4))).RunReviewAsync(job, CancellationToken.None);

        var post = notifier.Posts.ShouldHaveSingleItem("a stopped review posts one card").Text;
        post.ShouldStartWith("Review of acme/shop#12 at abc1234, stopped at your request:\n\n```scout-findings\n", customMessage: "a stopped review posts the card of what was read under its own heading");
        var card = Card(post);
        Notes(card).ShouldBe(["3 files not read: the review was stopped."], "the card says how many files were not read because the review was stopped");
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
        await store.CompleteBatchAsync(job.Id, 1, ["src/M1/F.cs"], [], CancellationToken.None);
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
        var purpose = JsonSerializer.Serialize(Kept.Purpose, JsonSerializerOptions.Web);
        chat.Turns.Select(t => LineAfter(t.Request, AuditBatchRunner.PurposeIsData)).ShouldAllBe(
            line => line == purpose, "every batch, before and after the pause and the new worker, is handed the purpose kept at the start");
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
}
