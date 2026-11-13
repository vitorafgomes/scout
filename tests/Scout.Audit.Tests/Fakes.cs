using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Conversation.Abstractions;
using Scout.Llm.Abstractions;
using Scout.Skills;

namespace Scout.Audit.Tests;

internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("scout-audit-").FullName;

    public string File(string relative, int size = 10)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, new string('x', size));
        return full;
    }

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

internal sealed class ScriptedChat(Func<ChatHistory, Kernel, int, Task> turn) : IChatCompletionService
{
    public ConcurrentQueue<(string System, string Request, Kernel Kernel)> Turns { get; } = new();

    public ConcurrentQueue<PromptExecutionSettings?> Settings { get; } = new();

    public IReadOnlyDictionary<string, object?> Attributes { get; init; } = new Dictionary<string, object?>();

    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Turns.Enqueue((chatHistory[0].Content ?? string.Empty, chatHistory[1].Content ?? string.Empty, kernel!));
        Settings.Enqueue(executionSettings);
        await turn(chatHistory, kernel!, Turns.Count).WaitAsync(cancellationToken).ConfigureAwait(false);
        return [new ChatMessageContent(AuthorRole.Assistant, "done")];
    }

    public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

internal sealed class RecordingNotifier(bool succeeds = true, bool throws = false, Func<string, Task>? onPost = null) : IOriginNotifier
{
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConcurrentQueue<(string Origin, string Text)> Posts { get; } = new();

    public Task Done => _done.Task;

    public async Task<bool> PostAsync(string originReference, string text, CancellationToken ct)
    {
        if (throws)
        {
            throw new HttpRequestException("reset");
        }

        Posts.Enqueue((originReference, text));
        if (text.Contains(" is done. Verdict: ", StringComparison.Ordinal))
        {
            _done.TrySetResult();
        }

        if (onPost is not null)
        {
            await onPost(text);
        }

        return succeeds;
    }
}

/// <summary>The audit store, with a step run just before a pause, a finish or the read of the running jobs, where another caller's write or a failure can land.</summary>
internal sealed class RacingStore(SqliteAuditStore store) : IAuditJobStore
{
    public Func<Task>? BeforePause { get; init; }

    public Func<Task>? BeforeFinish { get; init; }

    public Func<Task>? BeforeRunningJobs { get; init; }

    public Func<Task>? BeforeCreate { get; init; }

    public async Task<string> CreateJobAsync(
        string owner, string repo, int? pullRequest, string commit, string? origin, IReadOnlyList<AuditBatchPlan> batches, ReviewPreflight? preflight, CancellationToken ct)
    {
        if (BeforeCreate is not null)
        {
            await BeforeCreate();
        }

        return await store.CreateJobAsync(owner, repo, pullRequest, commit, origin, batches, preflight, ct);
    }

    public async Task<IReadOnlyList<AuditJob>> RunningJobsAsync(CancellationToken ct)
    {
        if (BeforeRunningJobs is not null)
        {
            await BeforeRunningJobs();
        }

        return await store.RunningJobsAsync(ct);
    }

    public Task<AuditJob?> JobAsync(string id, CancellationToken ct) => store.JobAsync(id, ct);

    public Task<AuditJob?> LatestJobAsync(string owner, string repo, int? pullRequest, CancellationToken ct) => store.LatestJobAsync(owner, repo, pullRequest, ct);

    public Task<IReadOnlyList<AuditBatch>> BatchesAsync(string jobId, CancellationToken ct) => store.BatchesAsync(jobId, ct);

    public Task<(AuditBatch? Next, int Count)> NextBatchAsync(string jobId, CancellationToken ct) => store.NextBatchAsync(jobId, ct);

    public Task<IReadOnlyList<AuditFinding>> FindingsAsync(string jobId, CancellationToken ct) => store.FindingsAsync(jobId, ct);

    public Task BeginBatchAsync(string jobId, int order, CancellationToken ct) => store.BeginBatchAsync(jobId, order, ct);

    public Task<IReadOnlyList<AuditFinding>> CompleteBatchAsync(
        string jobId, int order, IReadOnlyList<string> filesRead, IReadOnlyList<AuditFinding> findings, CancellationToken ct) =>
        store.CompleteBatchAsync(jobId, order, filesRead, findings, ct);

    public Task NotCoveredAsync(string jobId, int order, IReadOnlyList<string> filesRead, string reason, CancellationToken ct) =>
        store.NotCoveredAsync(jobId, order, filesRead, reason, ct);

    public Task<bool> StopAsync(string owner, string repo, int? pullRequest, CancellationToken ct) => store.StopAsync(owner, repo, pullRequest, ct);

    public async Task<bool> PauseAsync(string jobId, string reason, CancellationToken ct)
    {
        if (BeforePause is not null)
        {
            await BeforePause();
        }

        return await store.PauseAsync(jobId, reason, ct);
    }

    public Task ResumeAsync(string jobId, string? origin, CancellationToken ct) => store.ResumeAsync(jobId, origin, ct);

    public async Task<bool> FinishAsync(string jobId, CancellationToken ct)
    {
        if (BeforeFinish is not null)
        {
            await BeforeFinish();
        }

        return await store.FinishAsync(jobId, ct);
    }
}

internal sealed class OneSkill(string body) : ISkillCatalog
{
    public IReadOnlyList<Skill> All() => [Find("code-audit")!];

    public Skill? Find(string name) => name == "code-audit" ? new Skill("code-audit", "audit", body, "code-audit.md") : null;
}

