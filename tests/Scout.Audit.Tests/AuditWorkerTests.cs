using System.Globalization;
using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Scout.Llm.Abstractions;

namespace Scout.Audit.Tests;

public sealed class AuditWorkerTests : IDisposable
{
    private static readonly AuditBatchPlan[] ThreeAreas =
    [
        new("src/Orders", ["src/Orders/Checkout.cs"], 10),
        new("src/Billing", ["src/Billing/Invoice.cs"], 10),
        new("tests", ["tests/CheckoutTests.cs"], 10),
    ];

    private static readonly string[] OrdersFiles = ["src/Orders/Checkout.cs", "src/Orders/Refund.cs"];

    private readonly TempFolder _folder = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _folder.Dispose();
    }

    private SqliteAuditStore Store() => new(Path.Combine(_folder.Path, "audit.db"), TimeProvider.System);

    private static AuditWorker Worker(IAuditJobStore store, ScriptedChat chat, RecordingNotifier notifier, AuditSignal? signal = null, Kernel? kernel = null) =>
        new(store, Kernels.Runner(kernel ?? Kernels.Audit(), chat), notifier, signal ?? new AuditSignal(), NullLogger<AuditWorker>.Instance);

    private static string AreaOf(string request) => request.Split('\n')[0];

    private static Func<Microsoft.SemanticKernel.ChatCompletion.ChatHistory, Kernel, int, Task> ReadingEveryFile(
        Func<string, string?>? blockFor = null) => async (history, kernel, _) =>
    {
        var request = history[1].Content!;
        foreach (var file in request.Split('\n').Where(l => l.StartsWith("- src/", StringComparison.Ordinal) || l.StartsWith("- tests/", StringComparison.Ordinal)))
        {
            await Kernels.ReadAsync(kernel, file[2..].TrimEnd('\r'));
        }

        if (blockFor?.Invoke(request) is { } block)
        {
            await Kernels.PresentAsync(kernel, block);
        }
    };

    [Fact]
    public async Task An_audit_covers_every_area_without_a_prompt_and_posts_the_findings_and_the_scope()
    {
        var store = Store();
        var job = await store.CreateJobAsync("acme", "shop", null, "abc1234def", "local:session", ThreeAreas, null, CancellationToken.None);
        var chat = new ScriptedChat(ReadingEveryFile(request => request.Contains("area src/Orders", StringComparison.Ordinal)
            ? Kernels.Block("needs rework", ("swallow", "Gateway errors read as no orders", "src/Orders/Checkout.cs"))
            : null));
        var notifier = new RecordingNotifier();

        await Worker(store, chat, notifier).RunAsync((await store.JobAsync(job, CancellationToken.None))!, CancellationToken.None);

        chat.Turns.Select(t => AreaOf(t.Request)).ShouldBe(
        [
            "Audit acme/shop at commit abc1234def, area src/Orders (batch 1 of 3).",
            "Audit acme/shop at commit abc1234def, area src/Billing (batch 2 of 3).",
            "Audit acme/shop at commit abc1234def, area tests (batch 3 of 3).",
        ]);
        (await store.JobAsync(job, CancellationToken.None))!.Status.ShouldBe(AuditStatus.Done);
        var posts = notifier.Posts.ToList();
        posts.Count.ShouldBe(2);
        posts.ShouldAllBe(p => p.Origin == "local:session");
        posts[0].Text.ShouldStartWith("Audit of acme/shop, src/Orders (1 of 3):\n\n```scout-findings\n");
        posts[0].Text.ShouldContain("\"id\":\"swallow\"");
        posts[0].Text.ShouldContain("\"read\":\"src/Orders: 1 of 1 files read\"");
        posts[1].Text.ShouldBe(
            "Audit of acme/shop at abc1234 is done. Verdict: sound after the High findings (1 High, 0 Medium, 0 Low).\n"
            + "Scope: 3 of 3 areas covered; 3 of 3 files read.\n"
            + "Every area was covered.");
    }

    [Fact]
    public async Task A_batch_whose_new_findings_pass_30000_characters_is_posted_as_several_complete_blocks_each_under_the_limit()
    {
        var store = Store();
        var job = await store.CreateJobAsync("acme", "shop", null, "abc1234def", "local:session", ThreeAreas[..1], null, CancellationToken.None);
        var findings = Enumerable.Range(1, 50)
            .Select(i => new { id = $"f{i}", severity = "fix", title = $"Finding {i}", path = "src/Orders/Checkout.cs", line = i, why = new string('w', 900), fix = "Fix." })
            .ToList();
        var block = "```scout-findings\n" + System.Text.Json.JsonSerializer.Serialize(new { kind = "audit", repository = "acme/shop", verdict = "needs rework", findings }) + "\n```";
        var notifier = new RecordingNotifier();

        await Worker(store, new ScriptedChat(ReadingEveryFile(_ => block)), notifier).RunAsync((await store.JobAsync(job, CancellationToken.None))!, CancellationToken.None);

        var posts = notifier.Posts.Select(p => p.Text).Where(t => t.Contains(FindingsBlock.Fence, StringComparison.Ordinal)).ToList();
        posts.Count.ShouldBeGreaterThanOrEqualTo(2);
        posts.ShouldAllBe(p => p.Length < AuditWorker.MaxPostLength);
        posts.ShouldAllBe(p => p.Contains("\"repository\":\"acme/shop\",\"commit\":\"abc1234def\",\"verdict\":\"needs rework\"", StringComparison.Ordinal));
        posts.ShouldAllBe(p => p.EndsWith("\n```", StringComparison.Ordinal));
        var parsed = posts.Select(p => FindingsBlock.Read([p[p.IndexOf(FindingsBlock.Fence, StringComparison.Ordinal)..]], "acme", "shop", "abc1234def")).ToList();
        parsed.ShouldAllBe(p => p.Verdict == "needs rework" && p.Findings.Count > 0);
        parsed.SelectMany(p => p.Findings).Select(f => f.Id).ShouldBe(findings.Select(f => f.id));
        posts[0].ShouldStartWith($"Audit of acme/shop, src/Orders (1 of 1), part 1 of {posts.Count}:\n\n");
    }

    [Fact]
    public async Task A_scope_longer_than_30000_characters_is_posted_as_several_pieces_each_under_the_limit_holding_every_area_once()
    {
        var store = Store();
        var plan = Enumerable.Range(1, 200)
            .Select(i => new AuditBatchPlan(string.Create(CultureInfo.InvariantCulture, $"src/{new string('a', 190)}{i:D3}"), [$"src/{i}.cs"], 10))
            .ToArray();
        var job = await store.CreateJobAsync("acme", "shop", null, "abc1234def", "local:session", plan, null, CancellationToken.None);
        var notifier = new RecordingNotifier();

        await Worker(store, new ScriptedChat((_, _, _) => throw new HttpRequestException("reset")), notifier).RunAsync((await store.JobAsync(job, CancellationToken.None))!, CancellationToken.None);

        var pieces = notifier.Posts.Select(p => p.Text).ToList();
        pieces.Count.ShouldBeGreaterThanOrEqualTo(2);
        pieces.ShouldAllBe(p => p.Length <= AuditWorker.MaxPostLength);
        pieces[0].ShouldStartWith("Audit of acme/shop at abc1234 is done. Verdict: incomplete (0 High, 0 Medium, 0 Low).\nScope: 0 of 200 areas covered; 0 of 200 files read.");
        var text = string.Join("; ", pieces);
        plan.ShouldAllBe(b => text.Split(b.Area, StringSplitOptions.None).Length == 2);
        pieces[^1].ShouldEndWith("(the AI model did not answer (HttpRequestException)).");
    }

    [Fact]
    public async Task The_earlier_titles_a_batch_is_told_are_only_those_of_its_own_files()
    {
        var store = Store();
        var job = await store.CreateJobAsync("acme", "shop", null, "abc1234def", "local:session", ThreeAreas, null, CancellationToken.None);
        await store.CompleteBatchAsync(job, 1, ["src/Orders/Checkout.cs"],
        [
            new AuditFinding("swallow", "block", "Gateway errors read as no orders", "src/Orders/Checkout.cs", 12, null, "Why.", "Fix.", null),
            new AuditFinding("paid", "block", "Invoice errors read as paid", "SRC/Billing/Invoice.cs", 12, null, "Why.", "Fix.", null),
        ], CancellationToken.None);
        var chat = new ScriptedChat(ReadingEveryFile());

        await Worker(store, chat, new RecordingNotifier()).RunAsync((await store.JobAsync(job, CancellationToken.None))!, CancellationToken.None);

        var billing = chat.Turns.ElementAt(0).Request;
        billing.ShouldContain("- Invoice errors read as paid");
        billing.ShouldNotContain("Gateway errors read as no orders");
        chat.Turns.ElementAt(1).Request.ShouldNotContain("Findings earlier batches already reported");
    }

    [Fact]
    public async Task A_finding_an_earlier_batch_reported_is_not_posted_again()
    {
        var store = Store();
        var job = await store.CreateJobAsync("acme", "shop", null, "abc1234def", "local:session", ThreeAreas, null, CancellationToken.None);
        await store.CompleteBatchAsync(job, 1, ["src/Orders/Checkout.cs"],
            [new AuditFinding("swallow", "block", "Gateway errors read as no orders", "src/Billing/Invoice.cs", 12, null, "Why.", "Fix.", null)],
            CancellationToken.None);
        var chat = new ScriptedChat(ReadingEveryFile(request => request.Contains("area src/Billing", StringComparison.Ordinal)
            ? Kernels.Block("needs rework", ("gateway", "GATEWAY ERRORS READ AS NO ORDERS", "SRC/billing/invoice.cs"), ("paid", "Invoice errors read as paid", "src/Billing/Invoice.cs"))
            : null));
        var notifier = new RecordingNotifier();

        await Worker(store, chat, notifier).RunAsync((await store.JobAsync(job, CancellationToken.None))!, CancellationToken.None);

        var blocks = notifier.Posts.Select(p => p.Text).Where(t => t.Contains("```scout-findings", StringComparison.Ordinal)).ToList();
        blocks.ShouldHaveSingleItem().ShouldContain("Invoice errors read as paid");
        blocks[0].ShouldNotContain("GATEWAY ERRORS READ AS NO ORDERS");
        (await store.FindingsAsync(job, CancellationToken.None)).Select(f => f.Title).ShouldBe(["Gateway errors read as no orders", "Invoice errors read as paid"]);
    }

    [Fact]
    public async Task A_quota_failure_at_batch_2_of_5_makes_one_provider_call_for_it_and_none_after()
    {
        var store = Store();
        var plan = Enumerable.Range(1, 5).Select(i => new AuditBatchPlan($"src/M{i}", [$"src/M{i}/F.cs"], 10)).ToArray();
        var job = await store.CreateJobAsync("acme", "shop", null, "abc1234def", "local:session", plan, null, CancellationToken.None);
        var chat = new ScriptedChat((history, _, _) => history[1].Content!.Contains("area src/M2 ", StringComparison.Ordinal)
            ? throw new LlmQuotaExceededException("usage_limit_reached")
            : Task.CompletedTask);

        await Worker(store, chat, new RecordingNotifier()).RunAsync((await store.JobAsync(job, CancellationToken.None))!, CancellationToken.None);

        chat.Turns.Select(t => AreaOf(t.Request)).ShouldBe(
        [
            "Audit acme/shop at commit abc1234def, area src/M1 (batch 1 of 5).",
            "Audit acme/shop at commit abc1234def, area src/M2 (batch 2 of 5).",
        ]);
        (await store.JobAsync(job, CancellationToken.None))!.Status.ShouldBe(AuditStatus.Paused);
    }

    [Theory]
    [InlineData(HttpStatusCode.PaymentRequired, "the AI provider's usage limit was reached")]
    [InlineData(HttpStatusCode.Unauthorized, "the AI Source rejected its key")]
    public async Task A_payment_required_or_rejected_key_failure_at_batch_2_of_5_pauses_the_audit_and_makes_no_call_after(HttpStatusCode status, string reason)
    {
        var store = Store();
        var plan = Enumerable.Range(1, 5).Select(i => new AuditBatchPlan($"src/M{i}", [$"src/M{i}/F.cs"], 10)).ToArray();
        var job = await store.CreateJobAsync("acme", "shop", null, "abc1234def", "local:session", plan, null, CancellationToken.None);
        var chat = new ScriptedChat((history, _, _) => history[1].Content!.Contains("area src/M2 ", StringComparison.Ordinal)
            ? throw new HttpOperationException(status, "{\"error\":{\"message\":\"refused\"}}", "refused", null)
            : Task.CompletedTask);

        await Worker(store, chat, new RecordingNotifier()).RunAsync((await store.JobAsync(job, CancellationToken.None))!, CancellationToken.None);

        chat.Turns.Select(t => AreaOf(t.Request)).ShouldBe(
        [
            "Audit acme/shop at commit abc1234def, area src/M1 (batch 1 of 5).",
            "Audit acme/shop at commit abc1234def, area src/M2 (batch 2 of 5).",
        ]);
        var paused = (await store.JobAsync(job, CancellationToken.None))!;
        (paused.Status, paused.PausedReason).ShouldBe((AuditStatus.Paused, reason));
    }

    [Fact]
    public async Task The_instructions_are_built_once_per_job_and_identical_in_every_batch()
    {
        var store = Store();
        var job = await store.CreateJobAsync("acme", "shop", null, "abc1234def", "local:session", ThreeAreas, null, CancellationToken.None);
        var reads = 0;
        var chat = new ScriptedChat(ReadingEveryFile());

        await Worker(store, chat, new RecordingNotifier(), kernel: Kernels.Audit(conventionsRead: () => reads++))
            .RunAsync((await store.JobAsync(job, CancellationToken.None))!, CancellationToken.None);

        reads.ShouldBe(1);
        chat.Turns.Count.ShouldBe(3);
        chat.Turns.Select(t => t.System).Distinct().ShouldHaveSingleItem().ShouldEndWith(Kernels.Conventions);
    }

    [Fact]
    public async Task A_file_longer_than_a_read_returns_is_named_as_partly_read_and_not_counted_as_read()
    {
        var store = Store();
        var job = await store.CreateJobAsync(
            "acme", "shop", null, "abc1234def", "local:session",
            [new AuditBatchPlan("src/Orders", ["src/Orders/Checkout.cs", "src/Orders/Big.cs"], 40_000, ["src/Orders/Big.cs"])],
            null, CancellationToken.None);
        var notifier = new RecordingNotifier();

        await Worker(store, new ScriptedChat(ReadingEveryFile()), notifier).RunAsync((await store.JobAsync(job, CancellationToken.None))!, CancellationToken.None);

        notifier.Posts.ShouldHaveSingleItem().Text.ShouldBe(
            "Audit of acme/shop at abc1234 is done. Verdict: sound (0 High, 0 Medium, 0 Low).\n"
            + "Scope: 1 of 1 areas covered; 1 of 2 files read.\n"
            + "Partly read (first 20,000 characters): src/Orders/Big.cs.\n"
            + "Every area was covered.");
    }

    [Fact]
    public async Task An_area_whose_files_were_not_all_read_is_not_counted_as_covered_and_its_unread_files_are_named()
    {
        var store = Store();
        var job = await store.CreateJobAsync(
            "acme", "shop", null, "abc1234def", "local:session",
            [new AuditBatchPlan("src/Orders", ["src/Orders/Checkout.cs", "src/Orders/Refund.cs", "src/Orders/Tax.cs"], 30)],
            null, CancellationToken.None);
        var chat = new ScriptedChat(async (_, kernel, _) =>
        {
            await Kernels.ReadAsync(kernel, "src/Orders/Checkout.cs");
            await Kernels.PresentAsync(kernel, Kernels.Block("needs rework", ("swallow", "Gateway errors read as no orders", "src/Orders/Checkout.cs")));
        });
        var notifier = new RecordingNotifier();

        await Worker(store, chat, notifier).RunAsync((await store.JobAsync(job, CancellationToken.None))!, CancellationToken.None);

        var posts = notifier.Posts.Select(p => p.Text).ToList();
        posts.Count.ShouldBe(2);
        posts[0].ShouldContain("\"id\":\"swallow\"");
        posts[1].ShouldBe(
            "Audit of acme/shop at abc1234 is done. Verdict: incomplete (1 High, 0 Medium, 0 Low).\n"
            + "Scope: 0 of 1 areas covered; 1 of 3 files read.\n"
            + "Not fully read: src/Orders (not read: src/Orders/Refund.cs, src/Orders/Tax.cs).");
    }

    [Fact]
    public async Task An_unread_batch_is_not_accepted_as_covered_and_every_file_it_did_not_read_is_named()
    {
        var store = Store();
        var job = await store.CreateJobAsync(
            "acme", "shop", null, "abc1234def", "local:session",
            [
                new AuditBatchPlan("src/Orders", ["src/Orders/Checkout.cs", "src/Orders/Refund.cs"], 20),
                new AuditBatchPlan("src/Billing", ["src/Billing/Invoice.cs"], 10),
            ],
            null, CancellationToken.None);
        var chat = new ScriptedChat((history, kernel, turn) => history[1].Content!.Contains("area src/Orders", StringComparison.Ordinal)
            ? Task.CompletedTask
            : ReadingEveryFile()(history, kernel, turn));
        var notifier = new RecordingNotifier();

        await Worker(store, chat, notifier).RunAsync((await store.JobAsync(job, CancellationToken.None))!, CancellationToken.None);

        notifier.Posts.Select(p => p.Text).ShouldBe(
        [
            "Audit of acme/shop at abc1234 is done. Verdict: incomplete (0 High, 0 Medium, 0 Low).\n"
            + "Scope: 1 of 2 areas covered; 1 of 3 files read.\n"
            + "Not fully read: src/Orders (not read: src/Orders/Checkout.cs, src/Orders/Refund.cs).",
        ]);
    }

    [Fact]
    public async Task A_file_the_plugin_could_not_read_is_named_among_the_unread_files_in_the_final_post()
    {
        var store = Store();
        var job = await store.CreateJobAsync(
            "acme", "shop", null, "abc1234def", "local:session",
            [new AuditBatchPlan("src/Orders", ["src/Orders/Checkout.cs", "src/Orders/Refund.cs", "src/Orders/Tax.cs"], 30)],
            null, CancellationToken.None);
        var kernel = Kernels.Audit(read: path => path == "src/Orders/Refund.cs"
            ? Kernels.NotRead("src/Orders/Refund.cs not found in acme/shop at abc1234.")
            : Task.FromResult($"{path}: text"));
        var notifier = new RecordingNotifier();

        await Worker(store, new ScriptedChat(ReadingEveryFile()), notifier, kernel: kernel)
            .RunAsync((await store.JobAsync(job, CancellationToken.None))!, CancellationToken.None);

        notifier.Posts.Select(p => p.Text).ShouldBe(
        [
            "Audit of acme/shop at abc1234 is done. Verdict: incomplete (0 High, 0 Medium, 0 Low).\n"
            + "Scope: 0 of 1 areas covered; 2 of 3 files read.\n"
            + "Not fully read: src/Orders (not read: src/Orders/Refund.cs).",
        ]);
    }

    public static TheoryData<string[], bool, bool, string> FinalPosts => new()
    {
        {
            [], false, false,
            "Audit of acme/shop at abc1234 is done. Verdict: incomplete (0 High, 0 Medium, 0 Low).\n"
            + "Scope: 0 of 2 areas covered; 0 of 3 files read.\n"
            + "Not fully read: src/Orders (not read: src/Orders/Checkout.cs, src/Orders/Refund.cs); src/Billing (not read: src/Billing/Invoice.cs)."
        },
        {
            ["src/Billing/Invoice.cs"], false, false,
            "Audit of acme/shop at abc1234 is done. Verdict: incomplete (0 High, 0 Medium, 0 Low).\n"
            + "Scope: 1 of 2 areas covered; 1 of 3 files read.\n"
            + "Not fully read: src/Orders (not read: src/Orders/Checkout.cs, src/Orders/Refund.cs)."
        },
        {
            ["src/Orders/Checkout.cs", "src/Billing/Invoice.cs"], false, false,
            "Audit of acme/shop at abc1234 is done. Verdict: incomplete (0 High, 0 Medium, 0 Low).\n"
            + "Scope: 1 of 2 areas covered; 2 of 3 files read.\n"
            + "Not fully read: src/Orders (not read: src/Orders/Refund.cs)."
        },
        {
            ["src/Orders/Checkout.cs", "src/Billing/Invoice.cs"], true, false,
            "Audit of acme/shop at abc1234 is done. Verdict: incomplete (1 High, 0 Medium, 0 Low).\n"
            + "Scope: 1 of 2 areas covered; 2 of 3 files read.\n"
            + "Not fully read: src/Orders (not read: src/Orders/Refund.cs)."
        },
        {
            ["src/Orders/Checkout.cs", "src/Orders/Refund.cs", "src/Billing/Invoice.cs"], false, false,
            "Audit of acme/shop at abc1234 is done. Verdict: sound (0 High, 0 Medium, 0 Low).\n"
            + "Scope: 2 of 2 areas covered; 3 of 3 files read.\n"
            + "Every area was covered."
        },
        {
            ["src/Orders/Checkout.cs", "src/Orders/Refund.cs", "src/Billing/Invoice.cs"], true, false,
            "Audit of acme/shop at abc1234 is done. Verdict: sound after the High findings (1 High, 0 Medium, 0 Low).\n"
            + "Scope: 2 of 2 areas covered; 3 of 3 files read.\n"
            + "Every area was covered."
        },
        {
            ["src/Orders/Checkout.cs", "src/Orders/Refund.cs"], false, true,
            "Audit of acme/shop at abc1234 is done. Verdict: incomplete (0 High, 0 Medium, 0 Low).\n"
            + "Scope: 1 of 2 areas covered; 2 of 3 files read.\n"
            + "Not covered: src/Billing (the AI model did not answer (HttpRequestException))."
        },
        {
            ["src/Orders/Checkout.cs"], true, true,
            "Audit of acme/shop at abc1234 is done. Verdict: incomplete (1 High, 0 Medium, 0 Low).\n"
            + "Scope: 0 of 2 areas covered; 1 of 3 files read.\n"
            + "Not fully read: src/Orders (not read: src/Orders/Refund.cs).\n"
            + "Not covered: src/Billing (the AI model did not answer (HttpRequestException))."
        },
    };

    [Theory]
    [MemberData(nameof(FinalPosts))]
    public async Task A_clean_report_is_never_given_with_files_never_read(string[] read, bool highFinding, bool billingFails, string finalPost)
    {
        var store = Store();
        var job = await store.CreateJobAsync(
            "acme", "shop", null, "abc1234def", "local:session",
            [
                new AuditBatchPlan("src/Orders", OrdersFiles, 20),
                new AuditBatchPlan("src/Billing", ["src/Billing/Invoice.cs"], 10),
            ],
            null, CancellationToken.None);
        var chat = new ScriptedChat(async (history, kernel, _) =>
        {
            var request = history[1].Content!;
            var orders = request.Contains("area src/Orders", StringComparison.Ordinal);
            if (billingFails && !orders)
            {
                throw new HttpRequestException("reset");
            }

            foreach (var file in read.Where(file => request.Contains($"- {file}", StringComparison.Ordinal)))
            {
                await Kernels.ReadAsync(kernel, file);
            }

            if (highFinding && orders)
            {
                await Kernels.PresentAsync(kernel, Kernels.Block("sound", ("swallow", "Gateway errors read as no orders", "src/Orders/Checkout.cs")));
            }
        });
        var notifier = new RecordingNotifier();

        await Worker(store, chat, notifier).RunAsync((await store.JobAsync(job, CancellationToken.None))!, CancellationToken.None);

        var posts = notifier.Posts.Select(p => p.Text).ToList();
        posts.Count.ShouldBe(highFinding ? 2 : 1);
        posts[^1].ShouldBe(finalPost);
        chat.Turns.Count.ShouldBe(billingFails ? 3 : 2);
    }

    [Theory]
    [InlineData("sound", 0, "incomplete")]
    [InlineData("sound", 1, "incomplete")]
    [InlineData("needs rework", 1, "incomplete")]
    [InlineData("sound", 2, "sound")]
    [InlineData("needs rework", 2, "needs rework")]
    public async Task The_card_of_an_area_read_in_part_says_incomplete_whatever_verdict_the_model_gave(string modelsVerdict, int filesRead, string posted)
    {
        var store = Store();
        var job = await store.CreateJobAsync(
            "acme", "shop", null, "abc1234def", "local:session",
            [new AuditBatchPlan("src/Orders", OrdersFiles, 20)],
            null, CancellationToken.None);
        var chat = new ScriptedChat(async (_, kernel, _) =>
        {
            foreach (var file in OrdersFiles.Take(filesRead))
            {
                await Kernels.ReadAsync(kernel, file);
            }

            await Kernels.PresentAsync(kernel, Kernels.Block(modelsVerdict, ("swallow", "Gateway errors read as no orders", "src/Orders/Checkout.cs")));
        });
        var notifier = new RecordingNotifier();

        await Worker(store, chat, notifier).RunAsync((await store.JobAsync(job, CancellationToken.None))!, CancellationToken.None);

        notifier.Posts.First().Text.ShouldBe(
            "Audit of acme/shop, src/Orders (1 of 1):\n\n```scout-findings\n"
            + "{\"kind\":\"audit\",\"repository\":\"acme/shop\",\"commit\":\"abc1234def\","
            + string.Create(CultureInfo.InvariantCulture, $"\"verdict\":\"{posted}\",\"read\":\"src/Orders: {filesRead} of 2 files read\",")
            + "\"findings\":[{\"id\":\"swallow\",\"severity\":\"block\",\"title\":\"Gateway errors read as no orders\","
            + "\"path\":\"src/Orders/Checkout.cs\",\"line\":12,\"why\":\"Why.\",\"fix\":\"Fix.\"}]}\n```");
    }

    [Fact]
    public async Task A_failed_batch_is_reported_as_not_covered_and_the_audit_goes_on()
    {
        var store = Store();
        var job = await store.CreateJobAsync("acme", "shop", null, "abc1234def", "local:session", ThreeAreas, null, CancellationToken.None);
        var chat = new ScriptedChat(async (history, kernel, turn) =>
        {
            if (history[1].Content!.Contains("area src/Billing", StringComparison.Ordinal))
            {
                throw new HttpRequestException("reset");
            }

            await ReadingEveryFile()(history, kernel, turn);
        });
        var notifier = new RecordingNotifier();

        await Worker(store, chat, notifier).RunAsync((await store.JobAsync(job, CancellationToken.None))!, CancellationToken.None);

        chat.Turns.Count.ShouldBe(4);
        notifier.Posts.ShouldHaveSingleItem().Text.ShouldBe(
            "Audit of acme/shop at abc1234 is done. Verdict: incomplete (0 High, 0 Medium, 0 Low).\n"
            + "Scope: 2 of 3 areas covered; 2 of 3 files read.\n"
            + "Not covered: src/Billing (the AI model did not answer (HttpRequestException)).");
    }

    [Fact]
    public async Task An_audit_the_provider_limits_pauses_with_its_reason_keeps_the_batch_pending_and_resumes_there()
    {
        var store = Store();
        var job = await store.CreateJobAsync("acme", "shop", null, "abc1234def", "local:session", ThreeAreas, null, CancellationToken.None);
        var limited = true;
        var chat = new ScriptedChat(async (history, kernel, turn) =>
        {
            if (limited && history[1].Content!.Contains("area src/Billing", StringComparison.Ordinal))
            {
                throw new LlmQuotaExceededException("usage_limit_reached");
            }

            await ReadingEveryFile()(history, kernel, turn);
        });
        var notifier = new RecordingNotifier();
        var worker = Worker(store, chat, notifier);

        await worker.RunAsync((await store.JobAsync(job, CancellationToken.None))!, CancellationToken.None);

        var paused = (await store.JobAsync(job, CancellationToken.None))!;
        (paused.Status, paused.PausedReason).ShouldBe((AuditStatus.Paused, "the AI provider's usage limit was reached"));
        (await store.BatchesAsync(job, CancellationToken.None)).Select(b => b.Status).ShouldBe([AuditStatus.Done, AuditStatus.Pending, AuditStatus.Pending]);
        notifier.Posts.ShouldHaveSingleItem().Text.ShouldBe(
            "The audit of acme/shop paused: the AI provider's usage limit was reached. Ask Scout to continue the audit of acme/shop when the AI Source has capacity again.");
        chat.Turns.Count.ShouldBe(2);

        limited = false;
        await store.ResumeAsync(job, null, CancellationToken.None);
        await worker.RunAsync((await store.JobAsync(job, CancellationToken.None))!, CancellationToken.None);

        chat.Turns.Skip(2).Select(t => AreaOf(t.Request)).ShouldBe(
        [
            "Audit acme/shop at commit abc1234def, area src/Billing (batch 2 of 3).",
            "Audit acme/shop at commit abc1234def, area tests (batch 3 of 3).",
        ]);
        (await store.JobAsync(job, CancellationToken.None))!.Status.ShouldBe(AuditStatus.Done);
    }

    [Fact]
    public async Task The_audit_survives_a_restart_and_the_worker_resumes_it_from_the_first_batch_not_done()
    {
        var job = await Store().CreateJobAsync("acme", "shop", null, "abc1234def", "local:session", ThreeAreas, null, CancellationToken.None);
        await Store().CompleteBatchAsync(job, 1, ["src/Orders/Checkout.cs"], [], CancellationToken.None);
        SqliteConnection.ClearAllPools();
        var store = Store();
        var chat = new ScriptedChat(ReadingEveryFile());
        var notifier = new RecordingNotifier();
        using var worker = Worker(store, chat, notifier);

        await worker.StartAsync(CancellationToken.None);
        await Task.WhenAny(notifier.Done, Task.Delay(TimeSpan.FromSeconds(10)));
        await worker.StopAsync(CancellationToken.None);

        (await store.JobAsync(job, CancellationToken.None))!.Status.ShouldBe(AuditStatus.Done);
        chat.Turns.Select(t => AreaOf(t.Request)).ShouldBe(
        [
            "Audit acme/shop at commit abc1234def, area src/Billing (batch 2 of 3).",
            "Audit acme/shop at commit abc1234def, area tests (batch 3 of 3).",
        ]);
    }

    [Fact]
    public async Task An_unexpected_error_in_a_job_leaves_it_running_and_keeps_the_worker_alive()
    {
        var store = Store();
        var job = await store.CreateJobAsync("acme", "shop", null, "abc1234def", "local:session", ThreeAreas, null, CancellationToken.None);
        var logger = new ErrorLogger<AuditWorker>();
        AuditBatchRunner brokenRunner = null!;
        using var worker = new AuditWorker(store, brokenRunner, new RecordingNotifier(), new AuditSignal(), logger);

        await worker.StartAsync(CancellationToken.None);
        await Task.WhenAny(logger.Error, Task.Delay(TimeSpan.FromSeconds(10)));

        logger.Error.IsCompletedSuccessfully.ShouldBeTrue();
        (await logger.Error).ShouldBe("The audit worker stopped a job on an unexpected error (NullReferenceException).");
        worker.ExecuteTask!.IsCompleted.ShouldBeFalse();
        (await store.JobAsync(job, CancellationToken.None))!.Status.ShouldBe(AuditStatus.Running);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_post_to_the_conversation_that_fails_does_not_stop_the_audit()
    {
        var store = Store();
        var job = await store.CreateJobAsync("acme", "shop", null, "abc1234def", "local:session", ThreeAreas, null, CancellationToken.None);
        var chat = new ScriptedChat(ReadingEveryFile(_ => Kernels.Block("needs rework", ("swallow", "Gateway errors read as no orders", "src/Orders/Checkout.cs"))));

        await Worker(store, chat, new RecordingNotifier(throws: true)).RunAsync((await store.JobAsync(job, CancellationToken.None))!, CancellationToken.None);

        chat.Turns.Count.ShouldBe(3);
        (await store.JobAsync(job, CancellationToken.None))!.Status.ShouldBe(AuditStatus.Done);
        (await store.BatchesAsync(job, CancellationToken.None)).ShouldAllBe(b => b.Status == AuditStatus.Done);
    }

    private const string OwnerPaused = "The audit of acme/shop paused: you paused it. Ask Scout to continue the audit of acme/shop to resume it from the first area not done.";

    private const string OrdersCard = "Audit of acme/shop, src/Orders (1 of 3):";

    private static Func<Microsoft.SemanticKernel.ChatCompletion.ChatHistory, Kernel, int, Task> ReadingWithOneFindingInOrders() =>
        ReadingEveryFile(request => request.Contains("area src/Orders", StringComparison.Ordinal)
            ? Kernels.Block("needs rework", ("swallow", "Gateway errors read as no orders", "src/Orders/Checkout.cs"))
            : null);

    private static string FirstLine(string text) => text.Split('\n')[0];

    [Fact]
    public async Task Stopping_the_audit_ends_it_after_the_batch_in_progress_and_makes_no_further_call()
    {
        var store = Store();
        var job = await store.CreateJobAsync("acme", "shop", null, "abc1234def", "local:session", ThreeAreas, null, CancellationToken.None);
        var chat = new ScriptedChat(async (_, _, _) => await store.StopAsync("acme", "shop", null, CancellationToken.None));
        var notifier = new RecordingNotifier();

        await Worker(store, chat, notifier).RunAsync((await store.JobAsync(job, CancellationToken.None))!, CancellationToken.None);

        chat.Turns.Count.ShouldBe(1);
        notifier.Posts.ShouldHaveSingleItem().ShouldBe(("local:session", "The audit of acme/shop ended at your request: 1 of 3 areas done, 0 findings kept on the cards."));
        (await store.JobAsync(job, CancellationToken.None))!.Status.ShouldBe(AuditStatus.Stopped);
        (await store.BatchesAsync(job, CancellationToken.None)).Select(b => b.Status).ShouldBe([AuditStatus.Done, AuditStatus.Pending, AuditStatus.Pending]);
    }

    private static RepositoryAudits Owner(SqliteAuditStore store, string? origin = "local:session") =>
        new(store, new AuditSignal(), new AuditOptions(), new FakeSnapshots(), new FakeEnvironment(), () => Kernels.Audit(), () => origin, NullLogger<RepositoryAudits>.Instance);

    /// <summary>
    /// What the owner does while the audit runs, and every post that follows, in order, as the conversation it went to and
    /// its first line; each notice once.
    /// </summary>
    public static TheoryData<string, int, (string Origin, string FirstLine)[]> OwnerActions => new()
    {
        { "pause during an area", 1, [("local:session", OrdersCard), ("local:session", OwnerPaused)] },
        { "pause between areas", 1, [("local:session", OrdersCard), ("local:session", OwnerPaused)] },
        {
            "pause, then continue from another conversation before the area ends", 3,
            [("discord:1:2", OrdersCard), ("discord:1:2", "Audit of acme/shop at abc1234 is done. Verdict: sound after the High findings (1 High, 0 Medium, 0 Low).")]
        },
        { "pause, then the AI Source refuses the area", 1, [("local:session", OwnerPaused)] },
        { "stop during an area", 1, [("local:session", OrdersCard), ("local:session", "The audit of acme/shop ended at your request: 1 of 3 areas done, 1 findings kept on the cards.")] },
    };

    [Theory]
    [MemberData(nameof(OwnerActions))]
    public async Task What_the_owner_does_while_the_audit_runs_is_posted_once_to_the_conversation_the_job_holds_when_it_takes_effect(
        string action, int turns, (string Origin, string FirstLine)[] posts)
    {
        var store = Store();
        var job = await store.CreateJobAsync("acme", "shop", null, "abc1234def", "local:session", ThreeAreas, null, CancellationToken.None);
        var chat = new ScriptedChat(async (history, kernel, turn) =>
        {
            if (turn == 1)
            {
                switch (action)
                {
                    case "pause during an area":
                        await Owner(store).PauseAsync("acme", "shop", CancellationToken.None);
                        break;
                    case "pause, then continue from another conversation before the area ends":
                        await Owner(store).PauseAsync("acme", "shop", CancellationToken.None);
                        await Owner(store, "discord:1:2").StartAsync("acme", "shop", CancellationToken.None);
                        break;
                    case "pause, then the AI Source refuses the area":
                        await Owner(store).PauseAsync("acme", "shop", CancellationToken.None);
                        throw new LlmQuotaExceededException("usage_limit_reached");
                    case "stop during an area":
                        await Owner(store).StopAsync("acme", "shop", CancellationToken.None);
                        break;
                }
            }

            await ReadingWithOneFindingInOrders()(history, kernel, turn);
        });
        var notifier = new RecordingNotifier(onPost: async text =>
        {
            if (action == "pause between areas" && text.StartsWith(OrdersCard, StringComparison.Ordinal))
            {
                await Owner(store).PauseAsync("acme", "shop", CancellationToken.None);
            }
        });

        await Worker(store, chat, notifier).RunAsync((await store.JobAsync(job, CancellationToken.None))!, CancellationToken.None);

        chat.Turns.Count.ShouldBe(turns);
        notifier.Posts.Select(p => (p.Origin, FirstLine(p.Text))).ShouldBe(posts);
    }

    /// <summary>A control from Scout Desktop, which has no conversation of its own, and every post that follows it, each once.</summary>
    public static TheoryData<string, int, (string Origin, string FirstLine)[]> DesktopControls => new()
    {
        { "pause during an area", 1, [("local:session", OrdersCard), ("local:session", OwnerPaused)] },
        { "stop during an area", 1, [("local:session", OrdersCard), ("local:session", "The audit of acme/shop ended at your request: 1 of 3 areas done, 1 findings kept on the cards.")] },
        {
            "pause, then continue before the area ends", 3,
            [("local:session", OrdersCard), ("local:session", "Audit of acme/shop at abc1234 is done. Verdict: sound after the High findings (1 High, 0 Medium, 0 Low).")]
        },
    };

    [Theory]
    [MemberData(nameof(DesktopControls))]
    public async Task A_control_from_the_desktop_posts_the_same_notice_as_the_chats_to_the_conversation_the_job_was_started_from(
        string action, int turns, (string Origin, string FirstLine)[] posts)
    {
        var store = Store();
        var job = await store.CreateJobAsync("acme", "shop", null, "abc1234def", "local:session", ThreeAreas, null, CancellationToken.None);
        var desktop = Owner(store, origin: null);
        var chat = new ScriptedChat(async (history, kernel, turn) =>
        {
            if (turn == 1)
            {
                switch (action)
                {
                    case "pause during an area":
                        await desktop.PauseAsync("acme", "shop", CancellationToken.None);
                        break;
                    case "stop during an area":
                        await desktop.StopAsync("acme", "shop", CancellationToken.None);
                        break;
                    default:
                        await desktop.PauseAsync("acme", "shop", CancellationToken.None);
                        await desktop.ContinueAsync("acme", "shop", CancellationToken.None);
                        break;
                }
            }

            await ReadingWithOneFindingInOrders()(history, kernel, turn);
        });
        var notifier = new RecordingNotifier();

        await Worker(store, chat, notifier).RunAsync((await store.JobAsync(job, CancellationToken.None))!, CancellationToken.None);

        chat.Turns.Count.ShouldBe(turns);
        notifier.Posts.Select(p => (p.Origin, FirstLine(p.Text))).ShouldBe(posts);
    }

    [Fact]
    public async Task Pausing_the_audit_while_a_batch_runs_keeps_and_posts_that_batchs_findings_and_runs_no_further_batch_until_it_is_continued()
    {
        var store = Store();
        var job = await store.CreateJobAsync("acme", "shop", null, "abc1234def", "local:session", ThreeAreas, null, CancellationToken.None);
        string? told = null;
        var chat = new ScriptedChat(async (history, kernel, turn) =>
        {
            if (turn == 1)
            {
                told = await Owner(store).PauseAsync("acme", "shop", CancellationToken.None);
            }

            await ReadingWithOneFindingInOrders()(history, kernel, turn);
        });
        var notifier = new RecordingNotifier();
        var worker = Worker(store, chat, notifier);

        await worker.RunAsync((await store.JobAsync(job, CancellationToken.None))!, CancellationToken.None);

        told.ShouldBe("Paused the audit of acme/shop; an area in progress finishes first. The findings so far stay on the cards. 0 of 3 areas done, 0 not covered, 0 findings, 0 files read. Ask Scout to continue the audit of acme/shop to resume it from the first area not done.");
        chat.Turns.Select(t => AreaOf(t.Request)).ShouldBe(["Audit acme/shop at commit abc1234def, area src/Orders (batch 1 of 3)."]);
        var paused = (await store.JobAsync(job, CancellationToken.None))!;
        (paused.Status, paused.PausedReason).ShouldBe((AuditStatus.Paused, "you paused it"));
        (await store.BatchesAsync(job, CancellationToken.None)).Select(b => b.Status).ShouldBe([AuditStatus.Done, AuditStatus.Pending, AuditStatus.Pending]);
        (await store.FindingsAsync(job, CancellationToken.None)).Select(f => f.Id).ShouldBe(["swallow"]);
        notifier.Posts.Count.ShouldBe(2);
        notifier.Posts.First().Text.ShouldStartWith(OrdersCard + "\n\n```scout-findings\n");
        notifier.Posts.Last().Text.ShouldBe(OwnerPaused);

        await Owner(store).StartAsync("acme", "shop", CancellationToken.None);
        await worker.RunAsync((await store.JobAsync(job, CancellationToken.None))!, CancellationToken.None);

        chat.Turns.Skip(1).Select(t => AreaOf(t.Request)).ShouldBe(
        [
            "Audit acme/shop at commit abc1234def, area src/Billing (batch 2 of 3).",
            "Audit acme/shop at commit abc1234def, area tests (batch 3 of 3).",
        ]);
        (await store.JobAsync(job, CancellationToken.None))!.Status.ShouldBe(AuditStatus.Done);
        notifier.Posts.Count(p => p.Text == OwnerPaused).ShouldBe(1);
        notifier.Posts.Last().Text.ShouldBe(
            "Audit of acme/shop at abc1234 is done. Verdict: sound after the High findings (1 High, 0 Medium, 0 Low).\n"
            + "Scope: 3 of 3 areas covered; 3 of 3 files read.\n"
            + "Every area was covered.");
    }

    [Fact]
    public async Task Pausing_during_the_last_batch_leaves_nothing_pending_and_continuing_ends_the_audit_with_its_last_post()
    {
        var store = Store();
        var job = await store.CreateJobAsync("acme", "shop", null, "abc1234def", "local:session", ThreeAreas, null, CancellationToken.None);
        var chat = new ScriptedChat(async (history, kernel, turn) =>
        {
            if (turn == 3)
            {
                await Owner(store).PauseAsync("acme", "shop", CancellationToken.None);
            }

            await ReadingEveryFile()(history, kernel, turn);
        });
        var notifier = new RecordingNotifier();
        var worker = Worker(store, chat, notifier);

        await worker.RunAsync((await store.JobAsync(job, CancellationToken.None))!, CancellationToken.None);

        (await store.JobAsync(job, CancellationToken.None))!.Status.ShouldBe(AuditStatus.Paused);
        (await store.BatchesAsync(job, CancellationToken.None)).ShouldAllBe(b => b.Status == AuditStatus.Done);
        notifier.Posts.Select(p => p.Text).ShouldBe([OwnerPaused]);

        await Owner(store).StartAsync("acme", "shop", CancellationToken.None);
        await worker.RunAsync((await store.JobAsync(job, CancellationToken.None))!, CancellationToken.None);

        chat.Turns.Count.ShouldBe(3);
        (await store.JobAsync(job, CancellationToken.None))!.Status.ShouldBe(AuditStatus.Done);
        notifier.Posts.Select(p => p.Text).ShouldBe(
        [
            OwnerPaused,
            "Audit of acme/shop at abc1234 is done. Verdict: sound (0 High, 0 Medium, 0 Low).\n"
            + "Scope: 3 of 3 areas covered; 3 of 3 files read.\n"
            + "Every area was covered.",
        ]);
    }

    [Fact]
    public async Task The_AI_Source_refusing_a_batch_of_an_audit_the_owner_paused_keeps_the_owners_reason_the_batch_pending_and_posts_the_owners_notice_not_the_AI_Sources()
    {
        var store = Store();
        var job = await store.CreateJobAsync("acme", "shop", null, "abc1234def", "local:session", ThreeAreas, null, CancellationToken.None);
        var chat = new ScriptedChat(async (_, _, _) =>
        {
            await Owner(store).PauseAsync("acme", "shop", CancellationToken.None);
            throw new LlmQuotaExceededException("usage_limit_reached");
        });
        var notifier = new RecordingNotifier();

        await Worker(store, chat, notifier).RunAsync((await store.JobAsync(job, CancellationToken.None))!, CancellationToken.None);

        chat.Turns.Count.ShouldBe(1);
        var paused = (await store.JobAsync(job, CancellationToken.None))!;
        (paused.Status, paused.PausedReason).ShouldBe((AuditStatus.Paused, "you paused it"));
        (await store.BatchesAsync(job, CancellationToken.None)).Select(b => b.Status).ShouldBe([AuditStatus.Pending, AuditStatus.Pending, AuditStatus.Pending]);
        notifier.Posts.ShouldHaveSingleItem().ShouldBe(("local:session", OwnerPaused));
    }

    [Fact]
    public async Task A_pause_landing_after_the_last_area_but_before_the_finish_posts_no_done_until_continued_and_the_done_post_once()
    {
        var sqlite = Store();
        var job = await sqlite.CreateJobAsync("acme", "shop", null, "abc1234def", "local:session", ThreeAreas, null, CancellationToken.None);
        var racing = true;
        var store = new RacingStore(sqlite)
        {
            BeforeFinish = async () =>
            {
                if (racing)
                {
                    racing = false;
                    await Owner(sqlite).PauseAsync("acme", "shop", CancellationToken.None);
                }
            },
        };
        var notifier = new RecordingNotifier();
        var worker = Worker(store, new ScriptedChat(ReadingEveryFile()), notifier);

        await worker.RunAsync((await sqlite.JobAsync(job, CancellationToken.None))!, CancellationToken.None);

        (await sqlite.JobAsync(job, CancellationToken.None))!.Status.ShouldBe(AuditStatus.Paused);
        notifier.Posts.Select(p => p.Text).ShouldBe([OwnerPaused]);

        await Owner(sqlite).StartAsync("acme", "shop", CancellationToken.None);
        await worker.RunAsync((await sqlite.JobAsync(job, CancellationToken.None))!, CancellationToken.None);

        (await sqlite.JobAsync(job, CancellationToken.None))!.Status.ShouldBe(AuditStatus.Done);
        notifier.Posts.Select(p => FirstLine(p.Text)).ShouldBe([OwnerPaused, "Audit of acme/shop at abc1234 is done. Verdict: sound (0 High, 0 Medium, 0 Low)."]);
    }

    [Fact]
    public async Task A_stop_landing_after_the_last_area_but_before_the_finish_posts_the_stop_and_never_the_done()
    {
        var sqlite = Store();
        var job = await sqlite.CreateJobAsync("acme", "shop", null, "abc1234def", "local:session", ThreeAreas, null, CancellationToken.None);
        var store = new RacingStore(sqlite) { BeforeFinish = () => Owner(sqlite).StopAsync("acme", "shop", CancellationToken.None) };
        var notifier = new RecordingNotifier();

        await Worker(store, new ScriptedChat(ReadingEveryFile()), notifier).RunAsync((await sqlite.JobAsync(job, CancellationToken.None))!, CancellationToken.None);

        (await sqlite.JobAsync(job, CancellationToken.None))!.Status.ShouldBe(AuditStatus.Stopped);
        notifier.Posts.Select(p => p.Text).ShouldBe(["The audit of acme/shop ended at your request: 3 of 3 areas done, 0 findings kept on the cards."]);
    }

    [Theory]
    [InlineData("you paused it")]
    [InlineData("the AI provider's usage limit was reached")]
    public async Task A_restart_does_not_resume_a_paused_audit_whoever_paused_it(string reason)
    {
        var paused = await Store().CreateJobAsync("acme", "shop", null, "abc1234def", "local:session", ThreeAreas, null, CancellationToken.None);
        await Store().CompleteBatchAsync(paused, 1, ["src/Orders/Checkout.cs"], [], CancellationToken.None);
        await Store().PauseAsync(paused, reason, CancellationToken.None);
        await Store().CreateJobAsync("acme", "other", null, "abc1234def", "local:session", [new("src/Orders", ["src/Orders/Checkout.cs"], 10)], null, CancellationToken.None);
        SqliteConnection.ClearAllPools();
        var store = Store();
        var chat = new ScriptedChat(ReadingEveryFile());
        var notifier = new RecordingNotifier();
        using var worker = Worker(store, chat, notifier);

        await worker.StartAsync(CancellationToken.None);
        await Task.WhenAny(notifier.Done, Task.Delay(TimeSpan.FromSeconds(10)));
        await worker.StopAsync(CancellationToken.None);

        notifier.Done.IsCompletedSuccessfully.ShouldBeTrue("the running audit of acme/other ran to its end after the restart");
        chat.Turns.Select(t => AreaOf(t.Request)).ShouldBe(["Audit acme/other at commit abc1234def, area src/Orders (batch 1 of 1)."]);
        var kept = (await store.JobAsync(paused, CancellationToken.None))!;
        (kept.Status, kept.PausedReason).ShouldBe((AuditStatus.Paused, reason));
        (await store.BatchesAsync(paused, CancellationToken.None)).Select(b => b.Status).ShouldBe([AuditStatus.Done, AuditStatus.Pending, AuditStatus.Pending]);
    }

    [Fact]
    public async Task A_transient_worker_error_does_not_strand_a_running_audit_asking_to_start_it_runs_it_to_the_end()
    {
        var sqlite = Store();
        var job = await sqlite.CreateJobAsync("acme", "shop", null, "abc1234def", "local:session", ThreeAreas, null, CancellationToken.None);
        var failed = false;
        var store = new RacingStore(sqlite)
        {
            BeforeRunningJobs = () =>
            {
                if (!failed)
                {
                    failed = true;
                    throw new AuditStoreException("Scout could not read the audit database: database is locked.", new InvalidOperationException());
                }

                return Task.CompletedTask;
            },
        };
        var logger = new ErrorLogger<AuditWorker>();
        var signal = new AuditSignal();
        var chat = new ScriptedChat(ReadingEveryFile());
        var notifier = new RecordingNotifier();
        using var worker = new AuditWorker(store, Kernels.Runner(Kernels.Audit(), chat), notifier, signal, logger);
        var audits = new RepositoryAudits(sqlite, signal, new AuditOptions(), new FakeSnapshots(), new FakeEnvironment(), () => Kernels.Audit(), () => "local:session", NullLogger<RepositoryAudits>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await Task.WhenAny(logger.Error, Task.Delay(TimeSpan.FromSeconds(10)));
        var answer = await audits.StartAsync("acme", "shop", CancellationToken.None);
        await Task.WhenAny(notifier.Done, Task.Delay(TimeSpan.FromSeconds(10)));
        await worker.StopAsync(CancellationToken.None);

        logger.Error.IsCompletedSuccessfully.ShouldBeTrue("the worker logged the database error before the start was asked");
        (await logger.Error).ShouldBe("The audit worker could not use its database: Scout could not read the audit database: database is locked.");
        answer.ShouldStartWith("An audit of acme/shop is already running.");
        notifier.Done.IsCompletedSuccessfully.ShouldBeTrue("asking to start the running audit after the worker's error runs it to its end");
        (await sqlite.JobAsync(job, CancellationToken.None))!.Status.ShouldBe(AuditStatus.Done);
        chat.Turns.Count.ShouldBe(3);
    }

    [Fact]
    public async Task The_worker_leaves_a_running_review_alone_and_runs_the_audit_behind_it()
    {
        var review = await Store().CreateJobAsync("acme", "shop", 12, "abc1234def", "local:session", ThreeAreas, null, CancellationToken.None);
        var audit = await Store().CreateJobAsync("acme", "shop", null, "abc1234def", "local:session", [new("src/Orders", ["src/Orders/Checkout.cs"], 10)], null, CancellationToken.None);
        var store = Store();
        var chat = new ScriptedChat(ReadingEveryFile());
        var notifier = new RecordingNotifier();
        using var worker = Worker(store, chat, notifier);

        await worker.StartAsync(CancellationToken.None);
        await Task.WhenAny(notifier.Done, Task.Delay(TimeSpan.FromSeconds(10)));
        await worker.StopAsync(CancellationToken.None);

        notifier.Done.IsCompletedSuccessfully.ShouldBeTrue("the audit started after the review ran to its end");
        chat.Turns.Select(t => AreaOf(t.Request)).ShouldBe(["Audit acme/shop at commit abc1234def, area src/Orders (batch 1 of 1)."]);
        (await store.JobAsync(audit, CancellationToken.None))!.Status.ShouldBe(AuditStatus.Done);
        (await store.JobAsync(review, CancellationToken.None))!.Status.ShouldBe(AuditStatus.Running, "the worker leaves the review as it was");
        (await store.BatchesAsync(review, CancellationToken.None)).Select(b => b.Status).ShouldBe([AuditStatus.Pending, AuditStatus.Pending, AuditStatus.Pending]);
    }
}
