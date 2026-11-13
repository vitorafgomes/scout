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

    private IHost AuditHost(ToolCallingModel model, AllowingGate gate, bool github = true, HttpMessageHandler? batchModel = null, string? dockerRuns = null) =>
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

                services.AddScoutHost(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
                services.ConfigureHttpClientDefaults(client => client.ConfigurePrimaryHttpMessageHandler(() => new GitHubApi()));
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

    private sealed class GitHubApi : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = path switch
            {
                "/repos/acme/shop" => """{"full_name":"acme/shop","default_branch":"main"}""",
                "/repos/acme/shop/commits/main" => $$"""{"sha":"{{Sha}}"}""",
                _ => null,
            };
            return Task.FromResult(body is null
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