internal static class Kernels
{
    public const string Conventions = "Errors are results (src/Result.cs).";

    public static string Block(string verdict, params (string Id, string Title, string Path)[] findings) =>
        BlockFor("acme/shop", null, verdict, findings);

    public static string BlockFor(string repository, string? commit, string verdict, params (string Id, string Title, string Path)[] findings) =>
        "```scout-findings\n" + System.Text.Json.JsonSerializer.Serialize(new
        {
            kind = "audit",
            repository,
            commit,
            verdict,
            findings = findings.Select(f => new { id = f.Id, severity = "block", title = f.Title, path = f.Path, line = 12, why = "Why.", fix = "Fix." }),
        }) + "\n```";

    public static Kernel Audit(Func<KernelArguments, string>? snapshot = null, Action? conventionsRead = null, Func<string, Task<string>>? read = null)
    {
        var kernel = new Kernel();
        kernel.Plugins.Add(KernelPluginFactory.CreateFromFunctions("GitHub",
        [
            KernelFunctionFactory.CreateFromMethod((string owner, string repo, string path, string reference) => read?.Invoke(path) ?? Task.FromResult($"{path}: text"), "get_file_contents"),
            KernelFunctionFactory.CreateFromMethod((string query) => "no hits", "search_code"),
            KernelFunctionFactory.CreateFromMethod((string owner, string repo) => "files", "list_repository_files"),
            KernelFunctionFactory.CreateFromMethod((string block) => { TurnAttachments.Attach(block); return "Findings recorded."; }, "present_audit_findings"),
            KernelFunctionFactory.CreateFromMethod(() => "issues", "list_issues"),
        ]));
        kernel.Plugins.Add(KernelPluginFactory.CreateFromFunctions("GitHubActions",
        [
            KernelFunctionFactory.CreateFromMethod((string owner, string repo) =>
            {
                conventionsRead?.Invoke();
                return Conventions;
            }, "get_project_conventions"),
            KernelFunctionFactory.CreateFromMethod((KernelArguments arguments) => snapshot?.Invoke(arguments) ?? "refused", "snapshot_repository"),
            KernelFunctionFactory.CreateFromMethod((string title) => "Opened issue #1", "create_issue"),
        ]));
        kernel.Plugins.Add(KernelPluginFactory.CreateFromFunctions("ProjectKnowledge",
        [
            KernelFunctionFactory.CreateFromMethod((string project, string question) => "context", "search_project"),
            KernelFunctionFactory.CreateFromMethod((string project, string symbol) => "symbol", "find_symbol"),
            KernelFunctionFactory.CreateFromMethod(() => "projects", "list_projects"),
        ]));
        return kernel;
    }

    public static Task ReadAsync(Kernel kernel, string path) =>
        kernel.InvokeAsync("GitHub", "get_file_contents", new KernelArguments { ["owner"] = "acme", ["repo"] = "shop", ["path"] = path, ["reference"] = "abc1234" });

    public static Task<string> NotRead(string answer)
    {
        ReadOutcome.MarkNoContent();
        return Task.FromResult(answer);
    }

    public static Task PresentAsync(Kernel kernel, string block) =>
        kernel.InvokeAsync("GitHub", "present_audit_findings", new KernelArguments { ["block"] = block });

    public static AuditBatchRunner Runner(Kernel kernel, IChatCompletionService chat, TimeSpan? timeout = null) =>
        new(kernel, chat, new OneSkill(Skill), timeout ?? TimeSpan.FromMinutes(10), NullLogger<AuditBatchRunner>.Instance);

    private const string Skill = "## Pass 2: the audit\nBugs.\n\n## How to answer\nCall present_audit_findings.\n\n## Opening the issues\nCall create_issue.";
}

internal sealed class ErrorLogger<T> : ILogger<T>
{
    private readonly TaskCompletionSource<string> _error = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<string> Error => _error.Task;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.Error)
        {
            _error.TrySetResult(formatter(state, exception));
        }
    }
}

internal sealed class RecordingLogger<T> : ILogger<T>
{
    public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Enqueue((logLevel, formatter(state, exception)));
}

internal sealed class Status429Response(string body) : PipelineResponse
{
    public override int Status => 429;

    public override string ReasonPhrase => "Too Many Requests";

    public override Stream? ContentStream { get; set; }

    public override BinaryData Content => BinaryData.FromString(body);

    protected override PipelineResponseHeaders HeadersCore => throw new NotSupportedException();

    public override BinaryData BufferContent(CancellationToken cancellationToken = default) => Content;

    public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) => new(Content);

    public override void Dispose()
    {
    }
}

internal sealed class FakeSnapshots : IAuditSnapshots
{
    public List<AuditSnapshot> Taken { get; } = [];

    public List<string> Asked { get; } = [];

    public Exception? Failure { get; set; }

    public AuditSnapshot? Find(string owner, string repo, string commitOrShortSha)
    {
        Asked.Add($"{owner}/{repo}@{commitOrShortSha}");
        if (Failure is { } failure)
        {
            throw failure;
        }

        return Taken
            .Where(s => s.Owner == owner && s.Repo == repo && s.Commit.StartsWith(commitOrShortSha, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.TakenAt)
            .FirstOrDefault();
    }
}

internal sealed class FakeEnvironment : IReviewEnvironment
{
    public bool RunsOn { get; set; } = true;

    public Exception? Failure { get; set; }

    public bool ContainerRunsOn => Failure is null ? RunsOn : throw Failure;
}
