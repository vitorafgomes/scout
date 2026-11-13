using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Services;
using Scout.Llm;
using Scout.Llm.Abstractions;
using Scout.Skills;

namespace Scout.Audit;

/// <summary>
/// What one batch turn left: the files the model read, the files of the batch it did not read on a turn that ended without
/// error (empty when the batch was read in full), the verdict and findings it presented, or why it failed; a failure that
/// is the provider's usage or rate limit, its sign-in, its rejected key or a model it does not have, pauses the job instead of leaving the batch not covered.
/// </summary>
internal sealed record BatchOutcome(
    IReadOnlyList<string> FilesRead, IReadOnlyList<string> Unread, string? Verdict, IReadOnlyList<AuditFinding> Findings, string? Failure, bool Pause = false);

/// <summary>
/// Runs one batch as one model turn with a closed scope, through the kernel's chat service: the code-audit skill's pass 2
/// and how-to-answer text and the project's conventions profile as the instructions, the batch's file list, the titles of
/// the findings so far and the sentence that the scope is this list and nothing else as the request. The model is offered
/// only the GitHub reads, the project search and <c>present_audit_findings</c>, on a copy of the kernel holding nothing
/// else (<see cref="KernelScope.Only"/>), so no write can be called, and a call to any other function is logged by name
/// and never runs. The files read come from the <c>get_file_contents</c> calls and the findings from the
/// block the turn attached. A turn that fails, or takes longer than the turn time limit, is run once more; a second failure
/// is the batch's reason for not being covered. A turn the provider refuses for its usage or rate limit or its sign-in is
/// not run again: the outcome asks the job to pause.
/// </summary>
internal sealed class AuditBatchRunner(Kernel kernel, IChatCompletionService chat, ISkillCatalog? skills, TimeSpan turnTimeout, ILogger<AuditBatchRunner> logger)
{
    internal const string GitHubPlugin = "GitHub";
    internal const string KnowledgePlugin = "ProjectKnowledge";
    internal const string ActionsPlugin = "GitHubActions";
    internal const string ReadFile = "get_file_contents";
    internal const string GetConventions = "get_project_conventions";
    internal const string ScopeSentence = "The scope is this list and nothing else: read these files, judge only these files, and report findings only in these files.";

    internal static readonly (string Plugin, string Function)[] Offered =
    [
        (GitHubPlugin, ReadFile),
        (GitHubPlugin, "search_code"),
        (GitHubPlugin, "list_repository_files"),
        (GitHubPlugin, "present_audit_findings"),
        (KnowledgePlugin, "search_project"),
        (KnowledgePlugin, "find_symbol"),
    ];

