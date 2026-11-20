using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Scout.Audit;
using Scout.Conversation;
using Scout.Conversation.Abstractions;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.Ollama;
using Shouldly;
using Xunit;

namespace Scout.Host.Console.Tests;

public sealed class HostAuditTurnTests : IDisposable
{
    private const string Sha = "abc1234def5678abc1234def5678abc1234def56";

    private readonly string _folder = Directory.CreateTempSubdirectory("scout-host-audit-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    private string Snapshots => Path.Combine(_folder, "snapshots");

    private string Database => Path.Combine(_folder, "audit.db");

    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _gitHubPaths = new();

    private IHost AuditHost(ToolCallingModel model, IWriteGate gate, bool github = true, HttpMessageHandler? batchModel = null, string? dockerRuns = null, bool local = false) =>
        new HostBuilder()
            .ConfigureServices((_, services) =>
            {
                services.AddLogging();
                var settings = new Dictionary<string, string?>
                {
                    ["Scout:Llm:Provider"] = "Ollama",
                    ["Scout:Llm:Ollama:Endpoint"] = "http://localhost:11434",
                    ["Scout:Llm:Ollama:Chat:Model"] = "llama3.1",
                    ["Scout:Llm:Ollama:Embeddings:Model"] = "nomic-embed-text",
                    ["Scout:Plugins:GitHub:SnapshotsDirectory"] = Snapshots,
                    ["Scout:Audit:DbPath"] = Database,
                    ["Scout:Sandbox:Enabled"] = dockerRuns,
                };
                if (github)
                {
                    settings["Scout:Plugins:GitHub:Token"] = "github-dummy-not-real";
                }

                if (local)
                {
                    settings["Scout:Adapters:Local:Enabled"] = "true";
                }

                services.AddScoutHost(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
                services.ConfigureHttpClientDefaults(client => client.ConfigurePrimaryHttpMessageHandler(() => new GitHubApi(_gitHubPaths)));
                services.RemoveAll<ILlmProviderFactory>();
                services.AddSingleton<ILlmProviderFactory>(sp => new OllamaLlmProviderFactory(
                    sp.GetRequiredService<IOptions<LlmOptions>>(),
                    new HttpClient(batchModel ?? model)));
                services.RemoveAll<IWriteGate>();
                services.AddSingleton<IWriteGate>(gate);
            })
            .Build();

    private void Snapshot(params string[] files) => Snapshot(files, size: 4);

    private void Snapshot(string[] files, int size)
    {
        var root = Path.Combine(Snapshots, "acme__shop", Sha);
        foreach (var file in files)
        {
            var full = Path.Combine(root, file);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, size == 4 ? "text" : new string('x', size));
        }

        File.WriteAllText(
            root + ".json",
            $$"""{"owner":"acme","repo":"shop","reference":"main","sha":"{{Sha}}","isDefaultBranch":true,"root":"","takenAt":"2026-11-07T10:00:00+00:00","fileCount":{{files.Length}},"bytes":16}""");
    }

    [Fact]
    public async Task A_chat_turn_whose_model_calls_the_plugins_start_repository_audit_reaches_the_audit_job_through_the_write_gate_with_the_turns_origin()
    {
        Snapshot("README.md", "src/Orders/Checkout.cs", "src/Billing/Invoice.cs", "tests/CheckoutTests.cs");
        var model = new ToolCallingModel();
        var gate = new AllowingGate();
        using var host = AuditHost(model, gate);

        await host.Services.GetRequiredService<ConversationConductor>().HandleAsync(
            new ChatMessage("U1", "C1", "audit acme/shop", DateTimeOffset.UtcNow) { OriginReference = "local:session" },
            new FinalTextSink(),
            CancellationToken.None);

        gate.Requests.ShouldBe(
        [
            "github | Scout's data folder | start_repository_audit(owner=acme, repo=shop)",
            "github | Scout's data folder | snapshot_repository(owner=acme, reference=, repo=shop)",
        ]);
        model.Bodies[1].ShouldContain("Auditing acme/shop: 3 areas, 4 files; the first batch is running. Findings arrive here as each area is done.");
        var job = (await host.Services.GetRequiredService<IAuditJobStore>().RunningJobsAsync(CancellationToken.None)).ShouldHaveSingleItem();
        (job.Commit, job.Origin).ShouldBe((Sha, "local:session"));
    }

    [Fact]
    public async Task A_batch_turn_on_ollama_reads_the_snapshot_presents_real_findings_and_is_offered_no_write()
    {
        Snapshot("README.md", "src/Orders/Checkout.cs");
        var model = new BatchModel(Sha);
        using var host = AuditHost(new ToolCallingModel(), new AllowingGate(), batchModel: model);
        var runner = host.Services.GetRequiredService<AuditBatchRunner>();

        var job = new AuditJob("job1", "acme", "shop", Sha, null, "running");
        var outcome = await runner.RunAsync(
            job,
            await runner.InstructionsAsync(job, CancellationToken.None),
            new AuditBatch(1, "src", ["src/Orders/Checkout.cs"], "pending", [], null),
            1,
            [],
            CancellationToken.None);

        outcome.Failure.ShouldBeNull();
        outcome.FilesRead.ShouldBe(["src/Orders/Checkout.cs"]);
        outcome.Verdict.ShouldBe("needs rework");
        outcome.Findings.ShouldHaveSingleItem().ShouldBe(new AuditFinding(
            "swallow", "block", "Gateway errors read as no orders", "src/Orders/Checkout.cs", 1, null, "The catch returns an empty list.", "Let it fail.", "Defect"));
        model.OfferedTools[0].Order().ShouldBe(
        [
            "GitHub_get_file_contents", "GitHub_list_repository_files", "GitHub_present_audit_findings", "GitHub_search_code",
            "ProjectKnowledge_find_symbol", "ProjectKnowledge_search_project",
        ]);
        model.ToolResults[0].ShouldContain("text");
    }

    [Fact]
    public async Task A_batch_turn_on_ollama_counts_the_file_the_plugin_read_and_not_the_one_it_did_not_find()
    {
        Snapshot("README.md", "src/Orders/Checkout.cs");
        var model = new BatchModel(Sha, "src/Orders/Refund.cs", "src/Orders/Checkout.cs");
        using var host = AuditHost(new ToolCallingModel(), new AllowingGate(), batchModel: model);
        var runner = host.Services.GetRequiredService<AuditBatchRunner>();

        var job = new AuditJob("job1", "acme", "shop", Sha, null, "running");
        var outcome = await runner.RunAsync(
            job,
            await runner.InstructionsAsync(job, CancellationToken.None),
            new AuditBatch(1, "src", ["src/Orders/Checkout.cs", "src/Orders/Refund.cs"], "pending", [], null),
            1,
            [],
            CancellationToken.None);

        outcome.Failure.ShouldBeNull();
        model.ToolResults[0].ShouldStartWith("src/Orders/Refund.cs not found in acme/shop at main");
        outcome.FilesRead.ShouldBe(["src/Orders/Checkout.cs"]);
    }

    [Fact]
    public async Task On_ollama_a_batch_turn_whose_model_calls_an_unoffered_write_never_runs_it_and_logs_it_by_name()
    {
        Snapshot("README.md", "src/Orders/Checkout.cs");
        var model = new UnofferedCallModel();
        var gate = new AllowingGate();
        using var host = AuditHost(new ToolCallingModel(), gate, batchModel: model);
        var logger = new WarningLogger();
        var runner = new AuditBatchRunner(
            host.Services.GetRequiredService<Kernel>(),
            host.Services.GetRequiredService<Microsoft.SemanticKernel.ChatCompletion.IChatCompletionService>(),
            null,
            TimeSpan.FromMinutes(1),
            logger);
        var job = new AuditJob("job1", "acme", "shop", Sha, null, "running");

        var outcome = await runner.RunAsync(job, "instructions", new AuditBatch(1, "src", ["src/Orders/Checkout.cs"], "pending", [], null), 1, [], CancellationToken.None);

        outcome.Failure.ShouldBeNull();
        gate.Requests.ShouldBeEmpty();
        model.ToolResults.ShouldHaveSingleItem().ShouldNotContain("issue #");
        var warning = logger.Warnings.ShouldHaveSingleItem();
        warning.ShouldBe("Audit job1 batch 1: the model called GitHubActions_create_issue, which the batch does not offer; it did not run.");
        warning.ShouldNotContain("Secret title");
    }

    [Fact]
    public async Task The_largest_request_of_a_full_batch_stays_under_300000_characters()
    {
        var files = Enumerable.Range(1, 40).Select(i => string.Create(CultureInfo.InvariantCulture, $"src/Orders/File{i:D2}.cs")).ToArray();
        Snapshot(files, size: 1_450);
        var model = new BatchModel(Sha, files);
        using var host = AuditHost(new ToolCallingModel(), new AllowingGate(), batchModel: model);
        var runner = host.Services.GetRequiredService<AuditBatchRunner>();
        var options = host.Services.GetRequiredService<IOptions<AuditOptions>>().Value;
        var batch = AuditPlanner.Plan(Path.Combine(Snapshots, "acme__shop", Sha), options.MaxBatchFiles, options.MaxBatchCharacters).ShouldHaveSingleItem();
        var job = new AuditJob("job1", "acme", "shop", Sha, null, "running");

        var outcome = await runner.RunAsync(
            job, await runner.InstructionsAsync(job, CancellationToken.None), new AuditBatch(1, batch.Area, batch.Files, "pending", [], null), 1, [], CancellationToken.None);

        outcome.FilesRead.Count.ShouldBe(40);
        model.RequestSizes.Count.ShouldBe(42);
        model.RequestSizes.Max().ShouldBeLessThan(300_000);
    }

    [Fact]
    public void With_GitHub_connected_and_the_desktops_local_chat_on_the_host_builds_its_kernel_and_its_chat_adapter()
    {
        using var host = AuditHost(new ToolCallingModel(), new AllowingGate(), local: true);

        host.Services.GetRequiredService<Kernel>().Plugins.Select(p => p.Name).ShouldContain("GitHubActions");
        host.Services.GetServices<IChatAdapter>().Select(a => a.Name).ShouldContain("Local");
    }

    [Fact]
    public async Task The_plugins_status_and_stop_functions_answer_from_the_audit_jobs_rows()
    {
        Snapshot("README.md", "src/Orders/Checkout.cs", "src/Billing/Invoice.cs", "tests/CheckoutTests.cs");
        var gate = new AllowingGate();
        using var host = AuditHost(new ToolCallingModel(), gate);
        var kernel = host.Services.GetRequiredService<Kernel>();
        var owner = new KernelArguments { ["owner"] = "acme", ["repo"] = "shop" };

        var started = await kernel.InvokeAsync<string>("GitHubActions", "start_repository_audit", owner);
        var status = await kernel.InvokeAsync<string>("GitHubActions", "get_repository_audit_status", owner);
        var stopped = await kernel.InvokeAsync<string>("GitHubActions", "stop_repository_audit", owner);
        var after = await kernel.InvokeAsync<string>("GitHubActions", "get_repository_audit_status", owner);

        started.ShouldBe("Auditing acme/shop: 3 areas, 4 files; the first batch is running. Findings arrive here as each area is done.");
        status.ShouldBe("The audit of acme/shop at abc1234 is running. 0 of 3 areas done, 0 not covered, 0 findings, 0 files read.");
        stopped.ShouldBe("Stopped the audit of acme/shop; an area in progress finishes first. The findings so far stay on the cards. 0 of 3 areas done, 0 not covered, 0 findings, 0 files read.");
        after.ShouldBe("The audit of acme/shop at abc1234 is stopped. 0 of 3 areas done, 0 not covered, 0 findings, 0 files read.");
        gate.Requests.Count(r => r.Contains("stop_repository_audit", StringComparison.Ordinal)).ShouldBe(1);
        gate.Requests.Count(r => r.Contains("get_repository_audit_status", StringComparison.Ordinal)).ShouldBe(0);
    }

    [Fact]
    public async Task The_plugins_review_start_and_status_reach_the_review_job_through_the_host_adapter_and_the_write_gate()
    {
        var gate = new AllowingGate();
        using var host = AuditHost(new ToolCallingModel(), gate);
        var kernel = host.Services.GetRequiredService<Kernel>();
        var pullRequest = new KernelArguments { ["owner"] = "acme", ["repo"] = "shop", ["number"] = 12 };

        var started = await kernel.InvokeAsync<string>("GitHubActions", "start_pull_request_review", pullRequest);
        var status = await kernel.InvokeAsync<string>("GitHubActions", "get_pull_request_review_status", pullRequest);

        host.Services.GetRequiredService<Scout.Plugin.GitHub.Abstractions.IPullRequestReviews>().ShouldBeOfType<PullRequestReviewsAdapter>();
        started.ShouldBe(
            "No review of acme/shop#12 was started: the pull request's answer left no diff out, so it is reviewed in this turn.",
            "the plugin's start reaches the audit library's review start through the host adapter, with the left-out count of get_pull_request");
        status.ShouldBe("No review of acme/shop#12 has been started.", "the plugin's status reaches the review job's rows through the host adapter");
        gate.Requests.ShouldBe(["github | Scout's data folder | start_pull_request_review(number=12, owner=acme, repo=shop)"], "the start is a declared write and the status a read");
    }

    [Fact]
    public async Task A_review_started_through_the_plugin_keeps_its_inputs_on_the_job()
    {
        Snapshot("AGENTS.md", "src/Orders/Checkout.cs");
        var gate = new AllowingGate();
        using var host = AuditHost(new ToolCallingModel(), gate);
        var kernel = host.Services.GetRequiredService<Kernel>();

        var started = await kernel.InvokeAsync<string>("GitHubActions", "start_pull_request_review", new KernelArguments { ["owner"] = "acme", ["repo"] = "shop", ["number"] = 16 });

        started.ShouldStartWith("review started:", Case.Sensitive, customMessage: "the review starts through the plugin, the host adapter and the write gate");
        var store = host.Services.GetRequiredService<IAuditJobStore>();
        var job = (await store.LatestJobAsync("acme", "shop", 16, CancellationToken.None)).ShouldNotBeNull();
        var inputs = (await store.InputsAsync(job.Id, CancellationToken.None)).ShouldNotBeNull("the job row keeps the inputs");
        inputs.Purpose.ShouldBe(
            new ReviewPurpose("Checkout total", "Fixes #7.", 7, "Totals are negative", "A refund makes the total negative."),
            "the title, the description and the linked issue read through the plugin reach the job");
        inputs.Rules.Files.Select(f => f.Path).ShouldBe(["AGENTS.md"], "the rule files are read from the snapshot at the head");
        inputs.Removed.Entries.Select(e => (e.Path, e.What, string.Join(",", e.StillPresent))).ShouldBe(
            [("tests/CheckoutTests.cs", RemovedEntry.TestFileDeleted, "src/Orders/Checkout.cs")], "the removed file's status crosses the host and the code it tested is found in the snapshot");
        gate.Requests.Count(r => r.Contains("start_pull_request_review", StringComparison.Ordinal)).ShouldBe(1, "the start is the one declared write");
        _gitHubPaths.Count(p => p == "/repos/acme/shop/issues/7").ShouldBe(1, "the linked issue is read once");
    }

    [Fact]
    public async Task A_review_start_the_write_gate_refuses_runs_nothing_and_answers_the_gates_reason()
    {
        const string Reason = "GitHub writes into Scout's data folder are not allowed: allow them on the Features screen of Scout Desktop.";
        using var host = AuditHost(new ToolCallingModel(), new RefusingGate(Reason));
        var kernel = host.Services.GetRequiredService<Kernel>();
        var pullRequest = new KernelArguments { ["owner"] = "acme", ["repo"] = "shop", ["number"] = 12 };

        var started = await kernel.InvokeAsync<string>("GitHubActions", "start_pull_request_review", pullRequest);
        var status = await kernel.InvokeAsync<string>("GitHubActions", "get_pull_request_review_status", pullRequest);

        started.ShouldBe(Reason, "a start the write gate refuses answers the gate's reason");
        _gitHubPaths.ShouldBeEmpty("a refused start reads nothing from GitHub");
        status.ShouldBe("No review of acme/shop#12 has been started.", "a refused start keeps no review");
    }

    [Fact]
    public async Task The_plugins_pause_function_pauses_the_audit_job_through_the_write_gate_and_a_start_continues_it()
    {
        Snapshot("README.md", "src/Orders/Checkout.cs", "src/Billing/Invoice.cs", "tests/CheckoutTests.cs");
        var gate = new AllowingGate();
        using var host = AuditHost(new ToolCallingModel(), gate);
        var kernel = host.Services.GetRequiredService<Kernel>();
        var owner = new KernelArguments { ["owner"] = "acme", ["repo"] = "shop" };

        await kernel.InvokeAsync<string>("GitHubActions", "start_repository_audit", owner);
        var paused = await kernel.InvokeAsync<string>("GitHubActions", "pause_repository_audit", owner);
        var status = await kernel.InvokeAsync<string>("GitHubActions", "get_repository_audit_status", owner);
        var continued = await kernel.InvokeAsync<string>("GitHubActions", "start_repository_audit", owner);

        paused.ShouldBe("Paused the audit of acme/shop; an area in progress finishes first. The findings so far stay on the cards. 0 of 3 areas done, 0 not covered, 0 findings, 0 files read. Ask Scout to continue the audit of acme/shop to resume it from the first area not done.");
        status.ShouldBe("The audit of acme/shop at abc1234 is paused: you paused it. 0 of 3 areas done, 0 not covered, 0 findings, 0 files read.");
        continued.ShouldBe("Resuming the audit of acme/shop from the first area not done. 0 of 3 areas done, 0 not covered, 0 findings, 0 files read. Findings arrive here as each area is done.");
        gate.Requests.ShouldContain("github | Scout's data folder | pause_repository_audit(owner=acme, repo=shop)");
        gate.Requests.Count(r => r.Contains("pause_repository_audit", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public void With_github_the_plugins_audits_and_the_audits_snapshots_are_the_host_adapters_and_the_worker_runs()
    {
        using var host = AuditHost(new ToolCallingModel(), new AllowingGate());

        host.Services.GetRequiredService<Kernel>().Plugins.GetFunctionsMetadata().ShouldContain(f => f.PluginName == "GitHubActions" && f.Name == "start_repository_audit");
        host.Services.GetServices<IHostedService>().ShouldContain(s => s is AuditWorker);
        host.Services.GetRequiredService<Scout.Plugin.GitHub.Abstractions.IRepositoryAudits>().ShouldBeOfType<RepositoryAuditsAdapter>();
        host.Services.GetRequiredService<IAuditSnapshots>().ShouldBeOfType<AuditSnapshotsAdapter>();
    }

    [Fact]
    public void Without_github_no_audit_is_composed()
    {
        using var host = AuditHost(new ToolCallingModel(), new AllowingGate(), github: false);

        host.Services.GetService<Scout.Plugin.GitHub.Abstractions.IRepositoryAudits>().ShouldBeNull();
        host.Services.GetService<IAuditSnapshots>().ShouldBeNull();
        host.Services.GetService<IReviewEnvironment>().ShouldBeNull();
        host.Services.GetServices<IHostedService>().ShouldNotContain(s => s is AuditWorker);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void A_reviews_environment_says_Docker_runs_are_on_only_when_Scout_Desktop_turned_them_on(string? enabled, bool on)
    {
        using var host = AuditHost(new ToolCallingModel(), new AllowingGate(), dockerRuns: enabled);

        host.Services.GetRequiredService<IReviewEnvironment>().ShouldBeOfType<ReviewEnvironmentAdapter>().ContainerRunsOn.ShouldBe(on, $"Scout:Sandbox:Enabled = {enabled ?? "(not set)"}");
    }

    [Fact]
    public async Task A_review_reads_its_pull_request_again_through_the_host_adapter_as_its_head_and_every_changed_files_patch()
    {
        using var host = AuditHost(new ToolCallingModel(), new AllowingGate());
        var pullRequests = host.Services.GetRequiredService<IReviewPullRequests>().ShouldBeOfType<ReviewPullRequestsAdapter>();

        var read = (await pullRequests.ReadAsync("acme", "shop", 12, CancellationToken.None)).ShouldNotBeNull();
        var missing = await pullRequests.ReadAsync("acme", "shop", 14, CancellationToken.None);

        read.HeadSha.ShouldBe(Sha, "the host adapter reads the pull request's head commit");
        read.Files.ShouldBe([new ReviewFile("src/A.cs", "@@ -1 +1 @@\n+a"), new ReviewFile("img.png", null)], "the host adapter reads every changed file with its patch, null when GitHub gave none");
        missing.ShouldBeNull("a pull request GitHub does not find is no read, which the worker pauses on");
    }

    [Fact]
    public async Task A_review_reads_its_pull_requests_head_alone_through_the_host_adapter_in_one_read()
    {
        using var host = AuditHost(new ToolCallingModel(), new AllowingGate());
        var pullRequests = host.Services.GetRequiredService<IReviewPullRequests>();

        var head = await pullRequests.HeadAsync("acme", "shop", 12, CancellationToken.None);
        var reads = _gitHubPaths.ToList();
        var missing = await pullRequests.HeadAsync("acme", "shop", 14, CancellationToken.None);

        head.ShouldBe(Sha, "the host adapter reads the pull request's head commit");
        reads.ShouldBe(["/repos/acme/shop/pulls/12"], "the head is one read, never the list of changed files");
        missing.ShouldBeNull("a pull request GitHub does not find has no head, which the worker pauses on");
    }

    [Fact]
    public async Task A_review_head_GitHub_answers_without_a_commit_is_no_head_for_the_worker_to_pause_on()
    {
        using var host = AuditHost(new ToolCallingModel(), new AllowingGate());

        var head = await host.Services.GetRequiredService<IReviewPullRequests>().HeadAsync("acme", "shop", 15, CancellationToken.None);

        head.ShouldBeNull("a pull request GitHub answers with no head commit has no head, which the worker pauses on");
    }

    [Fact]
    public async Task A_review_head_read_GitHub_answers_with_a_server_error_throws_for_the_worker_to_pause_on()
    {
        using var host = AuditHost(new ToolCallingModel(), new AllowingGate());

        var failure = await Should.ThrowAsync<Scout.Plugin.GitHub.GitHubApiException>(
            () => host.Services.GetRequiredService<IReviewPullRequests>().HeadAsync("acme", "shop", 13, CancellationToken.None),
            "a head read GitHub answers with a server error throws for the worker to pause on");

        failure.StatusCode.ShouldBe(HttpStatusCode.BadGateway, "the failure is GitHub's server error");
    }

    [Fact]
    public async Task A_review_re_read_GitHub_answers_with_a_server_error_throws_for_the_worker_to_pause_on()
    {
        using var host = AuditHost(new ToolCallingModel(), new AllowingGate());

        var failure = await Should.ThrowAsync<Scout.Plugin.GitHub.GitHubApiException>(
            () => host.Services.GetRequiredService<IReviewPullRequests>().ReadAsync("acme", "shop", 13, CancellationToken.None));

        (failure.StatusCode, failure.Message).ShouldBe((HttpStatusCode.BadGateway, "GitHub is currently unavailable. Try again later."), "a re-read GitHub fails throws for the worker to pause on");
    }

    [Fact]
    public async Task A_card_at_the_budget_is_accepted_by_the_real_present_review_findings()
    {
        using var host = AuditHost(new ToolCallingModel(), new AllowingGate());
        var job = new AuditJob("job", "acme", "shop", "0123456789abcdef0123456789abcdef01234567", "local:session", AuditStatus.Running, Kind: AuditKind.Review, PullRequest: 12);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
        var findings = new List<AuditFinding>();
        foreach (var i in Enumerable.Range(1, AuditWorker.MaxCardFindings))
        {
            var next = new AuditFinding($"f{i}", "fix", $"Finding {i} with a title of some length", "src/A.cs", i, null, new string('w', 700), "Apply the fix.", null);
            if (JsonSerializer.Serialize<List<AuditFinding>>([.. findings, next], options).Length > AuditWorker.MaxCardFindingsCharacters)
            {
                break;
            }

            findings.Add(next);
        }

        var json = JsonSerializer.Serialize(findings, options);
        var (block, answer) = await host.Services.GetRequiredService<AuditBatchRunner>().PresentReviewAsync(
            job, AuditWorker.NoBlockingInFilesRead, json, "1 of 2 files read", CancellationToken.None);

        json.Length.ShouldBeGreaterThan(AuditWorker.MaxCardFindingsCharacters - 1_000, "the findings fill the card's budget");
        block.ShouldNotBeNull($"the real present_review_findings accepts a card at the budget, answering: {answer}");
    }

    [Fact]
    public async Task A_card_at_the_budget_with_a_kept_test_run_still_reaches_the_owner_as_a_card()
    {
        using var host = AuditHost(new ToolCallingModel(), new AllowingGate());
        host.Services.GetRequiredService<Scout.Plugin.GitHub.TestRuns>().Keep("acme", "shop", Sha, new Scout.Plugin.GitHub.ReviewTests(
            true, null, "mcr.microsoft.com/dotnet/sdk@sha256:abc", "dotnet test", 1, 42.0, new string('o', 4_000), "Ran on the .NET 10 SDK."));
        var store = host.Services.GetRequiredService<IAuditJobStore>();
        var id = await store.CreateJobAsync("acme", "shop", 12, Sha, "local:session", [new AuditBatchPlan("src", ["src/A.cs"], 10)], null, CancellationToken.None);
        await store.CompleteBatchAsync(id, 1, ["src/A.cs"],
            [.. Enumerable.Range(1, AuditWorker.MaxCardFindings).Select(i => new AuditFinding($"f{i}", "fix", $"Finding {i} with a title of some length", "src/A.cs", i, null, new string('w', 700), "Apply the fix.", null))],
            CancellationToken.None);
        var posts = new List<string>();
        var notifier = NSubstitute.Substitute.For<IOriginNotifier>();
        NSubstitute.SubstituteExtensions.Returns(notifier.PostAsync(NSubstitute.Arg.Any<string>(), NSubstitute.Arg.Do<string>(posts.Add), NSubstitute.Arg.Any<CancellationToken>()), true);
        using var worker = new AuditWorker(
            store, host.Services.GetRequiredService<AuditBatchRunner>(), notifier, new AuditSignal(), NSubstitute.Substitute.For<IReviewPullRequests>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<AuditWorker>.Instance);

        await worker.RunReviewAsync((await store.JobAsync(id, CancellationToken.None))!, CancellationToken.None);

        posts.ShouldHaveSingleItem().ShouldContain(FindingsBlock.Fence, Case.Sensitive, "a card at the budget with a kept test run and its checks reaches the owner as a card, never the plain sentence");
    }

    [Fact]
    public async Task The_review_card_comes_from_the_real_present_review_findings_with_the_verdict_and_read_line_Scouts_code_gives()
    {
        using var host = AuditHost(new ToolCallingModel(), new AllowingGate());
        var job = new AuditJob("job", "acme", "shop", Sha, "local:session", AuditStatus.Running, Kind: AuditKind.Review, PullRequest: 12);

        var (block, answer) = await host.Services.GetRequiredService<AuditBatchRunner>().PresentReviewAsync(
            job, AuditWorker.ReadyToMerge, "[]", "1 of 2 files read", CancellationToken.None);

        answer.ShouldStartWith("Findings recorded: 0.", customMessage: "the real present_review_findings accepts the card Scout's code gives");
        var card = FindingsBlock.Complete(block.ShouldNotBeNull(), 1, 2, ["1 files not read: no diff was given for it."], AuditWorker.TestsNotKept).ShouldNotBeNull();
        using var json = JsonDocument.Parse(card[(card.IndexOf('\n', StringComparison.Ordinal) + 1)..card.LastIndexOf("```", StringComparison.Ordinal)]);
        var root = json.RootElement;
        root.TryGetProperty("kind", out _).ShouldBeFalse();
        (root.GetProperty("repository").GetString(), root.GetProperty("number").GetInt32(), root.GetProperty("commit").GetString()).ShouldBe(("acme/shop", 12, Sha));
        (root.GetProperty("verdict").GetString(), root.GetProperty("read").GetString()).ShouldBe(
            (AuditWorker.ReadyToMerge, "1 of 2 files read"), "the card carries the verdict and the read line Scout's code gives");
        (root.GetProperty("coverage").GetProperty("read").GetInt32(), root.GetProperty("coverage").GetProperty("of").GetInt32()).ShouldBe((1, 2));
        root.GetProperty("notes").EnumerateArray().Select(n => n.GetString()).ShouldBe(["1 files not read: no diff was given for it."]);
        (root.GetProperty("tests").GetProperty("ran").GetBoolean(), root.GetProperty("tests").GetProperty("reason").GetString()).ShouldBe((false, AuditWorker.TestsNotKept));
    }

    private sealed class GitHubApi(System.Collections.Concurrent.ConcurrentQueue<string> paths) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            paths.Enqueue(path);
            var body = path switch
            {
                "/repos/acme/shop" => """{"full_name":"acme/shop","default_branch":"main"}""",
                "/repos/acme/shop/commits/main" => $$"""{"sha":"{{Sha}}"}""",
                var head when head == $"/repos/acme/shop/commits/{Sha}" => $$"""{"sha":"{{Sha}}"}""",
                "/repos/acme/shop/pulls/12" => $$"""{"number":12,"title":"Checkout","user":{"login":"octo"},"head":{"ref":"feature","sha":"{{Sha}}"},"base":{"ref":"main"},"changed_files":2}""",
                "/repos/acme/shop/pulls/15" => """{"number":15,"title":"No head","user":{"login":"octo"},"head":{"ref":"feature"},"base":{"ref":"main"},"changed_files":0}""",
                var checks when checks == $"/repos/acme/shop/commits/{Sha}/check-runs" => "{\"total_count\":40,\"check_runs\":["
                    + string.Join(",", Enumerable.Range(1, 40).Select(i => $"{{\"name\":\"build and test on runner {i} with the full matrix\",\"status\":\"completed\",\"conclusion\":\"success\",\"html_url\":\"https://ci.example/runs/{i}\"}}"))
                    + "]}",
                var status when status == $"/repos/acme/shop/commits/{Sha}/status" => """{"statuses":[]}""",
                "/user" => """{"login":"scout-owner"}""",
                "/repos/acme/shop/pulls/16" => $$"""{"number":16,"title":"Checkout total","body":"Fixes #7.","user":{"login":"octo"},"head":{"ref":"feature","sha":"{{Sha}}"},"base":{"ref":"main"},"changed_files":2}""",
                "/repos/acme/shop/pulls/16/files" => "[{\"filename\":\"src/Orders/Checkout.cs\",\"status\":\"modified\",\"additions\":1,\"deletions\":0,\"patch\":\"@@ -1 +1 @@\\n+" + new string('a', 12_100) + "\"},{\"filename\":\"tests/CheckoutTests.cs\",\"status\":\"removed\",\"additions\":0,\"deletions\":3}]",
                "/repos/acme/shop/issues/7" => """{"number":7,"title":"Totals are negative","body":"A refund makes the total negative.","user":{"login":"acme-dev"},"state":"open","labels":[],"comments":0,"html_url":"https://github.com/acme/shop/issues/7"}""",
                "/repos/acme/shop/pulls/12/files" => """[{"filename":"src/A.cs","status":"modified","additions":1,"deletions":0,"patch":"@@ -1 +1 @@\n+a"},{"filename":"img.png","status":"added","additions":0,"deletions":0}]""",
                _ => null,
            };
            return Task.FromResult(path == "/repos/acme/shop/pulls/13"
                ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                : body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class BatchModel(string sha, params string[] reads) : HttpMessageHandler
    {
        private int _calls;

        public List<int> RequestSizes { get; } = [];

        public List<string[]> OfferedTools { get; } = [];

        public List<string> ToolResults { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/api/chat", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            var text = await request.Content!.ReadAsStringAsync(cancellationToken);
            RequestSizes.Add(text.Length);
            using var body = JsonDocument.Parse(text);
            OfferedTools.Add(body.RootElement.TryGetProperty("tools", out var tools)
                ? [.. tools.EnumerateArray().Select(t => t.GetProperty("function").GetProperty("name").GetString()!)]
                : []);
            ToolResults.AddRange(body.RootElement.GetProperty("messages").EnumerateArray()
                .Where(m => m.GetProperty("role").GetString() == "tool")
                .Select(m => OllamaWire.ToolResult(m.GetProperty("content").GetString()!)));
            var findings = """[{"id":"swallow","severity":"block","title":"Gateway errors read as no orders","path":"src/Orders/Checkout.cs","line":1,"context":"","why":"The catch returns an empty list.","fix":"Let it fail.","rule":"Defect"}]""";
            var files = reads.Length == 0 ? ["src/Orders/Checkout.cs"] : reads;
            var call = Interlocked.Increment(ref _calls);
            return OllamaWire.Reply(call <= files.Length
                ? OllamaWire.ToolCall("GitHub_get_file_contents", JsonSerializer.Serialize(new { owner = "acme", repo = "shop", path = files[call - 1], reference = sha }))
                : call == files.Length + 1
                    ? OllamaWire.ToolCall("GitHub_present_audit_findings", JsonSerializer.Serialize(new { repository = "acme/shop", verdict = "needs rework", findings_json = findings, commit = sha }))
                    : OllamaWire.Text("One finding."));
        }
    }

    private sealed class UnofferedCallModel : HttpMessageHandler
    {
        private int _calls;

        public List<string> ToolResults { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/api/chat", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            ToolResults.AddRange(body.RootElement.GetProperty("messages").EnumerateArray()
                .Where(m => m.GetProperty("role").GetString() == "tool")
                .Select(m => OllamaWire.ToolResult(m.GetProperty("content").GetString()!)));
            var arguments = JsonSerializer.Serialize(new { owner = "acme", repo = "shop", title = "Secret title", body = "b" });
            return OllamaWire.Reply(Interlocked.Increment(ref _calls) == 1
                ? OllamaWire.ToolCall("GitHubActions_create_issue", arguments)
                : OllamaWire.Text("No findings."));
        }
    }

    private sealed class WarningLogger : Microsoft.Extensions.Logging.ILogger<AuditBatchRunner>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    private sealed class ToolCallingModel : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/api/chat", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return OllamaWire.Streamed(Bodies.Count == 1
                ? OllamaWire.ToolCall("GitHubActions_start_repository_audit", """{"owner":"acme","repo":"shop"}""")
                : OllamaWire.Text("Started."));
        }
    }

    private sealed class RefusingGate(string reason) : IWriteGate
    {
        public Task<WriteGateDecision> RequestAsync(string featureId, string scope, string target, string summary, CancellationToken cancellationToken) =>
            Task.FromResult(new WriteGateDecision(WriteGateDecisionKind.Refused, reason, null));

        public Task RecordAsync(string featureId, string scope, string target, string outcome, string? actionId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class AllowingGate : IWriteGate
    {
        public List<string> Requests { get; } = [];

        public Task<WriteGateDecision> RequestAsync(string featureId, string scope, string target, string summary, CancellationToken cancellationToken)
        {
            Requests.Add($"{featureId} | {scope} | {summary}");
            return Task.FromResult(new WriteGateDecision(WriteGateDecisionKind.Allowed, string.Empty, null));
        }

        public Task RecordAsync(string featureId, string scope, string target, string outcome, string? actionId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FinalTextSink : IResponseSink
    {
        public RenderCapabilities Capabilities { get; } = new() { FineGrainedEdit = true, MaxBodyLength = 4096 };

        public Task BeginAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UpdateAsync(string cumulativeText, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CompleteAsync(string finalText, IReadOnlyList<Citation> citations, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task FailAsync(string finalTextSoFar, string reason, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
