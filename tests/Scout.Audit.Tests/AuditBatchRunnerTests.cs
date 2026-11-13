using System.ClientModel;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Services;
using Scout.Llm.Abstractions;

namespace Scout.Audit.Tests;

public sealed class AuditBatchRunnerTests
{
    private static readonly AuditJob Job = new("job1", "acme", "shop", "abc1234def", "local:session", AuditStatus.Running);

    private static readonly AuditBatch Billing = new(2, "src/Billing", ["src/Billing/Invoice.cs", "src/Billing/Tax.cs"], AuditStatus.Pending, [], null);

    private static async Task<BatchOutcome> RunAsync(AuditBatchRunner runner, IReadOnlyList<string> titles, CancellationToken ct) =>
        await runner.RunAsync(Job, await runner.InstructionsAsync(Job, ct), Billing, 3, titles, ct);

    [Fact]
    public async Task A_batch_turn_gets_exactly_its_file_list_the_earlier_titles_and_the_closed_scope()
    {
        var chat = new ScriptedChat((_, _, _) => Task.CompletedTask);

        await RunAsync(Kernels.Runner(Kernels.Audit(), chat), ["Gateway errors read as no orders"], CancellationToken.None);

        var (system, request, _) = chat.Turns.ShouldHaveSingleItem();
        request.ShouldBe(
            "Audit acme/shop at commit abc1234def, area src/Billing (batch 2 of 3).\n"
            + "Read each file with get_file_contents(owner \"acme\", repo \"shop\", path, reference \"abc1234def\").\n"
            + AuditBatchRunner.ScopeSentence + "\n"
            + "- src/Billing/Invoice.cs\n"
            + "- src/Billing/Tax.cs\n"
            + "Findings earlier batches already reported; do not report them again:\n"
            + "- Gateway errors read as no orders\n"
            + "When you have findings, call present_audit_findings once with repository \"acme/shop\" and commit \"abc1234def\"; when there are none, answer that this area has no findings.",
            StringCompareShould.IgnoreLineEndings);
        system.ShouldStartWith("## Pass 2: the audit\nBugs.\n\n## How to answer\nCall present_audit_findings.");
        system.ShouldEndWith(Kernels.Conventions);
        system.ShouldNotContain("create_issue");
    }

    [Fact]
    public async Task A_batch_turn_offers_the_model_only_the_reads_the_project_search_and_present_audit_findings()
    {
        var chat = new ScriptedChat((_, _, _) => Task.CompletedTask);

        await RunAsync(Kernels.Runner(Kernels.Audit(), chat), [], CancellationToken.None);

        var offered = chat.Turns.ShouldHaveSingleItem().Kernel.Plugins.GetFunctionsMetadata().Select(f => $"{f.PluginName}.{f.Name}").Order();
        offered.ShouldBe(
        [
            "GitHub.get_file_contents", "GitHub.list_repository_files", "GitHub.present_audit_findings", "GitHub.search_code",
            "ProjectKnowledge.find_symbol", "ProjectKnowledge.search_project",
        ]);
    }