    /// <summary>Run <paramref name="batch"/> as one turn under <paramref name="instructions"/>, which a job builds once with <see cref="InstructionsAsync"/>.</summary>
    public async Task<BatchOutcome> RunAsync(AuditJob job, string instructions, AuditBatch batch, int batchCount, IReadOnlyList<string> titles, CancellationToken ct)
    {
        var request = Request(job, batch, batchCount, titles);
        var read = new FilesRead(job, batch);
        string? failure = null;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(turnTimeout);
                var blocks = await TurnAsync(job, batch, instructions, request, read, limit.Token).ConfigureAwait(false);
                var (verdict, presented, dropped) = FindingsBlock.Read(blocks, job.Owner, job.Repo, job.Commit);
                var findings = presented.Where(f => batch.Files.Contains(f.Path, StringComparer.OrdinalIgnoreCase)).ToList();
                dropped += presented.Count - findings.Count;
                if (dropped > 0)
                {
                    logger.LogWarning(
                        "Audit {JobId} batch {Order} dropped {Count} findings outside its files, its repository or its commit.", job.Id, batch.Order, dropped);
                }

                var paths = read.Paths;
                return new BatchOutcome(paths, Unread(batch.Files, paths), verdict, findings, null);
            }
            catch (Exception raw) when (!ct.IsCancellationRequested)
            {
                var ex = LlmFailures.Translate(raw, chat.GetModelId());
                if (ex is LlmQuotaExceededException or LlmRateLimitException or LlmSignInRequiredException or LlmKeyRejectedException or LlmModelNotFoundException)
                {
                    logger.LogWarning("Audit {JobId} batch {Order} hit the AI provider's limit ({ExceptionType}).", job.Id, batch.Order, ex.GetType().Name);
                    return new BatchOutcome(read.Paths, [], null, [], Reason(ex), Pause: true);
                }

                (failure, read) = (Reason(ex), attempt == 0 ? new FilesRead(job, batch) : read);
                logger.LogWarning(
                    "Audit {JobId} batch {Order} turn {Attempt} failed ({ExceptionType}).", job.Id, batch.Order, attempt + 1, ex.GetType().Name);
            }
        }

        return new BatchOutcome(read.Paths, [], null, [], failure);
    }

    /// <summary>The files of a batch that are not among the files read, in the batch's order.</summary>
    internal static IReadOnlyList<string> Unread(IReadOnlyList<string> files, IReadOnlyList<string> read) =>
        [.. files.Except(read, StringComparer.Ordinal)];

    internal static string Reason(Exception ex) => ex switch
    {
        LlmQuotaExceededException { Code: "credit_balance_too_low" } => "the AI provider's account has run out of credit",
        LlmQuotaExceededException => "the AI provider's usage limit was reached",
        LlmRateLimitException => "the AI provider's rate limit was reached",
        LlmSignInRequiredException => "the AI provider needs the owner to sign in again",
        LlmKeyRejectedException => "the AI Source rejected its key",
        LlmModelNotFoundException missing => $"the AI Source does not have the model {missing.Model}",
        OperationCanceledException or TimeoutException => "the AI model did not answer in time",
        _ => $"the AI model did not answer ({ex.GetType().Name})",
    };

    internal static string Request(AuditJob job, AuditBatch batch, int batchCount, IReadOnlyList<string> titles)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Audit {job.Owner}/{job.Repo} at commit {job.Commit}, area {batch.Area} (batch {batch.Order} of {batchCount}).");
        text.AppendLine(CultureInfo.InvariantCulture, $"Read each file with get_file_contents(owner \"{job.Owner}\", repo \"{job.Repo}\", path, reference \"{job.Commit}\").");
        text.AppendLine(ScopeSentence);
        foreach (var file in batch.Files)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- {file}");
        }

        if (titles.Count > 0)
        {
            text.AppendLine("Findings earlier batches already reported; do not report them again:");
            foreach (var title in titles)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"- {title}");
            }
        }

        text.Append(CultureInfo.InvariantCulture, $"When you have findings, call present_audit_findings once with repository \"{job.Owner}/{job.Repo}\" and commit \"{job.Commit}\"; when there are none, answer that this area has no findings.");
        return text.ToString();
    }

    /// <summary>The code-audit skill from its second pass up to the opening of issues, which the batch never does.</summary>
    internal static string PassTwo(string body)
    {
        var start = body.IndexOf("## Pass 2", StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        var end = body.IndexOf("## Opening the issues", start, StringComparison.Ordinal);
        return (end < 0 ? body[start..] : body[start..end]).Trim();
    }

    /// <summary>The system message of every batch of the job: the skill's pass 2 and the project's conventions profile.</summary>
    public async Task<string> InstructionsAsync(AuditJob job, CancellationToken ct)
    {
        var pass = skills?.Find("code-audit") is { } skill ? PassTwo(skill.Body) : string.Empty;
        var conventions = await ConventionsAsync(job, ct).ConfigureAwait(false);
        return $"{pass}\n\nThe project's conventions profile, the rules this audit judges against:\n{conventions}".Trim();
    }

    private async Task<string> ConventionsAsync(AuditJob job, CancellationToken ct)
    {
        if (!kernel.Plugins.TryGetFunction(ActionsPlugin, GetConventions, out var function))
        {
            return "None is kept; judge against what the files themselves show.";
        }

        try
        {
            var result = await kernel.InvokeAsync(function, new KernelArguments { ["owner"] = job.Owner, ["repo"] = job.Repo }, ct).ConfigureAwait(false);
            return result.GetValue<string>() ?? string.Empty;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Audit {JobId} could not read the conventions profile ({ExceptionType}).", job.Id, ex.GetType().Name);
            return "None could be read; judge against what the files themselves show.";
        }
    }

    private async Task<IReadOnlyList<string>> TurnAsync(AuditJob job, AuditBatch batch, string instructions, string request, FilesRead read, CancellationToken ct)
    {
        var offered = Offered.Select(o => $"{o.Plugin}.{o.Function}").ToList();
        var turn = KernelScope.Only(kernel, offered);
        turn.FunctionInvocationFilters.Add(read);
        var history = new ChatHistory();
        history.AddSystemMessage(instructions);
        history.AddUserMessage(request);
        var settings = new PromptExecutionSettings { FunctionChoiceBehavior = turn.Plugins.Count > 0 ? FunctionChoiceBehavior.Auto() : null };
        using var attachments = TurnAttachments.Begin();
        try
        {
            await chat.GetChatMessageContentAsync(history, settings, turn, ct).ConfigureAwait(false);
        }
        finally
        {
            foreach (var call in history.SelectMany(message => message.Items.OfType<FunctionCallContent>())
                .Select(call => call.PluginName is null ? call.FunctionName : $"{call.PluginName}.{call.FunctionName}")
                .Where(called => !KernelScope.IsDeclared(called, offered))
                .Distinct(StringComparer.Ordinal))
            {
                logger.LogWarning(
                    "Audit {JobId} batch {Order}: the model called {Function}, which the batch does not offer; it did not run.",
                    job.Id, batch.Order, call);
            }
        }

        return TurnAttachments.Take();
    }

    /// <summary>
    /// Records the path of every file of the batch the model read during a turn, from the job's repository at its commit
    /// (the full SHA or a prefix of at least seven characters), once the read returned the file's content; a read that
    /// threw, a read the function marked on <see cref="ReadOutcome"/> as not the content, or a read of another file,
    /// repository or commit, is not coverage.
    /// </summary>
    private sealed class FilesRead(AuditJob job, AuditBatch batch) : IFunctionInvocationFilter
    {
        private readonly List<string> _paths = [];

        public IReadOnlyList<string> Paths
        {
            get
            {
                lock (_paths)
                {
                    return [.. _paths];
                }
            }
        }

        public async Task OnFunctionInvocationAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
        {
            using var outcome = ReadOutcome.Begin();
            await next(context).ConfigureAwait(false);
            if (!outcome.NoContent
                && context.Function.Name == ReadFile
                && Argument(context, "path") is { Length: > 0 } file
                && batch.Files.Contains(file, StringComparer.Ordinal)
                && string.Equals(Argument(context, "owner"), job.Owner, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Argument(context, "repo"), job.Repo, StringComparison.OrdinalIgnoreCase)
                && Argument(context, "reference") is { Length: >= 7 } reference
                && job.Commit.StartsWith(reference, StringComparison.OrdinalIgnoreCase))
            {
                lock (_paths)
                {
                    if (!_paths.Contains(file, StringComparer.Ordinal))
                    {
                        _paths.Add(file);
                    }
                }
            }
        }

        private static string? Argument(FunctionInvocationContext context, string name) =>
            context.Arguments.TryGetValue(name, out var value) ? value?.ToString() : null;
    }

    /// <summary>How many times a batch turn is tried: a failure other than the AI provider's limits is run once more.</summary>
    internal const int Attempts = 2;
}