    [Fact]
    public async Task A_host_without_the_offered_functions_sends_no_function_choice_and_one_with_them_sends_automatic_choice()
    {
        var bare = new ScriptedChat((_, _, _) => Task.CompletedTask);
        var composed = new ScriptedChat((_, _, _) => Task.CompletedTask);

        await Kernels.Runner(new Kernel(), bare).RunAsync(Job, "instructions", Billing, 3, [], CancellationToken.None);
        await Kernels.Runner(Kernels.Audit(), composed).RunAsync(Job, "instructions", Billing, 3, [], CancellationToken.None);

        bare.Settings.ShouldHaveSingleItem().ShouldNotBeNull().FunctionChoiceBehavior.ShouldBeNull();
        composed.Settings.ShouldHaveSingleItem().ShouldNotBeNull().FunctionChoiceBehavior.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_call_to_a_function_the_batch_does_not_offer_is_logged_by_name_without_its_arguments()
    {
        var chat = new ScriptedChat((history, _, _) =>
        {
            history.Add(new ChatMessageContent(AuthorRole.Assistant,
            [
                new FunctionCallContent("create_issue", "GitHubActions", "call_1", new KernelArguments { ["title"] = "Secret title" }),
                new FunctionCallContent("get_file_contents", "GitHub", "call_2"),
            ]));
            return Task.CompletedTask;
        });
        var logger = new RecordingLogger<AuditBatchRunner>();
        var runner = new AuditBatchRunner(Kernels.Audit(), chat, null, TimeSpan.FromMinutes(10), logger);

        var outcome = await runner.RunAsync(Job, "instructions", Billing, 3, [], CancellationToken.None);

        outcome.Failure.ShouldBeNull();
        var (level, message) = logger.Entries.ShouldHaveSingleItem();
        level.ShouldBe(LogLevel.Warning);
        message.ShouldBe("Audit job1 batch 2: the model called GitHubActions.create_issue, which the batch does not offer; it did not run.");
        message.ShouldNotContain("Secret title");
    }

    [Fact]
    public async Task The_files_read_come_from_the_function_calls_and_the_findings_from_the_attached_block()
    {
        var chat = new ScriptedChat(async (_, kernel, _) =>
        {
            await Kernels.ReadAsync(kernel, "src/Billing/Invoice.cs");
            await Kernels.ReadAsync(kernel, "src/Billing/Invoice.cs");
            await Kernels.PresentAsync(kernel, Kernels.Block("needs rework", ("paid", "Invoice errors read as paid", "src/Billing/Invoice.cs")));
        });

        var outcome = await RunAsync(Kernels.Runner(Kernels.Audit(), chat), [], CancellationToken.None);

        outcome.FilesRead.ShouldBe(["src/Billing/Invoice.cs"]);
        outcome.Verdict.ShouldBe("needs rework");
        outcome.Findings.ShouldHaveSingleItem().ShouldBe(new AuditFinding("paid", "block", "Invoice errors read as paid", "src/Billing/Invoice.cs", 12, null, "Why.", "Fix.", null));
        outcome.Failure.ShouldBeNull();
    }

    [Theory]
    [InlineData("", "src/Billing/Invoice.cs,src/Billing/Tax.cs")]
    [InlineData("src/Billing/Invoice.cs", "src/Billing/Tax.cs")]
    [InlineData("src/Billing/Tax.cs", "src/Billing/Invoice.cs")]
    [InlineData("src/Billing/Invoice.cs,src/Billing/Tax.cs", "")]
    public async Task An_unread_batch_is_not_accepted_as_covered_its_outcome_names_every_file_the_model_did_not_read_and_keeps_its_findings(string read, string unread)
    {
        var chat = new ScriptedChat(async (_, kernel, _) =>
        {
            foreach (var file in read.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                await Kernels.ReadAsync(kernel, file);
            }

            await Kernels.PresentAsync(kernel, Kernels.Block("sound", ("paid", "Invoice errors read as paid", "src/Billing/Invoice.cs")));
        });

        var outcome = await RunAsync(Kernels.Runner(Kernels.Audit(), chat), [], CancellationToken.None);

        outcome.Unread.ShouldBe(unread.Split(',', StringSplitOptions.RemoveEmptyEntries));
        outcome.FilesRead.ShouldBe(read.Split(',', StringSplitOptions.RemoveEmptyEntries));
        (outcome.Failure, outcome.Pause).ShouldBe((null, false));
        outcome.Findings.Select(f => f.Id).ShouldBe(["paid"]);
    }

    [Fact]
    public async Task A_failed_batch_is_retried_once_then_reported_as_not_covered_with_the_provider_error_as_its_reason()
    {
        var chat = new ScriptedChat((_, _, _) => throw new HttpRequestException("reset"));

        var outcome = await RunAsync(Kernels.Runner(Kernels.Audit(), chat), [], CancellationToken.None);

        chat.Turns.Count.ShouldBe(2);
        outcome.Failure.ShouldBe("the AI model did not answer (HttpRequestException)");
        outcome.Pause.ShouldBeFalse();
        outcome.Findings.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("quota", "the AI provider's usage limit was reached")]
    [InlineData("rate", "the AI provider's rate limit was reached")]
    [InlineData("sign-in", "the AI provider needs the owner to sign in again")]
    [InlineData("http-429-quota", "the AI provider's usage limit was reached")]
    [InlineData("http-429", "the AI provider's rate limit was reached")]
    [InlineData("client-429-quota", "the AI provider's usage limit was reached")]
    [InlineData("credit", "the AI provider's account has run out of credit")]
    [InlineData("http-400-credit", "the AI provider's account has run out of credit")]
    [InlineData("model", "the AI Source does not have the model qwen2.5:3b")]
    [InlineData("http-404-model", "the AI Source does not have the model qwen2.5:3b")]
    public async Task A_batch_the_provider_refuses_for_its_limit_is_not_retried_and_asks_the_job_to_pause(string limit, string reason)
    {
        const string QuotaBody = """{"error":{"type":"insufficient_quota","code":"insufficient_quota"}}""";
        const string ModelNotFoundBody = """{"error":{"message":"model 'qwen2.5:3b' not found","type":"not_found_error","param":null,"code":null}}""";
        var chat = new ScriptedChat((_, _, _) => throw (limit switch
        {
            "quota" => new LlmQuotaExceededException("usage_limit_reached"),
            "rate" => new LlmRateLimitException("LLM rate limit (HTTP 429)."),
            "http-429-quota" => new HttpOperationException(HttpStatusCode.TooManyRequests, QuotaBody, "quota", null),
            "http-429" => new HttpOperationException(HttpStatusCode.TooManyRequests, string.Empty, "rate", null),
            "client-429-quota" => new ClientResultException(new Status429Response(QuotaBody)),
            "credit" => new LlmQuotaExceededException("credit_balance_too_low"),
            "http-400-credit" => new HttpOperationException(HttpStatusCode.BadRequest, """{"error":{"code":"credit_balance_too_low"}}""", "credit", null),
            "model" => new LlmModelNotFoundException("qwen2.5:3b"),
            "http-404-model" => new HttpOperationException(HttpStatusCode.NotFound, ModelNotFoundBody, "not found", null),
            _ => (Exception)new LlmSignInRequiredException(),
        }))
        {
            Attributes = new Dictionary<string, object?> { [AIServiceExtensions.ModelIdKey] = "qwen2.5:3b" },
        };

        var outcome = await RunAsync(Kernels.Runner(Kernels.Audit(), chat), [], CancellationToken.None);

        chat.Turns.Count.ShouldBe(1);
        (outcome.Pause, outcome.Failure).ShouldBe((true, reason));
        outcome.Findings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Only_the_batchs_own_files_read_from_the_jobs_repository_at_its_commit_count_as_read()
    {
        var chat = new ScriptedChat(async (_, kernel, _) =>
        {
            await Kernels.ReadAsync(kernel, "src/Billing/Invoice.cs");
            await Kernels.ReadAsync(kernel, "src/Orders/Checkout.cs");
            await kernel.InvokeAsync("GitHub", "get_file_contents", new KernelArguments { ["owner"] = "other", ["repo"] = "shop", ["path"] = "src/Billing/Tax.cs", ["reference"] = "abc1234" });
            await kernel.InvokeAsync("GitHub", "get_file_contents", new KernelArguments { ["owner"] = "acme", ["repo"] = "shop", ["path"] = "src/Billing/Tax.cs", ["reference"] = "main" });
        });

        var outcome = await RunAsync(Kernels.Runner(Kernels.Audit(), chat), [], CancellationToken.None);

        outcome.FilesRead.ShouldBe(["src/Billing/Invoice.cs"]);
    }

    [Fact]
    public async Task A_file_whose_read_failed_is_not_counted_as_read_when_the_model_carries_on()
    {
        var kernel = Kernels.Audit(read: path => path == "src/Billing/Tax.cs" ? throw new HttpRequestException("reset") : Task.FromResult($"{path}: text"));
        var chat = new ScriptedChat(async (_, turn, _) =>
        {
            await Kernels.ReadAsync(turn, "src/Billing/Invoice.cs");
            await Should.ThrowAsync<HttpRequestException>(() => Kernels.ReadAsync(turn, "src/Billing/Tax.cs"));
        });

        var outcome = await RunAsync(Kernels.Runner(kernel, chat), [], CancellationToken.None);

        outcome.Failure.ShouldBeNull();
        outcome.FilesRead.ShouldBe(["src/Billing/Invoice.cs"]);
    }

    [Theory]
    [InlineData("src/Billing/Tax.cs not found in acme/shop at abc1234.")]
    [InlineData("Could not reach GitHub. Try again later.")]
    public async Task A_file_the_plugin_could_not_read_is_not_counted_as_read_and_a_file_read_normally_is(string answer)
    {
        var kernel = Kernels.Audit(read: path => path == "src/Billing/Tax.cs" ? Kernels.NotRead(answer) : Task.FromResult($"{path}: text"));
        var chat = new ScriptedChat(async (_, turn, _) =>
        {
            await Kernels.ReadAsync(turn, "src/Billing/Tax.cs");
            await Kernels.ReadAsync(turn, "src/Billing/Invoice.cs");
        });

        var outcome = await RunAsync(Kernels.Runner(kernel, chat), [], CancellationToken.None);

        outcome.Failure.ShouldBeNull();
        outcome.FilesRead.ShouldBe(["src/Billing/Invoice.cs"]);
    }

    [Fact]
    public async Task Of_two_reads_at_once_only_the_one_the_plugin_could_not_read_is_left_uncounted()
    {
        var reading = 0;
        var bothReading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var kernel = Kernels.Audit(read: async path =>
        {
            if (Interlocked.Increment(ref reading) == 2)
            {
                bothReading.SetResult();
            }

            await bothReading.Task;
            return path == "src/Billing/Tax.cs" ? await Kernels.NotRead("Could not reach GitHub. Try again later.") : $"{path}: text";
        });
        var chat = new ScriptedChat((_, turn, _) => Task.WhenAll(
            Kernels.ReadAsync(turn, "src/Billing/Tax.cs"),
            Kernels.ReadAsync(turn, "src/Billing/Invoice.cs")));

        var outcome = await RunAsync(Kernels.Runner(kernel, chat), [], CancellationToken.None);

        reading.ShouldBe(2);
        outcome.FilesRead.ShouldBe(["src/Billing/Invoice.cs"]);
    }

    [Fact]
    public async Task A_read_that_marks_nothing_counts_as_before_whatever_its_answer_says()
    {
        var kernel = Kernels.Audit(read: path => Task.FromResult($"{path} not found in acme/shop at abc1234."));
        var chat = new ScriptedChat(async (_, turn, _) =>
        {
            await Kernels.ReadAsync(turn, "src/Billing/Invoice.cs");
            await Kernels.ReadAsync(turn, "src/Billing/Tax.cs");
        });

        var outcome = await RunAsync(Kernels.Runner(kernel, chat), [], CancellationToken.None);

        outcome.FilesRead.ShouldBe(["src/Billing/Invoice.cs", "src/Billing/Tax.cs"]);
    }

    [Theory]
    [InlineData("acme/shop", "abc1234def", "paid,tax", 1)]
    [InlineData("ACME/shop", "abc1234", "paid,tax", 1)]
    [InlineData("acme/shop", null, "paid,tax", 1)]
    [InlineData("other/shop", "abc1234def", "", 3)]
    [InlineData("acme/shop", "fff9999", "", 3)]
    [InlineData("acme/shop", "abc", "", 3)]
    public async Task Findings_outside_the_batchs_files_repository_or_commit_are_dropped_and_logged(string repository, string? commit, string kept, int dropped)
    {
        var chat = new ScriptedChat((_, kernel, _) => Kernels.PresentAsync(kernel, Kernels.BlockFor(
            repository, commit, "needs rework",
            ("paid", "Invoice errors read as paid", "src/Billing/Invoice.cs"),
            ("tax", "Tax rounded down", "SRC/billing/TAX.cs"),
            ("cart", "Cart total ignores tax", "src/Orders/Checkout.cs"))));
        var logger = new RecordingLogger<AuditBatchRunner>();
        var runner = new AuditBatchRunner(Kernels.Audit(), chat, null, TimeSpan.FromMinutes(10), logger);

        var outcome = await runner.RunAsync(Job, "instructions", Billing, 3, [], CancellationToken.None);

        outcome.Findings.Select(f => f.Id).ShouldBe(kept.Split(',', StringSplitOptions.RemoveEmptyEntries));
        outcome.Verdict.ShouldBe(kept.Length > 0 ? "needs rework" : null);
        var (level, message) = logger.Entries.ShouldHaveSingleItem();
        level.ShouldBe(LogLevel.Warning);
        message.ShouldBe($"Audit job1 batch 2 dropped {dropped} findings outside its files, its repository or its commit.");
    }

    [Fact]
    public async Task A_batch_whose_first_turn_fails_is_covered_by_the_retry()
    {
        var chat = new ScriptedChat(async (_, kernel, turn) =>
        {
            if (turn == 1)
            {
                throw new HttpRequestException("reset");
            }

            await Kernels.PresentAsync(kernel, Kernels.Block("sound", ("tax", "Tax rounded per line", "src/Billing/Tax.cs")));
        });

        var outcome = await RunAsync(Kernels.Runner(Kernels.Audit(), chat), [], CancellationToken.None);

        chat.Turns.Count.ShouldBe(2);
        outcome.Failure.ShouldBeNull();
        outcome.Findings.ShouldHaveSingleItem().Title.ShouldBe("Tax rounded per line");
    }

    [Fact]
    public async Task A_turn_that_takes_longer_than_the_time_limit_is_abandoned_as_not_answered_in_time_not_paused()
    {
        var chat = new ScriptedChat((_, _, _) => new TaskCompletionSource().Task);
        var runner = Kernels.Runner(Kernels.Audit(), chat, TimeSpan.FromMilliseconds(100));

        var run = RunAsync(runner, [], CancellationToken.None);
        await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10)));

        run.IsCompletedSuccessfully.ShouldBeTrue();
        var outcome = await run;
        (outcome.Failure, outcome.Pause).ShouldBe(("the AI model did not answer in time", false));
        chat.Turns.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_turn_cut_short_by_shutdown_is_not_a_failure_and_is_not_retried()
    {
        using var shutdown = new CancellationTokenSource();
        var chat = new ScriptedChat(async (_, _, _) =>
        {
            await shutdown.CancelAsync();
            shutdown.Token.ThrowIfCancellationRequested();
        });

        await Should.ThrowAsync<OperationCanceledException>(() => RunAsync(Kernels.Runner(Kernels.Audit(), chat), [], shutdown.Token));

        chat.Turns.Count.ShouldBe(1);
    }

    [Fact]
    public void The_pass_two_text_stops_before_the_opening_of_issues()
    {
        var body = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "skills", "code-audit.md"));

        var pass = AuditBatchRunner.PassTwo(body);

        pass.ShouldStartWith("## Pass 2: the audit, one dimension at a time");
        pass.ShouldContain("present_audit_findings(repository, verdict, findings_json, commit, read, known_json)");
        pass.ShouldNotContain("create_issue");
    }

    [Fact]
    public async Task A_retry_does_not_reuse_file_coverage_from_the_failed_turn()
    {
        var chat = new ScriptedChat(async (_, kernel, turn) =>
        {
            if (turn == 1)
            {
                await Kernels.ReadAsync(kernel, "src/Billing/Invoice.cs");
                throw new HttpRequestException("reset");
            }

            await Kernels.ReadAsync(kernel, "src/Billing/Tax.cs");
        });

        var outcome = await RunAsync(Kernels.Runner(Kernels.Audit(), chat), [], CancellationToken.None);

        chat.Turns.Count.ShouldBe(2);
        outcome.Failure.ShouldBeNull();
        outcome.FilesRead.ShouldBe(["src/Billing/Tax.cs"]);
        outcome.Unread.ShouldBe(["src/Billing/Invoice.cs"]);
    }
}
